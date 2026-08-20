namespace GSTAutoPilot.Domain.Entities;

// Universal per-tenant CarolERP document mapping. One row per (TenantId,
// GstCategory) tells the engine which CarolERP header + line table holds that
// category of document, and which CarolERP *DocType* (+ optional *SubType*)
// codes flag it.
//
// DocType/SubType are the PORTABLE codes from the CarolERP `Documents` master
// (Documents.DocType / Documents.SubType), NOT the install-specific
// Bill_Mas.DocId. At read time the engine joins `Documents` to resolve these
// codes into this install's actual DocId set, then filters the header table by
// those DocIds. This makes a mapping portable across CarolERP installs whose
// DocId numbering differs.
//
// This table is the single source of truth for the CarolERP read path; the
// legacy three-column sales profile on the Tenant row is only a fallback for
// tenants that have no mapping rows yet.
public class DocumentMapping
{
    public int MappingId { get; set; }
    public Guid TenantId { get; set; }

    // One of GstDocumentCatalog categories. Unique per tenant.
    public string GstCategory { get; set; } = string.Empty;

    // Human label shown in the Settings UI, e.g. "Custom Export Sales".
    public string DisplayName { get; set; } = string.Empty;

    // CarolERP header table — Bill_Mas or Bill_File_mas.
    public string HeaderTable { get; set; } = string.Empty;

    // CarolERP line table — Bill_Exp_trn, Bill_Ls_Trn, Bill_Inp_trn,
    // Bill_Lp_trn, Bill_File_trn, Bill_DrCr_Items, Bill_Serv_Trn, Bill_Gen_Trn.
    public string LineTable { get; set; } = string.Empty;

    // Comma-separated Documents.DocType codes that flag this category,
    // e.g. "130" or "50". Null/blank => no DocType filter (all documents in
    // the header table).
    public string? DocTypes { get; set; }

    // Comma-separated Documents.SubType codes to narrow within DocTypes,
    // e.g. "0" (Purchase Bill) vs "1" (Import Purchase Bill) under DocType 130.
    // Null/blank => all SubTypes of the selected DocTypes.
    public string? SubTypes { get; set; }

    // true = outward supply (feeds Invoices / GSTR-1 / output tax),
    // false = inward supply (feeds GSTR-3B ITC / reconciliation).
    public bool IsOutward { get; set; } = true;

    // Display order in the Settings grid.
    public int SortOrder { get; set; }

    // How line tax is laid out: "IGST", "CGSTSGST", or "AUTO" (use whatever
    // tax columns the line table carries). Advisory — the line reader is
    // schema-driven.
    public string TaxMode { get; set; } = GstDocumentCatalog.TaxModeAuto;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;
}
