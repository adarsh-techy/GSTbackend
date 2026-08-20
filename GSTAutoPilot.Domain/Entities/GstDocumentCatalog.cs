namespace GSTAutoPilot.Domain.Entities;

// The fixed catalogue of GST document categories GSTAutoPilot understands, with
// the default CarolERP wiring for each — keyed by the portable CarolERP
// `Documents.DocType` (+ optional SubType) codes rather than install-specific
// DocIds, so a fresh tenant seeds correctly on any standard CarolERP install.
//
// Defaults below reflect the verified Flooratex / standard-CarolERP codes:
//   DocType 130 = Purchase Bill (SubType 0) / Import Purchase Bill (SubType 1)
//   DocType 135 = Invoice (export, SubType 0) / Packing List (SubType 1)
//   DocType 50  = Local / Inter-State / Amazon Sales
//   DocType 425 = Local Purchase Bill
//   DocType 455 = Debit Note (SubType 0) / Credit Note (SubType 1)
// Admins tune any of these via Settings -> Document Mapping (Discover lists the
// DocTypes actually present in a header table).
public static class GstDocumentCatalog
{
    public const string TaxModeIgst = "IGST";
    public const string TaxModeCgstSgst = "CGSTSGST";
    public const string TaxModeAuto = "AUTO";

    // Category keys (stored verbatim in DocumentMapping.GstCategory).
    public const string ExportSales = "ExportSales";
    public const string LocalSales = "LocalSales";
    public const string SalesBill = "SalesBill";
    // Outward sales whose lines live in Bill_Lp_trn (unusual — that table is
    // normally inward local-purchase). KSCC "Sales bill - Coir/Fibre"
    // (DocType 820) keeps its sales lines here. Needs its own category because
    // LocalPurchase already owns Bill_Lp_trn and (TenantId,GstCategory) is unique.
    public const string LocalSalesCoir = "LocalSalesCoir";
    public const string Purchase = "Purchase";
    public const string ImportPurchase = "ImportPurchase";
    public const string LocalPurchase = "LocalPurchase";
    public const string ServiceBill = "ServiceBill";
    public const string DebitNote = "DebitNote";
    public const string CreditNote = "CreditNote";
    // Outward sales debit note — INCREASES output tax (price/qty upward
    // revision). Counterpart of CreditNote; lines in Bill_DrCr_Items, summed
    // with the default +1 sign. KSCC DocType 910 SubType 0.
    public const string SalesDebitNote = "SalesDebitNote";
    public const string GeneralPurchase = "GeneralPurchase";
    // Inward general-expense journal ITC (freight, rent, professional, AMC…)
    // booked through the general-voucher module. Distinct from GeneralPurchase
    // because its lines live in the double-entry Bill_General table, not
    // Bill_Gen_Trn. KSCC DocType 930.
    public const string GeneralExpense = "GeneralExpense";
    // Inward credit note (purchase return / supplier credit) — NETS DOWN ITC.
    public const string PurchaseCreditNote = "PurchaseCreditNote";

    // Categories whose tax NETS DOWN the ITC side of GSTR-3B Table 4 (their
    // amounts are stored positive in CarolERP, so the consumer subtracts them).
    public static bool ReducesItc(string? category)
        => string.Equals(category, PurchaseCreditNote, StringComparison.OrdinalIgnoreCase);

    // Categories whose tax NETS DOWN the OUTPUT side of GSTR-3B Table 3.1 /
    // GSTR-1 (sales credit notes — CarolERP stores them positive, so the
    // consumer subtracts them from output liability).
    public static bool ReducesOutputTax(string? category)
        => string.Equals(category, CreditNote, StringComparison.OrdinalIgnoreCase);

    // Categories backed by a journal/voucher table where many bills legitimately
    // carry NO GST line (the reader filters to GST-bearing rows). For these the
    // taxable value must come ONLY from the lines — never fall back to the
    // header total, which would inflate the taxable base with non-GST journals.
    public static bool LinesOnlyTaxable(string? category)
        => string.Equals(category, GeneralExpense, StringComparison.OrdinalIgnoreCase);

    public sealed record CategoryDefault(
        string GstCategory,
        string DisplayName,
        bool IsOutward,
        string HeaderTable,
        string LineTable,
        string? DocTypes,
        string? SubTypes,
        string TaxMode,
        int SortOrder,
        bool SeedActive);

