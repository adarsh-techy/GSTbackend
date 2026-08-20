namespace GSTAutoPilot.Application.Exceptions;

// Thrown when the GSTR-1 builder finishes with invoices that reached NO table —
// not b2b, b2cl, b2cs, cdnr, cdnur or exp. Every rupee in the period's book must
// land somewhere in the return; anything left over means the return understates
// turnover and will not tie to GSTR-3B.
//
// This used to happen silently: a row classified "B2B" with no counter-party
// GSTIN matched no branch and simply disappeared (52 invoices / Rs 97.9L taxable
// in KSCC's Oct-2025 book). A filing that is quietly short is worse than one
// that refuses to build, so the builder now stops instead.
public sealed class Gstr1UnreportedInvoicesException : Exception
{
    // Invoice numbers that reached no table, capped for the message.
    public IReadOnlyList<string> InvoiceNumbers { get; }
    public int InvoiceCount { get; }
    public decimal TaxableValue { get; }
    public decimal Tax { get; }

    public Gstr1UnreportedInvoicesException(
        IReadOnlyList<string> invoiceNumbers, int invoiceCount, decimal taxableValue, decimal tax)
        : base($"{invoiceCount} invoice(s) totalling {taxableValue:N2} taxable / {tax:N2} tax would not appear in any GSTR-1 table: "
             + string.Join(", ", invoiceNumbers)
             + (invoiceCount > invoiceNumbers.Count ? $" (+{invoiceCount - invoiceNumbers.Count} more)" : string.Empty))
    {
        InvoiceNumbers = invoiceNumbers;
        InvoiceCount = invoiceCount;
        TaxableValue = taxableValue;
        Tax = tax;
    }
}
