using Acme.Application.Common;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
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
                // Masked (R2-15): the mock also runs on the test environment,
                // and a campaign's recipients are patients.
                string.Join(", ", message.To.Select(PersonalData.Mask)),
                message.Subject,
                message.Attachments.Count);
            return new EmailDispatchResult(message.To.Count, 0, "Mock SMTP — no email actually sent.", Mocked: true);
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
                IsBodyHtml = true,
            };
            mail.To.Add(recipient);

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    message.TextBody, null, "text/plain"));
            }

            // The HTML goes in as an AlternateView rather than Body, because a
            // cid: reference only resolves against LinkedResources hanging off
            // a view. Setting Body and adding attachments separately gives a
            // message where the image arrives but the <img> stays broken.
            var inlineImages = message.InlineImages ?? Array.Empty<EmailInlineImage>();
            var htmlView = AlternateView.CreateAlternateViewFromString(
                message.HtmlBody, null, "text/html");

            foreach (var img in inlineImages)
            {
                var resource = new LinkedResource(new MemoryStream(img.Content), img.ContentType)
                {
                    ContentId = img.ContentId,
                    // Without this the part is offered as a download in some
                    // clients as well as being drawn in the body.
                    TransferEncoding = TransferEncoding.Base64,
                };
                htmlView.LinkedResources.Add(resource);
            }

            mail.AlternateViews.Add(htmlView);

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
                logger.LogWarning(ex, "Failed to send email to {Recipient}", PersonalData.Mask(recipient));
                failed++;
            }
        }

        return new EmailDispatchResult(sent, failed, failed > 0 ? "Some recipients failed." : null);
    }
}
