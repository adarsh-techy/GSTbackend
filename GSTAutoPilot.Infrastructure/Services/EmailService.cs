using GSTAutoPilot.Application.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GSTAutoPilot.Infrastructure.Services;

public class EmailService : IEmailService
{
    public async Task SendAsync(SmtpConfig config, EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new InvalidOperationException("SMTP host is not configured.");
        if (string.IsNullOrWhiteSpace(config.FromEmail))
            throw new InvalidOperationException("SMTP From email is not configured.");
        if (string.IsNullOrWhiteSpace(message.ToEmail))
            throw new InvalidOperationException("Recipient email is required.");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(config.FromName) ? config.FromEmail : config.FromName,
            config.FromEmail));
        msg.To.Add(MailboxAddress.Parse(message.ToEmail.Trim()));
        if (!string.IsNullOrWhiteSpace(message.CcEmail))
        {
            foreach (var cc in message.CcEmail.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                msg.Cc.Add(MailboxAddress.Parse(cc));
        }
        msg.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.Body };
        foreach (var a in message.Attachments)
            body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        msg.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        var socket = config.EnableSsl
            ? (config.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
            : SecureSocketOptions.None;
        await client.ConnectAsync(config.Host.Trim(), config.Port, socket, cancellationToken);
        if (!string.IsNullOrWhiteSpace(config.Username))
            await client.AuthenticateAsync(config.Username.Trim(), config.Password, cancellationToken);
        await client.SendAsync(msg, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
