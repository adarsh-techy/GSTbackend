namespace GSTAutoPilot.Application.DTOs;

// A customs Bill of Entry row (import IGST credit) as shown in the UI.
public class BillOfEntryDto
{
    public int BoEId { get; set; }
    public string Period { get; set; } = string.Empty;
    public string BoENumber { get; set; } = string.Empty;
    public DateTime BoEDate { get; set; }
    public string? PortCode { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierGSTIN { get; set; }
    public decimal AssessableValue { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CessAmount { get; set; }
    public string? Remarks { get; set; }
}

// Create/update payload (BoEId comes from the route on update).
public class SaveBillOfEntryCommand
{
    public string Period { get; set; } = string.Empty;
    public string BoENumber { get; set; } = string.Empty;
    public DateTime BoEDate { get; set; }
    public string? PortCode { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierGSTIN { get; set; }
    public decimal AssessableValue { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CessAmount { get; set; }
    public string? Remarks { get; set; }
}

// Period rollup used by GSTR-3B import ITC.
public class BillOfEntryPeriodTotals
{
    public int Count { get; set; }
    public decimal AssessableValue { get; set; }
    public decimal IGSTAmount { get; set; }
    public decimal CessAmount { get; set; }
}
