using System.ComponentModel.DataAnnotations.Schema;

namespace GSTAutoPilot.Infrastructure.CarolERP.Entities;

// CarolERP `Documents` master. Maps each install-specific DocId (used on
// Bill_Mas.DocId / Bill_File_mas.DocId) to a portable DocType + SubType code
// and a human DocName. Document Mappings filter by DocType/SubType, which we
// resolve to the install's DocId set through this table.
public class CarolDocument
{
    public short DocId { get; set; }
    public short DocType { get; set; }
    public byte SubType { get; set; }
    public string? DocName { get; set; }
    public string? DocCode { get; set; }
    // Document-series prefix (e.g. "CC", "CC/SB/") used to build the printed
    // invoice number: Prefix + "/" + BillNumber. Present on KSCC installs; may
    // be absent on others, so reads go through DocIdToPrefixMapAsync which
    // probes for the column first and skips it when missing.
    public string? Prefix { get; set; }
    // GstReverse on Documents exists only on Flooratex-flavor installs; the
    // GstReverse flag we actually use is on the bill HEADER (Bill_Mas), not
    // Documents. Don't map it here or KSCC queries break with "Invalid column".
    // Sanction column drifts between CarolERP installs: Flooratex has
    // `Sanction`, KSCC has `SanctionRequired`. We deliberately DON'T map it
    // on the entity — instead CarolERPDbContext.SanctionRequiredDocIdsAsync
    // probes sys.columns at read-time and issues raw SQL with the right name.
    // Multi-company support: each Document row belongs to a specific company
    // (by CoId) and optionally records the GST registration the document was
    // issued under. Bill_Mas / Bill_File_mas don't have a CoId column — the
    // bill's company is resolved through this row: header.DocId → Documents.CoId.
    // Multiple companies can share the same GstNo (sister companies under one
    // GST registration), so filtering should usually be by CoId not GstNo.
    public byte CoId { get; set; }
    // Documents.GSTNo (uppercase in SQL) — usually NULL; falls back to
    // company.GstNo. Mapped with [Column] because EF would otherwise expect
    // "GstNo" (PascalCase) and miss the SQL column "GSTNo".
    [Column("GSTNo")]
    public string? GstNo { get; set; }
}
