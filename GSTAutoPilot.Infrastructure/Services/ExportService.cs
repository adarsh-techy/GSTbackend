using ClosedXML.Excel;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class ExportService : IExportService
{
    private readonly IInvoiceService _invoiceService;
    private readonly IGstr3bService _gstr3bService;
    private readonly IReconService _reconService;
    private readonly ICompanyService _companyService;
    private readonly CarolERPDbContext _carol;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ExportService(
        IInvoiceService invoiceService,
        IGstr3bService gstr3bService,
        IReconService reconService,
        ICompanyService companyService,
        CarolERPDbContext carol,
        IHttpContextAccessor httpContextAccessor)
    {
        _invoiceService = invoiceService;
        _gstr3bService = gstr3bService;
        _reconService = reconService;
        _companyService = companyService;
        _carol = carol;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<ExportResult> ExportGstr1Async(string period, string? section = null, CancellationToken cancellationToken = default)
    {
        var (year, month) = ParsePeriod(period);
        var company = await LoadCompanyAsync(cancellationToken);
        var partyRows = await _invoiceService.GetGstr1SummaryAsync(year, month, cancellationToken);
        var invoiceRows = await _invoiceService.ListAsync(year, month, cancellationToken);

        var sec = (section ?? "all").Trim().ToLowerInvariant();
        using var wb = new XLWorkbook();
        string? fileTag;
        switch (sec)
        {
            case "summary":
                BuildGstr1SummarySheet(wb, company, period, partyRows);
                fileTag = "Summary";
                break;
            case "b2b":
                BuildGstr1DetailSheet(wb, company, period, FilterSection(invoiceRows, "B2B"), "B2B", "GSTR-1 · B2B Invoices");
                fileTag = "B2B";
                break;
            case "export":
            case "exports":
                BuildGstr1DetailSheet(wb, company, period, FilterSection(invoiceRows, "Export"), "Exports", "GSTR-1 · Export Invoices");
                fileTag = "Exports";
                break;
            case "b2c":
                BuildGstr1DetailSheet(wb, company, period, FilterSection(invoiceRows, "B2CL", "B2CS"), "B2C", "GSTR-1 · B2C Invoices");
                fileTag = "B2C";
                break;
            case "b2cl":
                BuildGstr1DetailSheet(wb, company, period, FilterSection(invoiceRows, "B2CL"), "B2CL", "GSTR-1 · B2C Large (inter-state > 2.5L)");
                fileTag = "B2CL";
                break;
            case "b2cs":
                BuildGstr1DetailSheet(wb, company, period, FilterSection(invoiceRows, "B2CS"), "B2CS", "GSTR-1 · B2C Small");
                fileTag = "B2CS";
                break;
            case "cdn":
                BuildGstr1DetailSheet(wb, company, period, FilterSection(invoiceRows, "CDN"), "Credit-Debit Notes", "GSTR-1 · Credit / Debit Notes");
                fileTag = "CDN";
                break;
            default: // full workbook
                BuildGstr1SummarySheet(wb, company, period, partyRows);
                BuildGstr1DetailSheet(wb, company, period, invoiceRows, "Invoice Detail", "GSTR-1 (Invoice Detail)");
                fileTag = null;
                break;
        }

        var bytes = ExcelHelpers.Save(wb);
        var gstinTag = ExcelHelpers.SafeFileFragment(GetTenantGstin());
        var name = fileTag is null ? $"GSTR1_{gstinTag}_{period}.xlsx" : $"GSTR1_{fileTag}_{gstinTag}_{period}.xlsx";
        return new ExportResult(bytes, name);
    }

    private static IReadOnlyList<InvoiceResponse> FilterSection(IReadOnlyList<InvoiceResponse> rows, params string[] sections)
        => rows.Where(r => sections.Contains(r.Section)).ToList();

    public async Task<ExportResult> ExportGstr3bAsync(string period, CancellationToken cancellationToken = default)
    {
        var (year, month) = ParsePeriod(period);
        var company = await LoadCompanyAsync(cancellationToken);
        var report = await _gstr3bService.ComputeAsync(year, month, cancellationToken);

        using var wb = new XLWorkbook();
        BuildGstr3bSheet(wb, company, period, report);

        var bytes = ExcelHelpers.Save(wb);
        var gstinTag = ExcelHelpers.SafeFileFragment(GetTenantGstin());
        return new ExportResult(bytes, $"GSTR3B_{gstinTag}_{period}.xlsx");
    }

    public async Task<ExportResult> ExportInvoicesAsync(string period, CancellationToken cancellationToken = default)
    {
        var (year, month) = ParsePeriod(period);
        var company = await LoadCompanyAsync(cancellationToken);
        var invoiceRows = await _invoiceService.ListAsync(year, month, cancellationToken);

        using var wb = new XLWorkbook();
        BuildInvoiceRegisterSheet(wb, company, period, invoiceRows);

        var bytes = ExcelHelpers.Save(wb);
        var gstinTag = ExcelHelpers.SafeFileFragment(GetTenantGstin());
        return new ExportResult(bytes, $"Invoices_{gstinTag}_{period}.xlsx");
    }

    public async Task<ExportResult> ExportReconAsync(string period, CancellationToken cancellationToken = default)
    {
        var company = await LoadCompanyAsync(cancellationToken);
        var report = await _reconService.GetResultsAsync(period, cancellationToken);

        using var wb = new XLWorkbook();
        BuildReconSheet(wb, company, period, report);

        var bytes = ExcelHelpers.Save(wb);
        var gstinTag = ExcelHelpers.SafeFileFragment(GetTenantGstin());
        return new ExportResult(bytes, $"Recon_{gstinTag}_{period}.xlsx");
    }

    // ---- Sheet builders -----------------------------------------------------

    private static void BuildGstr1SummarySheet(XLWorkbook wb, CompanyDto company, string period, IReadOnlyList<Gstr1SummaryRow> rows)
    {
        var ws = wb.Worksheets.Add("Summary");
        const int cols = 10;
        var headerRow = ExcelHelpers.AddCompanyHeader(ws, company, "GSTR-1 (Party Summary)", period, cols);

        string[] headers = { "Party Name", "Section", "Party GSTIN", "Invoice Count", "Taxable Amount", "IGST", "CGST", "SGST", "Total GST", "Invoice Value" };
        for (var i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        ExcelHelpers.StyleHeaderRow(ws.Range(headerRow, 1, headerRow, cols));

        var dataStart = headerRow + 1;
        var r = dataStart;
        decimal tTax = 0, tIgst = 0, tCgst = 0, tSgst = 0, tTotal = 0;
        int tCount = 0;
        foreach (var row in rows)
        {
            ws.Cell(r, 1).Value = row.PartyName;
            ws.Cell(r, 2).Value = row.Section;
            ws.Cell(r, 3).Value = row.PartyGSTIN;
            ws.Cell(r, 4).Value = row.InvoiceCount;
            ws.Cell(r, 5).Value = row.TaxableValue;
            ws.Cell(r, 6).Value = row.IGST;
            ws.Cell(r, 7).Value = row.CGST;
            ws.Cell(r, 8).Value = row.SGST;
            ws.Cell(r, 9).Value = row.IGST + row.CGST + row.SGST;
            ws.Cell(r, 10).Value = row.TotalAmount;
            tTax += row.TaxableValue; tIgst += row.IGST; tCgst += row.CGST; tSgst += row.SGST; tTotal += row.TotalAmount; tCount += row.InvoiceCount;
            r++;
        }

        if (r > dataStart) ExcelHelpers.ApplyBanding(ws.Range(dataStart, 1, r - 1, cols));

        ws.Cell(r, 1).Value = "TOTAL";
        ws.Cell(r, 4).Value = tCount;
        ws.Cell(r, 5).Value = tTax;
        ws.Cell(r, 6).Value = tIgst;
        ws.Cell(r, 7).Value = tCgst;
        ws.Cell(r, 8).Value = tSgst;
        ws.Cell(r, 9).Value = tIgst + tCgst + tSgst;
        ws.Cell(r, 10).Value = tTotal;
        ExcelHelpers.StyleTotalRow(ws.Range(r, 1, r, cols));

        foreach (var col in new[] { 5, 6, 7, 8, 9, 10 }) ExcelHelpers.FormatAmountColumn(ws.Column(col));
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents();
    }

    private static void BuildGstr1DetailSheet(XLWorkbook wb, CompanyDto company, string period, IReadOnlyList<InvoiceResponse> rows, string sheetName, string title)
    {
        var ws = wb.Worksheets.Add(sheetName);
        const int cols = 15;
        var headerRow = ExcelHelpers.AddCompanyHeader(ws, company, title, period, cols);

        string[] headers = { "Invoice No", "Invoice Date", "Party Name", "Party GSTIN", "Supply Type",
                              "Taxable Amount", "IGST Rate%", "IGST Amount", "CGST Rate%", "CGST Amount",
                              "SGST Rate%", "SGST Amount", "Total Value (INR)", "IRN Status", "IRN Number" };
        for (var i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        ExcelHelpers.StyleHeaderRow(ws.Range(headerRow, 1, headerRow, cols));

        var dataStart = headerRow + 1;
        var r = dataStart;
        decimal tTax = 0, tIgst = 0, tCgst = 0, tSgst = 0, tTotal = 0;
        foreach (var inv in rows)
        {
            ws.Cell(r, 1).Value = inv.InvoiceNumber;
            ws.Cell(r, 2).Value = inv.InvoiceDate;
            ws.Cell(r, 3).Value = inv.PartyName;
            ws.Cell(r, 4).Value = inv.PartyGSTIN;
            ws.Cell(r, 5).Value = inv.PlaceOfSupply;
            ws.Cell(r, 6).Value = inv.TaxableValue;
            ws.Cell(r, 7).Value = inv.Lines.Count > 0 ? (double)inv.Lines.Max(l => l.GstRate) : 0;
            ws.Cell(r, 8).Value = inv.IGST;
            ws.Cell(r, 9).Value = (double)EffectiveRate(inv.CGST, inv.TaxableValue);
            ws.Cell(r, 10).Value = inv.CGST;
            ws.Cell(r, 11).Value = (double)EffectiveRate(inv.SGST, inv.TaxableValue);
            ws.Cell(r, 12).Value = inv.SGST;
            ws.Cell(r, 13).Value = inv.TotalAmount;
            ws.Cell(r, 14).Value = string.IsNullOrWhiteSpace(inv.Irn) ? inv.EInvoiceStatus : "Generated";
            ws.Cell(r, 15).Value = inv.Irn;
            tTax += inv.TaxableValue; tIgst += inv.IGST; tCgst += inv.CGST; tSgst += inv.SGST; tTotal += inv.TotalAmount;
            r++;
        }

        if (r > dataStart) ExcelHelpers.ApplyBanding(ws.Range(dataStart, 1, r - 1, cols));

        ws.Cell(r, 1).Value = "TOTAL";
        ws.Cell(r, 6).Value = tTax;
        ws.Cell(r, 8).Value = tIgst;
        ws.Cell(r, 10).Value = tCgst;
        ws.Cell(r, 12).Value = tSgst;
        ws.Cell(r, 13).Value = tTotal;
        ExcelHelpers.StyleTotalRow(ws.Range(r, 1, r, cols));

        ExcelHelpers.FormatDateColumn(ws.Column(2));
        foreach (var col in new[] { 6, 8, 10, 12, 13 }) ExcelHelpers.FormatAmountColumn(ws.Column(col));
        ws.Column(7).Style.NumberFormat.Format = "0.##\"%\"";
        ws.Column(9).Style.NumberFormat.Format = "0.##\"%\"";
        ws.Column(11).Style.NumberFormat.Format = "0.##\"%\"";
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents();
    }

    // Effective tax rate (%) implied by a tax amount over its taxable base.
    // Rounded to 2 dp; blended across rates when an invoice mixes them. Returns
    // 0 when there's no taxable base or no tax (e.g. inter-state: CGST/SGST = 0).
    private static decimal EffectiveRate(decimal taxAmount, decimal taxableValue)
        => taxableValue == 0m || taxAmount == 0m
            ? 0m
            : decimal.Round(taxAmount / taxableValue * 100m, 2);

    private static void BuildGstr3bSheet(XLWorkbook wb, CompanyDto company, string period, Gstr3bResponse r)
    {
        var ws = wb.Worksheets.Add("GSTR-3B");
        const int cols = 3;
        var headerRow = ExcelHelpers.AddCompanyHeader(ws, company, "GSTR-3B", period, cols);

        ws.Cell(headerRow, 1).Value = "Section";
        ws.Cell(headerRow, 2).Value = "Field";
        ws.Cell(headerRow, 3).Value = "Amount (INR)";
        ExcelHelpers.StyleHeaderRow(ws.Range(headerRow, 1, headerRow, cols));

        var rowIdx = headerRow + 1;
        AddKv(ws, ref rowIdx, "3.1 Outward Supplies", "Invoice Count", r.Section3_1_OutwardSupplies.InvoiceCount);
        AddKv(ws, ref rowIdx, "3.1 Outward Supplies", "Taxable Value", r.Section3_1_OutwardSupplies.TaxableValue);
        AddKv(ws, ref rowIdx, "3.1 Outward Supplies", "IGST", r.Section3_1_OutwardSupplies.IGST);
        AddKv(ws, ref rowIdx, "3.1 Outward Supplies", "CGST", r.Section3_1_OutwardSupplies.CGST);
        AddKv(ws, ref rowIdx, "3.1 Outward Supplies", "SGST", r.Section3_1_OutwardSupplies.SGST);
        AddKv(ws, ref rowIdx, "3.1 Outward Supplies", "Total GST Collected", r.Section3_1_OutwardSupplies.TotalGstCollected);

        AddKv(ws, ref rowIdx, "Table 4 ITC", "Purchase Count", r.Table4_Itc.PurchaseCount);
        AddKv(ws, ref rowIdx, "Table 4 ITC", "Taxable Value", r.Table4_Itc.TaxableValue);
        AddKv(ws, ref rowIdx, "Table 4 ITC", "IGST", r.Table4_Itc.IGST);
        AddKv(ws, ref rowIdx, "Table 4 ITC", "CGST", r.Table4_Itc.CGST);
        AddKv(ws, ref rowIdx, "Table 4 ITC", "SGST", r.Table4_Itc.SGST);
        AddKv(ws, ref rowIdx, "Table 4 ITC", "Total ITC Available", r.Table4_Itc.TotalItcAvailable);

        AddKv(ws, ref rowIdx, "Net Tax Payable", "IGST", r.NetTaxPayable.IGST);
        AddKv(ws, ref rowIdx, "Net Tax Payable", "CGST", r.NetTaxPayable.CGST);
        AddKv(ws, ref rowIdx, "Net Tax Payable", "SGST", r.NetTaxPayable.SGST);
        AddKv(ws, ref rowIdx, "Net Tax Payable", "Total", r.NetTaxPayable.Total);

        AddKv(ws, ref rowIdx, "Carry Forward", "IGST", r.CarryForward.IGST);
        AddKv(ws, ref rowIdx, "Carry Forward", "CGST", r.CarryForward.CGST);
        AddKv(ws, ref rowIdx, "Carry Forward", "SGST", r.CarryForward.SGST);
        AddKv(ws, ref rowIdx, "Carry Forward", "Total", r.CarryForward.TotalCarryForward);

        ExcelHelpers.ApplyBanding(ws.Range(headerRow + 1, 1, rowIdx - 1, cols));
        ExcelHelpers.FormatAmountColumn(ws.Column(3));
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents();
    }

    private static void AddKv(IXLWorksheet ws, ref int row, string section, string field, decimal value)
    {
        ws.Cell(row, 1).Value = section;
        ws.Cell(row, 2).Value = field;
        ws.Cell(row, 3).Value = value;
        row++;
    }

    private static void AddKv(IXLWorksheet ws, ref int row, string section, string field, int value)
    {
        ws.Cell(row, 1).Value = section;
        ws.Cell(row, 2).Value = field;
        ws.Cell(row, 3).Value = value;
        row++;
    }

    private static void BuildInvoiceRegisterSheet(XLWorkbook wb, CompanyDto company, string period, IReadOnlyList<InvoiceResponse> rows)
    {
        var ws = wb.Worksheets.Add("Invoices");
        const int cols = 9;
        var headerRow = ExcelHelpers.AddCompanyHeader(ws, company, "Invoice Register", period, cols);

        string[] headers = { "Invoice No", "Invoice Date", "Party Name", "Party GSTIN", "Supply Type",
                              "Taxable Amount", "IGST", "CGST + SGST", "Total Value (INR)" };
        for (var i = 0; i < headers.Length; i++) ws.Cell(headerRow, i + 1).Value = headers[i];
        ExcelHelpers.StyleHeaderRow(ws.Range(headerRow, 1, headerRow, cols));

        var dataStart = headerRow + 1;
        var r = dataStart;
        decimal tTax = 0, tIgst = 0, tCs = 0, tTotal = 0;
        foreach (var inv in rows)
        {
            ws.Cell(r, 1).Value = inv.InvoiceNumber;
            ws.Cell(r, 2).Value = inv.InvoiceDate;
            ws.Cell(r, 3).Value = inv.PartyName;
            ws.Cell(r, 4).Value = inv.PartyGSTIN;
            ws.Cell(r, 5).Value = inv.PlaceOfSupply;
            ws.Cell(r, 6).Value = inv.TaxableValue;
            ws.Cell(r, 7).Value = inv.IGST;
            ws.Cell(r, 8).Value = inv.CGST + inv.SGST;
            ws.Cell(r, 9).Value = inv.TotalAmount;
            tTax += inv.TaxableValue; tIgst += inv.IGST; tCs += inv.CGST + inv.SGST; tTotal += inv.TotalAmount;
            r++;
        }

        if (r > dataStart) ExcelHelpers.ApplyBanding(ws.Range(dataStart, 1, r - 1, cols));

        ws.Cell(r, 1).Value = "TOTAL";
        ws.Cell(r, 6).Value = tTax;
        ws.Cell(r, 7).Value = tIgst;
        ws.Cell(r, 8).Value = tCs;
        ws.Cell(r, 9).Value = tTotal;
        ExcelHelpers.StyleTotalRow(ws.Range(r, 1, r, cols));

        ExcelHelpers.FormatDateColumn(ws.Column(2));
        foreach (var col in new[] { 6, 7, 8, 9 }) ExcelHelpers.FormatAmountColumn(ws.Column(col));
        ws.SheetView.FreezeRows(headerRow);
        ws.Columns().AdjustToContents();
    }

    private static void BuildReconSheet(XLWorkbook wb, CompanyDto company, string period, ReconReportResponse report)
    {
        var ws = wb.Worksheets.Add("Reconciliation");
        const int cols = 8;
        var headerRow = ExcelHelpers.AddCompanyHeader(ws, company, "Reconciliation Report", period, cols);

        // Inline summary band before the detail header
        ws.Cell(headerRow, 1).Value = $"Matched: {report.Summary.Matched}    Mismatch: {report.Summary.Mismatch}    Missing: {report.Summary.Missing}    Not in 2B: {report.Summary.NotIn2B}    Total: {report.Summary.Total}";
        ws.Range(headerRow, 1, headerRow, cols).Merge().Style
            .Font.SetBold().Font.SetFontSize(11)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1e3a8a"))
            .Font.SetFontColor(XLColor.White)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        var detailHeader = headerRow + 1;
        string[] headers = { "Status", "Supplier GSTIN", "Invoice No", "GSTR-2B Amount", "Books Amount", "Difference", "AI Remarks", "Created" };
        for (var i = 0; i < headers.Length; i++) ws.Cell(detailHeader, i + 1).Value = headers[i];
        ExcelHelpers.StyleHeaderRow(ws.Range(detailHeader, 1, detailHeader, cols));

        var dataStart = detailHeader + 1;
        var r = dataStart;
        foreach (var row in report.Rows)
        {
            ws.Cell(r, 1).Value = row.Status;
            ws.Cell(r, 2).Value = row.SupplierGSTIN;
            ws.Cell(r, 3).Value = row.InvoiceNo;
            ws.Cell(r, 4).Value = row.GSTR2BAmount;
            ws.Cell(r, 5).Value = row.BooksAmount;
            ws.Cell(r, 6).Value = row.Difference;
            ws.Cell(r, 7).Value = row.AIRemarks;
            ws.Cell(r, 8).Value = row.CreatedOn;
            r++;
        }
        if (r > dataStart) ExcelHelpers.ApplyBanding(ws.Range(dataStart, 1, r - 1, cols));

        foreach (var col in new[] { 4, 5, 6 }) ExcelHelpers.FormatAmountColumn(ws.Column(col));
        ExcelHelpers.FormatDateColumn(ws.Column(8));
        ws.SheetView.FreezeRows(detailHeader);
        ws.Columns().AdjustToContents();
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<CompanyDto> LoadCompanyAsync(CancellationToken cancellationToken)
        => await _companyService.GetAsync(cancellationToken) ?? new CompanyDto { CompanyName = "Tenant" };

    private string GetTenantGstin()
    {
        var tenant = _httpContextAccessor.HttpContext?.Items["Tenant"] as Tenant;
        return tenant?.GSTIN ?? "tenant";
    }

    private static (int Year, int Month) ParsePeriod(string period)
    {
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6
            || !int.TryParse(period.AsSpan(0, 4), out var y)
            || !int.TryParse(period.AsSpan(4, 2), out var m)
            || m < 1 || m > 12)
        {
            throw new ArgumentException("period must be YYYYMM (e.g. 202604).", nameof(period));
        }
        return (y, m);
    }
}
