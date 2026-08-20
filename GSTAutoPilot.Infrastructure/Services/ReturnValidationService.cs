using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Application.Services;

namespace GSTAutoPilot.Infrastructure.Services;

// Pre-file validation for GSTR-1: checks the prepared outward invoices for the
// problems GSTN rejects at retsave, so the filing preview can list them with a
// "Fix in Invoices" / "Continue Anyway" choice. Errors block a clean file;
// warnings are advisory.
public class ReturnValidationService : IReturnValidationService
{
    private const int MaxIssues = 200;      // cap the returned list; counts stay exact
    private const decimal TaxTolerance = 1m; // rupee rounding slack for rate-vs-tax

    private readonly IInvoiceService _invoiceService;
    private readonly IGstnReturnService _gstnReturnService;

    public ReturnValidationService(IInvoiceService invoiceService, IGstnReturnService gstnReturnService)
    {
        _invoiceService = invoiceService;
        _gstnReturnService = gstnReturnService;
    }

    public async Task<ReturnValidationResult> ValidateGstr1Async(int year, int month, CancellationToken cancellationToken = default)
    {
        var invoices = await _invoiceService.ListAsync(year, month, cancellationToken);
        var result = new ReturnValidationResult
        {
            Period = $"{year:D4}{month:D2}",
            ReturnType = "GSTR1",
            InvoicesChecked = invoices.Count,
        };

        // Collect every issue first; cap AFTER ordering (errors first) so the
        // returned list always shows all errors even when warnings dominate.
        var all = new List<ValidationIssue>();
        void Add(string severity, string code, string message, InvoiceResponse inv)
        {
            if (severity == "Error") result.ErrorCount++; else result.WarningCount++;
            all.Add(new ValidationIssue
            {
                Severity = severity,
                Code = code,
                Message = message,
                InvoiceNo = inv.InvoiceNumber,
                Section = inv.Section,
            });
        }

        foreach (var inv in invoices)
        {
            var no = string.IsNullOrWhiteSpace(inv.InvoiceNumber) ? $"Bill {inv.BillId}" : inv.InvoiceNumber;

            // B2B (registered recipient) must carry a valid 15-char GSTIN.
            if (string.Equals(inv.Section, "B2B", StringComparison.OrdinalIgnoreCase) && !IsValidGstin(inv.PartyGSTIN))
                Add("Error", "INVALID_GSTIN", $"Invoice {no}: B2B supply with missing/invalid party GSTIN.", inv);

            // Every line needs a usable HSN (mandatory Table-12 dropdown). Flags the
            // items that NormalizeHsn reduces to empty (blank / "Not Defined" / junk).
            if (inv.Lines.Any(l => NormalizedHsnIsEmpty(l.HSNCode)))
                Add("Error", "MISSING_HSN", $"Invoice {no}: one or more items have no valid HSN code.", inv);

            // Tax should match rate x taxable on each line (absolute, so credit
            // notes are checked too). Flags data-entry / rate-master errors.
            if (inv.Lines.Any(LineTaxMismatch))
                Add("Warning", "TAX_MISMATCH", $"Invoice {no}: line tax doesn't match rate x taxable value.", inv);
        }
        // NOTE: place-of-supply is intentionally NOT validated here. The SP-sourced
        // InvoiceResponse doesn't populate PlaceOfSupply/PosStateCode (the GSTN JSON
        // builder derives POS from the party GSTIN / state downstream), so a POS
        // check at this layer would false-positive on every invoice.

        // Whole-return tie-out. Building the JSON is the only way to know what
        // actually reaches a table, and the builder refuses to produce a return
        // that leaves invoices out — catch that here so the preview shows the
        // reason instead of the filing flow dying on an unhandled exception.
        try
        {
            await _gstnReturnService.BuildGstr1Async(year, month, cancellationToken);
        }
        catch (Gstr1UnreportedInvoicesException ex)
        {
            result.ErrorCount += ex.InvoiceCount;
            all.Add(new ValidationIssue
            {
                Severity = "Error",
                Code = "NOT_IN_ANY_TABLE",
                Message = $"{ex.InvoiceCount} invoice(s) totalling Rs {ex.TaxableValue:N2} taxable / Rs {ex.Tax:N2} tax "
                        + $"would not appear anywhere in the return: {string.Join(", ", ex.InvoiceNumbers)}"
                        + (ex.InvoiceCount > ex.InvoiceNumbers.Count ? $" (+{ex.InvoiceCount - ex.InvoiceNumbers.Count} more)" : string.Empty),
                InvoiceNo = ex.InvoiceNumbers.FirstOrDefault() ?? string.Empty,
                Section = string.Empty,
            });
        }

        // Errors first, then warnings; cap the returned list (counts stay exact).
        var ordered = all.OrderBy(i => i.Severity == "Error" ? 0 : 1).ToList();
        result.IssuesTruncated = ordered.Count > MaxIssues;
        result.Issues = ordered.Take(MaxIssues).ToList();
        return result;
    }

    private static bool LineTaxMismatch(InvoiceLineResponse l)
    {
        if (l.GstRate <= 0m) return false; // nil/exempt lines carry no rate to check
        var expected = decimal.Round(l.TaxableValue * l.GstRate / 100m, 2);
        var actual = l.IGST + l.CGST + l.SGST;
        var tolerance = Math.Max(TaxTolerance, Math.Abs(l.TaxableValue) * 0.005m); // ₹1 or 0.5%
        return Math.Abs(Math.Abs(expected) - Math.Abs(actual)) > tolerance;
    }

    // A real GSTIN is exactly 15 alphanumeric characters (mirrors InvoiceService).
    private static bool IsValidGstin(string? raw)
    {
        var g = (raw ?? string.Empty).Trim();
        return g.Length == 15 && g.All(char.IsLetterOrDigit);
    }

    // True when the code has no valid HSN after canonicalization (digits only, at
    // a valid GSTN level 8/6/4) — mirrors InvoiceService.NormalizeHsn.
    private static bool NormalizedHsnIsEmpty(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length < 4;
    }
}
