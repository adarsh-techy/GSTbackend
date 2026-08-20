namespace GSTAutoPilot.Application.DTOs;

public class InvoiceResponse
{
    public Guid Id { get; set; }
    public int BillId { get; set; }
    // e-Invoice status: "Done" (IRN exists), "Required" (>5L, no IRN), "NA".
    public string EInvoiceStatus { get; set; } = "NA";
    // The generated IRN for this bill, if any (empty when none).
    public string Irn { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public string PartyName { get; set; } = string.Empty;
    public string PartyGSTIN { get; set; } = string.Empty;
    public string PlaceOfSupply { get; set; } = string.Empty;
    // 2-digit GST place-of-supply state code of the BUYER (from CarolERP
    // Account.StateId). Used to fix POS for B2C / unregistered supplies, where
    // there is no buyer GSTIN to read the state from. Empty when unknown or
    // foreign (export).
    public string PosStateCode { get; set; } = string.Empty;
    // GSTR-1 classification: "B2B" | "Export" | "B2C" | "CDN" (credit/debit
    // note), plus the source document-mapping category it came from.
    public string Section { get; set; } = "B2C";
    public string GstCategory { get; set; } = string.Empty;
    public decimal TaxableValue { get; set; }
    // Invoice-level discount, derived as (sum of line Rate x Qty) - taxable, so
    // it does NOT depend on the per-install discount column name (DiscAmt /
    // DiscAmount). 0 when there's no discount. Taxable is already net of it.
    public decimal Discount { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    // Invoice-level adjustment from CarolERP Bill_Tax (round-off, misc charges,
    // discount), already signed (+/-) per Tax.ValEffect. Folded into TotalAmount;
    // exposed separately so the UI / PDF can show a "Round Off" line. Label is
    // the Tax.TaxName(s) (e.g. "Round Off(-)").
    public decimal RoundOff { get; set; }
    public string RoundOffLabel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    // Multi-company: which company in CarolERP this bill belongs to.
    // Resolved via header.DocId → Documents.CoId. Null if the row's DocId is
    // unmapped to a company.
    public byte? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public List<InvoiceLineResponse> Lines { get; set; } = new();
}

// Light-weight item for the header company selector. CarolCompany lives in
// CarolERP; this DTO is the API contract for the in-app dropdown.
public class CompanySummaryDto
{
    public byte CoId { get; set; }
    public string CoName { get; set; } = string.Empty;
    public string? GstNo { get; set; }
    public int? StateId { get; set; }
    public int BillCount { get; set; }
}

public class InvoiceLineResponse
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string HSNCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstRate { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    public decimal Total { get; set; }
    // Compensation cess on the line. 0 for goods that attract no cess (e.g.
    // coir); flows through when the source (SP CessAmt column / ERP) carries it.
    public decimal Cess { get; set; }
}

// GSTR-1 Table 12 (HSN summary) row: one per (HSN, rate).
public class Gstr1HsnRow
{
    public string HSNCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UQC { get; set; } = "OTH-OTHERS";
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal IGST { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal Cess { get; set; }
    public decimal TotalValue { get; set; }
    // "B2B" (registered recipient / export) or "B2C" (unregistered). GSTR-1
    // Table 12 is split into these sub-tables from the May-2025 tax period.
    public string SupplyType { get; set; } = "B2B";
}

// GSTR-1 Table 13 (documents issued) row.
public class Gstr1DocRow
{
    public string DocType { get; set; } = string.Empty;
    public int Count { get; set; }
}

// GSTR-1 supplementary tables (12 HSN summary + 13 documents issued).
public class Gstr1TablesResponse
{
    public List<Gstr1HsnRow> Hsn { get; set; } = new();
    public List<Gstr1DocRow> DocsIssued { get; set; } = new();
}

public class Gstr1SummaryRow
{
    public string PartyName { get; set; } = string.Empty;
    public string PartyGSTIN { get; set; } = string.Empty;
    // "B2B" | "Export" | "B2C" — the supply nature for this party.
    public string Section { get; set; } = "B2C";
    public int InvoiceCount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal CGST { get; set; }
    public decimal SGST { get; set; }
    public decimal IGST { get; set; }
    public decimal TotalAmount { get; set; }
}
