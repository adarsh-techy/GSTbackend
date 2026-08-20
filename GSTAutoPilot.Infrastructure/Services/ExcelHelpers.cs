using ClosedXML.Excel;
using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Infrastructure.Services;

// Shared styling so every report (GSTR-1, GSTR-3B, Invoices, Recon) feels
// like the same product. The colour palette matches the in-app dark KPI
// cards loosely — dark navy header, white text, light-grey banded rows.
internal static class ExcelHelpers
{
    public static readonly XLColor HeaderBg = XLColor.FromHtml("#0f172a");
    public static readonly XLColor HeaderFg = XLColor.White;
    public static readonly XLColor BandBg = XLColor.FromHtml("#f1f5f9");
    public static readonly XLColor TotalBg = XLColor.FromHtml("#e2e8f0");
    public const string InrFormat = "_-₹* #,##0.00_-;-₹* #,##0.00_-;_-₹* \"-\"??_-;_-@_-";

    // Writes the 4-row company banner at the top of every sheet:
    //   row 1: company name (large, bold)
    //   row 2: address + GSTIN
    //   row 3: report title + period (e.g. "GSTR-1 for Apr 2026")
    //   row 4: blank separator
    // Returns the row index where data should begin (5).
    public static int AddCompanyHeader(IXLWorksheet ws, CompanyDto company, string title, string period, int columnCount)
    {
        var name = string.IsNullOrWhiteSpace(company.CompanyName) ? "GSTAutoPilot" : company.CompanyName.ToUpperInvariant();
        var addressParts = new[] { company.Address1, company.Address2, company.Address3, company.PinCode }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        var address = string.Join(", ", addressParts);
        var gstin = string.IsNullOrWhiteSpace(company.GSTIN) ? string.Empty : $"GSTIN: {company.GSTIN}";

        ws.Cell(1, 1).Value = name;
        ws.Range(1, 1, 1, columnCount).Merge().Style
            .Font.SetBold().Font.SetFontSize(14)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        ws.Cell(2, 1).Value = string.IsNullOrEmpty(gstin) ? address : $"{address}    {gstin}";
        ws.Range(2, 1, 2, columnCount).Merge().Style
            .Font.SetFontSize(10).Font.SetFontColor(XLColor.FromHtml("#475569"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        ws.Cell(3, 1).Value = $"{title} — {FormatPeriod(period)}";
        ws.Range(3, 1, 3, columnCount).Merge().Style
            .Font.SetBold().Font.SetFontSize(12).Font.SetFontColor(XLColor.FromHtml("#0f172a"))
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);

        return 5; // first data row after the 4-row banner
    }

    public static void StyleHeaderRow(IXLRange headerRange)
    {
        headerRange.Style
            .Fill.SetBackgroundColor(HeaderBg)
            .Font.SetFontColor(HeaderFg)
            .Font.SetBold()
            .Alignment.SetVertical(XLAlignmentVerticalValues.Center)
            .Border.SetOutsideBorder(XLBorderStyleValues.Thin)
            .Border.SetInsideBorder(XLBorderStyleValues.Thin);
    }

    public static void ApplyBanding(IXLRange dataRange)
    {
        if (dataRange.RowCount() == 0) return;
        for (var i = 1; i <= dataRange.RowCount(); i++)
        {
            if (i % 2 == 0)
            {
                dataRange.Row(i).Style.Fill.SetBackgroundColor(BandBg);
            }
        }
        dataRange.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin)
            .Border.SetInsideBorderColor(XLColor.FromHtml("#e2e8f0"))
            .Border.SetInsideBorder(XLBorderStyleValues.Hair);
    }

    public static void StyleTotalRow(IXLRange totalRange)
    {
        totalRange.Style
            .Font.SetBold()
            .Fill.SetBackgroundColor(TotalBg)
            .Border.SetTopBorder(XLBorderStyleValues.Medium)
            .Border.SetBottomBorder(XLBorderStyleValues.Medium);
    }

    public static void FormatAmountColumn(IXLColumn column)
    {
        column.Style.NumberFormat.Format = InrFormat;
        column.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Right);
    }

    public static void FormatDateColumn(IXLColumn column)
    {
        column.Style.DateFormat.Format = "dd-MMM-yyyy";
    }

    public static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static string FormatPeriod(string period)
    {
        if (string.IsNullOrWhiteSpace(period) || period.Length != 6) return period;
        if (!int.TryParse(period.AsSpan(0, 4), out var y)) return period;
        if (!int.TryParse(period.AsSpan(4, 2), out var m)) return period;
        if (m < 1 || m > 12) return period;
        return $"{new DateTime(y, m, 1):MMM yyyy}";
    }

    public static string SafeFileFragment(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "tenant";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Where(c => !invalid.Contains(c) && !char.IsWhiteSpace(c)).ToArray());
    }
}
