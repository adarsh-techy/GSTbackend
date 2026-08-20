using System.Globalization;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.CarolERP.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GSTAutoPilot.Infrastructure.Services;

public class InvoicePdfService : IInvoicePdfService
{
    static InvoicePdfService()
    {
        // QuestPDF Community licence — required once per process before any
        // document is generated. Free for organisations earning under USD 1M/yr.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private readonly CarolERPDbContext _carol;
    private readonly CarolDocumentReader _reader;
    private readonly Persistence.TenantDbContext _db;
    private readonly ICompanyService _companyService;
    private readonly ITenantSettingsService _settingsService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IWebHostEnvironment _hostEnv;

    public InvoicePdfService(
        CarolERPDbContext carol,
        CarolDocumentReader reader,
        Persistence.TenantDbContext db,
        ICompanyService companyService,
        ITenantSettingsService settingsService,
        IHttpContextAccessor httpContextAccessor,
        IWebHostEnvironment hostEnv)
    {
        _carol = carol;
        _reader = reader;
        _db = db;
        _companyService = companyService;
        _settingsService = settingsService;
        _httpContextAccessor = httpContextAccessor;
        _hostEnv = hostEnv;
    }

    public async Task<InvoicePdfResult?> RenderAsync(int billId, CancellationToken cancellationToken = default)
    {
        var raw = await _reader.ReadOutwardRawByBillIdAsync(billId, cancellationToken);
        if (raw is null) return null;
        var header = raw.Value.Header;

        var account = await _carol.Accounts
            .FirstOrDefaultAsync(a => a.AccountId == header.AccountId, cancellationToken);

        var lines = BuildLines(raw.Value.Lines);

        var company = await _companyService.GetAsync(cancellationToken)
            ?? new CompanyDto { CompanyName = "Company" };
        var settings = await _settingsService.GetAsync(cancellationToken);

        // QR source priority: the CarolERP SignedQRCode column first (when the
        // ERP itself e-invoiced the bill), otherwise our own IRNRecords — i.e.
        // an IRN we generated via WhiteBooks / the stub for this BillId.
        var qrSource = header.SignedQRCode;
        if (string.IsNullOrWhiteSpace(qrSource))
        {
            var irn = await _db.IRNRecords.AsNoTracking()
                .Where(r => r.BillId == billId && r.Status == IRNStatus.Generated)
                .OrderByDescending(r => r.AcknowledgementDate)
                .FirstOrDefaultAsync(cancellationToken);
            qrSource = irn?.QRCode;
        }
        var qr = BuildQrPng(qrSource);
        var logo = TryLoadLogo(settings.LogoPath);

        var invoiceNumber = BuildInvoiceNumber(header, raw.Value.Prefix);
        var fileName = $"Invoice-{Sanitize(invoiceNumber)}.pdf";

        var roundOffs = await _reader.ReadRoundOffAsync(new[] { billId }, cancellationToken);
        var roundOff = roundOffs.TryGetValue(billId, out var ro) ? ro : null;

        var document = new InvoicePdfDocument(
            company,
            settings,
            header,
            account,
            lines,
            invoiceNumber,
            qr,
            logo,
            raw.Value.Extras,
            roundOff?.Amount ?? 0m,
            roundOff?.Label ?? string.Empty);

        var bytes = document.GeneratePdf();
        return new InvoicePdfResult(bytes, fileName);
    }

    // Lines arrive already normalized by CarolDocumentReader (the matching
    // Document Mapping's line table), with description, HSN and INR tax resolved.
    private static IReadOnlyList<LineRow> BuildLines(IReadOnlyList<CarolSalesLine> lines)
    {
        if (lines.Count == 0) return Array.Empty<LineRow>();

        // Collapse repeated lines of the same product onto one row: a single
        // item dispatched in several cartons shows up as multiple Bill_Exp_trn
        // rows with identical description + HSN + unit rate. Group them and sum
        // quantity / amount / tax. GroupBy preserves first-seen order.
        var grouped = lines
            .GroupBy(l => (l.Description, l.Hsn, l.Rate, l.IgstRate))
            .Select(g => new LineRow(
                Sr: 0,
                Description: g.Key.Description,
                Hsn: g.Key.Hsn,
                Quantity: g.Sum(x => x.Quantity),
                Rate: g.Key.Rate,
                AmountInr: decimal.Round(g.Sum(x => x.TaxableInr), 2),
                IgstRate: g.Key.IgstRate,
                IgstAmount: decimal.Round(g.Sum(x => x.IgstAmount), 2),
                CgstAmount: decimal.Round(g.Sum(x => x.CgstAmount), 2),
                SgstAmount: decimal.Round(g.Sum(x => x.SgstAmount), 2),
                GrossInr: decimal.Round(g.Sum(x => x.GrossInr), 2)))
            .ToList();

        for (var i = 0; i < grouped.Count; i++)
            grouped[i] = grouped[i] with { Sr = i + 1 };
        return grouped;
    }

    // The NIC e-invoice SignedQRCode contains either:
    //   1. a base64-encoded image (PNG/JPG) — decode and embed directly, or
    //   2. a JWS/string payload (`header.payload.signature`) — encode that
    //      string into a QR with QRCoder.
    // We try (1) first by checking PNG/JPG magic bytes after base64-decode;
    // if that fails we fall back to (2). Empty/whitespace returns null and
    // the document renders the "e-Invoice Pending" notice instead.
    private static byte[]? BuildQrPng(string? signedQrCode)
    {
        if (string.IsNullOrWhiteSpace(signedQrCode)) return null;
        var content = signedQrCode.Trim();

        if (TryDecodeBase64Image(content, out var imageBytes))
        {
            return imageBytes;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(8);
    }

    private byte[]? TryLoadLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath)) return null;
        // Logo paths from the upload endpoint are saved as relative paths like
        // "uploads/logos/{tenantId}.png" rooted at wwwroot. Anything else is
        // treated as an absolute filesystem path.
        var candidate = Path.IsPathRooted(logoPath)
            ? logoPath
            : Path.Combine(_hostEnv.WebRootPath ?? Path.Combine(_hostEnv.ContentRootPath, "wwwroot"), logoPath);
        try
        {
            if (!File.Exists(candidate)) return null;
            var bytes = File.ReadAllBytes(candidate);
            // Quick header check — QuestPDF's Image() throws (and aborts the
            // whole document) if Skia can't decode the bytes. Drop anything
            // that doesn't look like a real PNG/JPG.
            var isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50
                && bytes[2] == 0x4E && bytes[3] == 0x47;
            var isJpg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
            if (!isPng && !isJpg) return null;
            return bytes;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryDecodeBase64Image(string content, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var clean = new string(content.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.Length == 0 || clean.Length % 4 != 0) return false;
        try
        {
            var decoded = Convert.FromBase64String(clean);
            var isPng = decoded.Length > 8 && decoded[0] == 0x89 && decoded[1] == 0x50
                && decoded[2] == 0x4E && decoded[3] == 0x47;
            var isJpg = decoded.Length > 3 && decoded[0] == 0xFF && decoded[1] == 0xD8
                && decoded[2] == 0xFF;
            if (isPng || isJpg)
            {
                bytes = decoded;
                return true;
            }
        }
        catch (FormatException)
        {
        }
        return false;
    }

    // Printed invoice number: document-series Prefix + "/" + core number (e.g.
    // "CC" + "236" => "CC/236"), inserting the "/" only when the prefix doesn't
    // already end in one. Matches the invoice-list/GSTR-1 numbering.
    private static string BuildInvoiceNumber(CarolSalesMas header, string? prefix)
    {
        string core;
        if (!string.IsNullOrWhiteSpace(header.InvNo)) core = header.InvNo!;
        else if (header.BillNumber.HasValue) core = header.BillNumber.Value.ToString() + (header.Suffix ?? string.Empty);
        else return $"BILL-{header.BillId}";
        var pfx = prefix?.Trim();
        if (string.IsNullOrWhiteSpace(pfx)) return core;
        var sep = pfx!.EndsWith("/") ? string.Empty : "/";
        return $"{pfx}{sep}{core}";
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Where(c => !invalid.Contains(c) && !char.IsWhiteSpace(c)).ToArray());
    }
}

