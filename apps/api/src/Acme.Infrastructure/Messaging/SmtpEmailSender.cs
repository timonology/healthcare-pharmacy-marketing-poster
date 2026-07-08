using System.Net;
using System.Net.Mail;
using Acme.Application.Messaging;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Messaging;

public sealed class SmtpEmailSender(
    IOptions<MessagingOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly MessagingOptions _options = options.Value;

    public async Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (_options.UseMock)
        {
            logger.LogInformation(
                "[MOCK email] from={From} to={To} subject={Subject} attachments={Attachments}",
                message.FromAddress,
                string.Join(", ", message.To),
                message.Subject,
                message.Attachments.Count);
            return new EmailDispatchResult(message.To.Count, 0, "Mock SMTP — no email actually sent.");
        }

        var smtp = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port)
        {
            EnableSsl = _options.Smtp.EnableSsl,
            Credentials = !string.IsNullOrEmpty(_options.Smtp.Username)
                ? new NetworkCredential(_options.Smtp.Username, _options.Smtp.Password)
                : null,
        };

        // Fall back to the configured SMTP sender if the caller left it blank
        // (mirrors MailjetEmailSender behaviour so switching providers doesn't
        // require code changes).
        var fromAddress = string.IsNullOrWhiteSpace(message.FromAddress)
            ? _options.Smtp.FromAddress : message.FromAddress;
        var fromName = string.IsNullOrWhiteSpace(message.FromName)
            ? _options.Smtp.FromName : message.FromName;

        var sent = 0;
        var failed = 0;
        foreach (var recipient in message.To)
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(fromAddress, fromName),
                Subject = message.Subject,
                Body = message.HtmlBody,
                IsBodyHtml = true,
            };
            mail.To.Add(recipient);

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    message.TextBody, null, "text/plain"));
            }

            foreach (var att in message.Attachments)
            {
                var stream = new MemoryStream(att.Content);
                mail.Attachments.Add(new Attachment(stream, att.FileName, att.ContentType));
            }

            try
            {
                await smtp.SendMailAsync(mail, ct);
                sent++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send email to {Recipient}", recipient);
                failed++;
            }
        }

        return new EmailDispatchResult(sent, failed, failed > 0 ? "Some recipients failed." : null);
    }
}
