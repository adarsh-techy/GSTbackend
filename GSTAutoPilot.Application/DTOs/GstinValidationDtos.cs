namespace GSTAutoPilot.Application.DTOs;

public class GstinValidationResponse
{
    public Guid ValidationId { get; set; }
    public string GSTIN { get; set; } = string.Empty;
    public bool FormatValid { get; set; }
    public string? FormatError { get; set; }
    public string TradeName { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string StateCode { get; set; } = string.Empty;
    public string TaxpayerType { get; set; } = string.Empty;
    public DateTime? RegistrationDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string FilingFrequency { get; set; } = string.Empty;
    public string LastFiledReturn { get; set; } = string.Empty;
    public int ComplianceScore { get; set; }
    public DateTime ValidatedOn { get; set; }
    public string Source { get; set; } = "STUB";
    public bool FromCache { get; set; }
}

public class BulkValidateRequest
{
    public List<string> Gstins { get; set; } = new();
}

public class BulkValidateResponse
{
    public int Total { get; set; }
    public int Valid { get; set; }
    public int Invalid { get; set; }
    public List<GstinValidationResponse> Results { get; set; } = new();
}
