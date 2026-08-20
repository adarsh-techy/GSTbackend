namespace GSTAutoPilot.Domain.Entities;

public class GSTINValidation
{
    public Guid ValidationId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string GSTIN { get; set; } = string.Empty;
    public string TradeName { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string TaxpayerType { get; set; } = string.Empty;
    public DateTime? RegistrationDate { get; set; }
    public string Status { get; set; } = GSTINStatus.Active;
    public string FilingFrequency { get; set; } = string.Empty;
    public string LastFiledReturn { get; set; } = string.Empty;
    public int ComplianceScore { get; set; }
    public DateTime ValidatedOn { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "STUB";
}

public static class GSTINStatus
{
    public const string Active = "Active";
    public const string Cancelled = "Cancelled";
    public const string Suspended = "Suspended";
}
