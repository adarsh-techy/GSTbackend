namespace GSTAutoPilot.Application.DTOs;

public class GstOtpVerifyRequest
{
    public string Txn { get; set; } = string.Empty;
    public string Otp { get; set; } = string.Empty;
}
