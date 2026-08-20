namespace GSTAutoPilot.Domain.Entities;

public class GSTR2B
{
    public Guid GSTR2BId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string SupplierGSTIN { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CGSTAmount { get; set; }
    public decimal SGSTAmount { get; set; }
    public string FilingPeriod { get; set; } = string.Empty;
    public DateTime FetchedOn { get; set; } = DateTime.UtcNow;

    // GSTR-2B section: "B2B" (supplier invoices) or "IMPG" (import of goods,
    // from customs Bills of Entry / ICEGATE). Defaults to B2B for back-compat.
    public string RecordType { get; set; } = Gstr2bRecordType.B2B;

    // Provenance of the row: "GSTN" / "GSTN (N files)" for a real pull from the
    // portal. Null on legacy rows that predate this column (which may be stale
    // mock data from before the mock path was removed). Lets the UI and recon
    // flag whether the ITC figures came from a genuine GSTN fetch.
    public string? Source { get; set; }

    // GSTR-2B per-invoice ITC availability (itcavl = "Y"/"N"). GSTN flags credit
    // as unavailable for e.g. PoS-rule supplies or section 16(4) time-barred
    // invoices; such credit must NOT be claimed even if it's in the books.
    // Defaults to true (available) — absent/legacy rows are treated as eligible.
    public bool IsItcEligible { get; set; } = true;

    // GSTR-2B reason code (rsn) when IsItcEligible is false — why the portal
    // marked the credit unavailable. Null when eligible.
    public string? ItcIneligibleReason { get; set; }
}

public static class Gstr2bRecordType
{
    public const string B2B = "B2B";
    public const string IMPG = "IMPG";
    // Credit/Debit notes from suppliers (GSTR-2B CDNR). Credit notes are stored
    // with negative amounts so 2B totals net down; kept out of the B2B recon
    // bucket (matched separately, not invoice-for-invoice).
    public const string CDNR = "CDNR";
    // Amendments (revised supplier invoices / notes for prior periods). They
    // carry the revised values and reconcile like their base type.
    public const string B2BA = "B2BA";
    public const string CDNRA = "CDNRA";
    // Input Service Distributor credit (GSTR-2B ISD). Distributed ITC — counted
    // in the available-ITC total but not reconciled invoice-for-invoice.
    public const string ISD = "ISD";
}