internal sealed record LineRow(
    int Sr,
    string Description,
    string Hsn,
    decimal Quantity,
    decimal Rate,
    decimal AmountInr,
    decimal IgstRate,
    decimal IgstAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal GrossInr);

internal sealed class InvoicePdfDocument : IDocument
{
    private static readonly CultureInfo Inr = CultureInfo.GetCultureInfo("en-IN");

    private readonly CompanyDto _company;
    private readonly TenantSettingsDto _settings;
    private readonly CarolSalesMas _header;
    private readonly CarolAccount? _account;
    private readonly IReadOnlyList<LineRow> _lines;
    private readonly string _invoiceNumber;
    private readonly byte[]? _qrPng;
    private readonly byte[]? _logoPng;
    private readonly CarolDocumentReader.HeaderExtras? _extras;
    private readonly decimal _roundOff;
    private readonly string _roundOffLabel;

    public InvoicePdfDocument(
        CompanyDto company,
        TenantSettingsDto settings,
        CarolSalesMas header,
        CarolAccount? account,
        IReadOnlyList<LineRow> lines,
        string invoiceNumber,
        byte[]? qrPng,
        byte[]? logoPng,
        CarolDocumentReader.HeaderExtras? extras,
        decimal roundOff,
        string roundOffLabel)
    {
        _company = company;
        _settings = settings;
        _header = header;
        _account = account;
        _lines = lines;
        _invoiceNumber = invoiceNumber;
        _qrPng = qrPng;
        _logoPng = logoPng;
        _extras = extras;
        _roundOff = roundOff;
        _roundOffLabel = roundOffLabel;
    }

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(25);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Calibri));

            page.Header().Element(ComposeHeader);
            page.Content().Element(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeHeader(IContainer container)
    {
        // Header collapses unused slots so the company name takes the full
        // width when no logo is configured and the right rail disappears
        // entirely when this invoice has no e-Invoice QR.
        container.PaddingBottom(8).BorderBottom(1).BorderColor(Colors.Grey.Darken1)
            .PaddingBottom(8).Row(row =>
        {
            if (_logoPng is not null)
            {
                // Bytes are header-checked upstream, but a malformed body can
                // still throw inside Skia. Swallow so a bad logo doesn't break
                // the entire invoice generation.
                row.ConstantItem(80).AlignTop().Element(c =>
                {
                    try { c.Width(70).Height(70).Image(_logoPng); }
                    catch { /* logo failed to decode — render nothing */ }
                });
            }
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(_company.CompanyName.ToUpperInvariant()).Bold().FontSize(16);
                col.Item().Text(JoinNonEmpty(_company.Address1)).FontSize(9);
                col.Item().Text(JoinNonEmpty(_company.Address2, _company.Address3)).FontSize(9);
                if (!string.IsNullOrWhiteSpace(_company.PinCode))
                    col.Item().Text($"PIN: {_company.PinCode}").FontSize(9);
                if (!string.IsNullOrWhiteSpace(_company.GSTIN))
                    col.Item().PaddingTop(2).Text($"GSTIN: {_company.GSTIN}").Bold().FontSize(10);
                if (!string.IsNullOrWhiteSpace(_company.PAN))
                    col.Item().Text($"PAN: {_company.PAN}").FontSize(9);
                if (!string.IsNullOrWhiteSpace(_company.Phone))
                    col.Item().Text($"Tel: {_company.Phone}").FontSize(9);
                if (!string.IsNullOrWhiteSpace(_company.Email))
                    col.Item().Text($"Email: {_company.Email}").FontSize(9);
            });

            if (_qrPng is not null)
            {
                row.ConstantItem(100).AlignRight().AlignTop().Column(col =>
                {
                    col.Item().Width(80).Height(80).Image(_qrPng);
                    col.Item().AlignCenter().Text("e-Invoice").FontSize(8).FontColor(Colors.Grey.Medium);
                });
            }
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.PaddingVertical(10).Column(col =>
        {
            col.Spacing(8);

            col.Item().Border(1.5f).BorderColor(Colors.Grey.Darken2).Background(Colors.Grey.Lighten3)
                .AlignCenter().Padding(8).Text("TAX INVOICE").Bold().FontSize(14);

            col.Item().Border(1).BorderColor(Colors.Grey.Darken1).Row(row =>
            {
                row.RelativeItem().Padding(6).Column(c =>
                {
                    c.Item().Text(t => { t.Span("Invoice No: ").SemiBold(); t.Span(_invoiceNumber); });
                    if (!string.IsNullOrWhiteSpace(_header.IRN))
                        c.Item().Text(t => { t.Span("IRN: ").SemiBold(); t.Span(_header.IRN!); });
                });
                row.RelativeItem().Padding(6).Column(c =>
                {
                    c.Item().Text(t =>
                    {
                        t.Span("Date: ").SemiBold();
                        t.Span(_header.BillDate.ToString("dd/MM/yyyy", Inr));
                    });
                    if (_header.AckNo.HasValue)
                        c.Item().Text(t => { t.Span("Ack No: ").SemiBold(); t.Span(_header.AckNo.Value.ToString()); });
                    if (!string.IsNullOrWhiteSpace(_header.EwbNo))
                        c.Item().Text(t => { t.Span("EWB: ").SemiBold(); t.Span(_header.EwbNo!); });
                });
            });

            col.Item().Border(1).BorderColor(Colors.Grey.Darken1).Padding(6).Column(c =>
            {
                c.Item().Text("Bill To:").SemiBold();
                c.Item().Text(BillToName()).Bold();
                c.Item().Text(t => { t.Span("GSTIN: ").SemiBold(); t.Span(BillToGstinText()); });
                if (!string.IsNullOrWhiteSpace(_header.SupplyType))
                    c.Item().Text(t => { t.Span("Place of Supply: ").SemiBold(); t.Span(_header.SupplyType!); });
            });

            col.Item().Element(ComposeLinesTable);

            col.Item().Element(ComposeTotals);

            if (_settings.ShowBankDetails && !string.IsNullOrWhiteSpace(_company.BankName))
            {
                col.Item().Border(1).BorderColor(Colors.Grey.Darken1).Background(Colors.Grey.Lighten4).Padding(8).Column(c =>
                {
                    c.Item().Text("Bank Details").SemiBold().FontSize(11);
                    c.Item().Text(t => { t.Span("Bank: ").SemiBold(); t.Span(_company.BankName ?? string.Empty); });
                    if (!string.IsNullOrWhiteSpace(_company.BankAccName))
                        c.Item().Text(t => { t.Span("A/C Name: ").SemiBold(); t.Span(_company.BankAccName!); });
                    if (!string.IsNullOrWhiteSpace(_company.AccountNo))
                        c.Item().Text(t => { t.Span("A/C No: ").SemiBold(); t.Span(_company.AccountNo!); });
                    if (!string.IsNullOrWhiteSpace(_company.IFSCCode))
                        c.Item().Text(t => { t.Span("IFSC: ").SemiBold(); t.Span(_company.IFSCCode!); });
                    if (!string.IsNullOrWhiteSpace(_company.BranchName))
                        c.Item().Text(t => { t.Span("Branch: ").SemiBold(); t.Span(_company.BranchName!); });
                });
            }

            if (!string.IsNullOrWhiteSpace(_settings.TermsAndConditions))
            {
                col.Item().PaddingTop(6)
                    .DefaultTextStyle(s => s.FontSize(9).FontColor(Colors.Grey.Darken2))
                    .Text(t =>
                    {
                        t.Span("Terms & Conditions: ").SemiBold();
                        t.Span(_settings.TermsAndConditions!);
                    });
            }

            if (_settings.ShowSignature)
            {
                col.Item().PaddingTop(20).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text($"For {_company.CompanyName}").SemiBold();
                    c.Item().PaddingTop(40).AlignRight().Text("Authorised Signatory").FontSize(9);
                });
            }

            if (!string.IsNullOrWhiteSpace(_settings.InvoiceFooterText))
            {
                col.Item().PaddingTop(6).AlignCenter().Text(_settings.InvoiceFooterText!).FontSize(9).Italic();
            }
        });
    }

    private void ComposeLinesTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);
                c.RelativeColumn(5);
                c.ConstantColumn(60);
                c.ConstantColumn(50);
                c.ConstantColumn(60);
                c.ConstantColumn(75);
            });

            table.Header(header =>
            {
                static IContainer Cell(IContainer c) => c.Background(Colors.Grey.Darken3)
                    .PaddingVertical(5).PaddingHorizontal(6).DefaultTextStyle(t => t.FontColor(Colors.White).SemiBold().FontSize(10));
                header.Cell().Element(Cell).AlignCenter().Text("#");
                header.Cell().Element(Cell).Text("Description");
                header.Cell().Element(Cell).Text("HSN");
                header.Cell().Element(Cell).AlignRight().Text("Qty");
                header.Cell().Element(Cell).AlignRight().Text("Rate");
                header.Cell().Element(Cell).AlignRight().Text("Amount");
            });

            foreach (var row in _lines)
            {
                var stripe = row.Sr % 2 == 0 ? Colors.Grey.Lighten4 : Colors.White;
                IContainer Cell(IContainer c) => c.Background(stripe).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1)
                    .PaddingVertical(4).PaddingHorizontal(6);
                table.Cell().Element(Cell).AlignCenter().Text(row.Sr.ToString());
                table.Cell().Element(Cell).Text(row.Description);
                table.Cell().Element(Cell).Text(row.Hsn);
                table.Cell().Element(Cell).AlignRight().Text(row.Quantity.ToString("0.##", Inr));
                table.Cell().Element(Cell).AlignRight().Text(row.Rate.ToString("N2", Inr));
                table.Cell().Element(Cell).AlignRight().Text(row.AmountInr.ToString("N2", Inr));
            }
        });
    }

    private void ComposeTotals(IContainer container)
    {
        var rate = _header.ExchRate;
        var taxableLines = _lines.Sum(l => l.AmountInr);
        var igstAmt = decimal.Round(_lines.Sum(l => l.IgstAmount), 2);
        var cgstAmt = decimal.Round(_lines.Sum(l => l.CgstAmount), 2);
        var sgstAmt = decimal.Round(_lines.Sum(l => l.SgstAmount), 2);
        var headerInr = _header.TotalAmt * rate;
        // CarolERP header TotalAmt is the pre-tax (often foreign-currency)
        // amount, so the grand total must add the tax. Fall back to header*rate
        // only when there are no line rows to tax.
        var taxable = taxableLines == 0m ? headerInr : taxableLines;
        // Discount = gross line Amount - taxable (stored gross, not Rate x Qty
        // which over-states it when Rate is a list/MRP price). 0 when no discount.
        var grossLines = decimal.Round(_lines.Sum(l => l.GrossInr), 2);
        var discountAmt = grossLines > taxable ? decimal.Round(grossLines - taxable, 2) : 0m;
        // Invoice-level adjustment (round-off / misc charges) is already signed.
        // Apply it only to the line-derived total; the header fallback amount is
        // already the ERP's final rounded figure.
        var totalAmtInr = taxableLines == 0m ? headerInr : decimal.Round(taxable + igstAmt + cgstAmt + sgstAmt + _roundOff, 2);
        var igstRate = _lines.Select(l => l.IgstRate).DefaultIfEmpty(0m).Max();
        // Effective CGST/SGST rate, derived from amount ÷ taxable (these lines
        // carry the tax amount but not a separate CGST/SGST rate column).
        var cgstRate = taxable > 0m ? decimal.Round(cgstAmt / taxable * 100m, 2) : 0m;
        var sgstRate = taxable > 0m ? decimal.Round(sgstAmt / taxable * 100m, 2) : 0m;
        var totalInWords = AmountInWords.Inr(totalAmtInr);

        container.Column(outer =>
        {
            outer.Item().Row(row =>
            {
                row.RelativeItem();
                row.ConstantItem(240).Border(1).BorderColor(Colors.Grey.Darken1).Column(c =>
                {
                    if (discountAmt != 0m)
                    {
                        c.Item().Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text("Gross Amount");
                            r.ConstantItem(120).AlignRight().Text(grossLines.ToString("N2", Inr));
                        });
                        c.Item().Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text("Discount");
                            r.ConstantItem(120).AlignRight().Text("-" + discountAmt.ToString("N2", Inr));
                        });
                    }
                    c.Item().Padding(6).Row(r =>
                    {
                        r.RelativeItem().Text("Taxable Amount").SemiBold();
                        r.ConstantItem(120).AlignRight().Text(taxable.ToString("N2", Inr));
                    });
                    // Show only the taxes that actually apply: CGST+SGST for
                    // intra-state, IGST for inter-state. A wholly-exempt bill
                    // (all zero) shows none.
                    if (cgstAmt != 0m)
                        c.Item().Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text($"CGST @ {cgstRate:0.##}%");
                            r.ConstantItem(120).AlignRight().Text(cgstAmt.ToString("N2", Inr));
                        });
                    if (sgstAmt != 0m)
                        c.Item().Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text($"SGST @ {sgstRate:0.##}%");
                            r.ConstantItem(120).AlignRight().Text(sgstAmt.ToString("N2", Inr));
                        });
                    if (igstAmt != 0m)
                        c.Item().Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text($"IGST @ {igstRate:0.##}%");
                            r.ConstantItem(120).AlignRight().Text(igstAmt.ToString("N2", Inr));
                        });
                    if (_roundOff != 0m)
                        c.Item().Padding(6).Row(r =>
                        {
                            r.RelativeItem().Text(string.IsNullOrWhiteSpace(_roundOffLabel) ? "Round Off" : _roundOffLabel);
                            r.ConstantItem(120).AlignRight().Text(_roundOff.ToString("N2", Inr));
                        });
                    c.Item().Background(Colors.Grey.Darken3).Padding(6).Row(r =>
                    {
                        r.RelativeItem().Text("TOTAL INR").Bold().FontColor(Colors.White);
                        r.ConstantItem(120).AlignRight().Text(totalAmtInr.ToString("N2", Inr)).Bold().FontColor(Colors.White);
                    });
                });
            });

            outer.Item().PaddingTop(6).Background(Colors.Grey.Lighten3).Padding(6).Text(t =>
            {
                t.Span("Amount in Words: ").SemiBold();
                t.Span(totalInWords);
            });
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.PaddingTop(6).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                if (!string.IsNullOrWhiteSpace(_company.IECode))
                    c.Item().Text($"IE Code: {_company.IECode}").FontSize(8).FontColor(Colors.Grey.Medium);
                c.Item().Text("This is a system-generated document.").FontSize(8).FontColor(Colors.Grey.Medium);
            });
            row.ConstantItem(120).AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Medium));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        });
    }

    // Buyer name: for cash/walk-in bills the Account is the generic "Cash"
    // ledger, so use the real name carried on the header (OtherRef/Title/TosName).
    private string BillToName()
    {
        var name = _account?.AccountName?.Trim();
        var isCash = string.IsNullOrEmpty(name) || name.Equals("Cash", StringComparison.OrdinalIgnoreCase);
        if (isCash && !string.IsNullOrWhiteSpace(_extras?.CustomerRef))
            return _extras!.CustomerRef!.Trim();
        return string.IsNullOrEmpty(name) ? "—" : name!;
    }

    private string BillToGstinText()
    {
        // GSTIN may be on the Account or, for cash bills, on the header.
        var gstin = CleanGstin(_account?.GstNo) ?? CleanGstin(_extras?.GstNo);
        if (gstin is not null) return gstin;
        var supplyType = _header.SupplyType ?? string.Empty;
        var isExport = supplyType.Contains("EXP", StringComparison.OrdinalIgnoreCase)
            || supplyType.Contains("OVERSEAS", StringComparison.OrdinalIgnoreCase)
            || _header.ExchRate != 1m;
        return isExport ? "Foreign / Export Customer" : "Unregistered";
    }

    // Returns a clean 15-char GSTIN, or null for blanks / placeholders like
    // "NIL" so they don't masquerade as a real registration.
    private static string? CleanGstin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return cleaned.Length == 15 && cleaned.All(char.IsLetterOrDigit) ? cleaned : null;
    }

    private static string JoinNonEmpty(params string?[] parts)
        => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

