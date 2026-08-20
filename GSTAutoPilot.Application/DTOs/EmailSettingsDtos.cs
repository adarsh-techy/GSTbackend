namespace GSTAutoPilot.Application.DTOs;

// SMTP status returned to the UI — never includes the password.
public class SmtpStatusDto
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? FromName { get; set; }
    public string? FromEmail { get; set; }
    public bool EnableSsl { get; set; } = true;
    public bool HasPassword { get; set; }
    public bool IsConfigured { get; set; }
}

// Save payload. Password is write-only: blank on edit keeps the stored value.
public class SmtpConfigCommand
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string FromName { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

public class SendTestEmailRequest
{
    // Optional override; defaults to the SMTP username (per spec).
    public string? ToEmail { get; set; }
}
