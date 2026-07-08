namespace Acme.Application.Messaging;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    string FromAddress,
    string FromName,
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? TextBody,
    IReadOnlyList<EmailAttachment> Attachments);

public sealed record EmailDispatchResult(int Sent, int Failed, string? Note);

public interface IEmailSender
{
    Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct);
}