    // Order here is both the seed SortOrder and the Settings display order.
    public static readonly IReadOnlyList<CategoryDefault> Defaults = new[]
    {
        new CategoryDefault(ExportSales,     "Custom Export Sales",        true,  "Bill_Mas", "Bill_Exp_trn",    "135", "0",  TaxModeIgst,     1, true),
        new CategoryDefault(LocalSales,      "Local Sales - GST",          true,  "Bill_Mas", "Bill_Ls_Trn",     "50",  null, TaxModeCgstSgst, 2, true),
        // Second local-sales document shape: some CarolERP installs (e.g. KSCC)
        // keep their main "Sales Bill" lines in Bill_Exp_trn rather than
        // Bill_Ls_Trn. Seeded inactive with no DocTypes — admins point it at the
        // right DocType per tenant. SalesLineProvider has a KSCC Bill_Exp_trn
        // reader that yields TaxableAmt + CGST/SGST/IGST from those lines.
        new CategoryDefault(SalesBill,       "Sales Bill",                 true,  "Bill_Mas", "Bill_Exp_trn",    null,  null, TaxModeAuto,     2, false),
        // Outward sales lines stored in Bill_Lp_trn (KSCC "Sales bill - Coir",
        // DocType 820). Seeded inactive with no DocTypes; admins point it at the
        // tenant's coir-sales DocType. SalesLineProvider's KSCC Bill_Lp_trn
        // reader yields TaxableAmt(/Amount-Disc) + CGST/SGST/IGST from the lines.
        new CategoryDefault(LocalSalesCoir,  "Local Sales - Coir/Fibre",   true,  "Bill_Mas", "Bill_Lp_trn",     null,  null, TaxModeCgstSgst, 2, false),
        new CategoryDefault(Purchase,        "Purchase Bill",              false, "Bill_Mas", "Bill_Inp_trn",    "130", "0",  TaxModeAuto,     3, true),
        new CategoryDefault(ImportPurchase,  "Import Purchase Bill",       false, "Bill_Mas", "Bill_Inp_trn",    "130", "1",  TaxModeIgst,     4, true),
        new CategoryDefault(LocalPurchase,   "Local Purchase Bill",        false, "Bill_Mas", "Bill_Lp_trn",     "425", null, TaxModeCgstSgst, 5, true),
        new CategoryDefault(DebitNote,       "Debit Note",                 false, "Bill_Mas", "Bill_DrCr_Items", "455", "0",  TaxModeAuto,     6, false),
        new CategoryDefault(CreditNote,      "Credit Note",                true,  "Bill_Mas", "Bill_DrCr_Items", "455", "1",  TaxModeAuto,     7, false),
        // Outward sales debit note — adds to output tax (default +1 sign). Lines
        // in Bill_DrCr_Items; seeded inactive, admins point it at the tenant's
        // sales-debit-note DocType (KSCC = 910 SubType 0).
        new CategoryDefault(SalesDebitNote,  "Sales Debit Note",           true,  "Bill_Mas", "Bill_DrCr_Items", null,  null, TaxModeAuto,     7, false),
        new CategoryDefault(ServiceBill,     "Service Bill",               false, "Bill_Mas", "Bill_Serv_Trn",   null,  null, TaxModeAuto,     8, false),
        new CategoryDefault(GeneralPurchase, "General Purchase",           false, "Bill_Mas", "Bill_Gen_Trn",    null,  null, TaxModeAuto,     9, false),
        // General-expense journal ITC — lines in the double-entry Bill_General
        // table (KSCC DocType 930). Seeded inactive/no-DocTypes; admins point it
        // at the tenant's general-voucher DocType. The KSCC Bill_General reader
        // filters to GST-bearing expense Dr lines.
        new CategoryDefault(GeneralExpense,  "General Expense (ITC)",      false, "Bill_Mas", "Bill_General",    null,  null, TaxModeAuto,     9, false),
        // Inward credit note — reduces ITC (see ReducesItc). Lines in
        // Bill_DrCr_Items; seeded inactive, admins point it at the tenant's
        // purchase-credit-note DocType (KSCC = 900).
        new CategoryDefault(PurchaseCreditNote, "Purchase Credit Note",    false, "Bill_Mas", "Bill_DrCr_Items", null,  null, TaxModeAuto,    10, false),
    };

    public static bool IsKnownCategory(string? category)
        => category is not null && Defaults.Any(d => d.GstCategory == category);
}