internal static class AmountInWords
{
    private static readonly string[] Ones =
    {
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine",
        "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen",
        "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens =
    {
        "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"
    };

    public static string Inr(decimal amount)
    {
        var negative = amount < 0;
        if (negative) amount = -amount;
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var rupees = (long)Math.Truncate(amount);
        var paise = (int)Math.Round((amount - rupees) * 100m, MidpointRounding.AwayFromZero);

        var sb = new System.Text.StringBuilder();
        if (negative) sb.Append("Minus ");
        sb.Append("Rupees ");
        sb.Append(WholeNumberToWords(rupees));
        if (paise > 0)
        {
            sb.Append(" And ");
            sb.Append(WholeNumberToWords(paise));
            sb.Append(paise == 1 ? " Paisa" : " Paise");
        }
        sb.Append(" Only");
        return sb.ToString();
    }

    private static string WholeNumberToWords(long n)
    {
        if (n == 0) return "Zero";
        var parts = new List<string>();
        var crore = n / 10_000_000L; n %= 10_000_000L;
        if (crore > 0) parts.Add($"{WholeNumberToWords(crore)} Crore");
        var lakh = n / 100_000L; n %= 100_000L;
        if (lakh > 0) parts.Add($"{TwoDigit(lakh)} Lakh");
        var thousand = n / 1000L; n %= 1000L;
        if (thousand > 0) parts.Add($"{TwoDigit(thousand)} Thousand");
        var hundred = n / 100L; n %= 100L;
        if (hundred > 0) parts.Add($"{Ones[hundred]} Hundred");
        if (n > 0)
        {
            if (parts.Count > 0) parts.Add("and");
            parts.Add(TwoDigit(n));
        }
        return string.Join(' ', parts);
    }

    private static string TwoDigit(long n)
    {
        if (n < 20) return Ones[n];
        var tens = (int)(n / 10);
        var ones = (int)(n % 10);
        return ones == 0 ? Tens[tens] : $"{Tens[tens]} {Ones[ones]}";
    }
}
