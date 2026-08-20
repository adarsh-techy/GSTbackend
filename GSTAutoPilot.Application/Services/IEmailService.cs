namespace GSTAutoPilot.Application.Services;

public record SmtpConfig(
    string Host,
    int Port,
    string Username,
    string Password,
    string FromName,
    string FromEmail,
    bool EnableSsl);

public record EmailAttachment(string FileName, byte[] Content, string ContentType);

public record EmailMessage(
    string ToEmail,
    string? CcEmail,
    string Subject,
    string Body,
    IReadOnlyList<EmailAttachment> Attachments);

public interface IEmailService
{
    Task SendAsync(SmtpConfig config, EmailMessage message, CancellationToken cancellationToken = default);
}
