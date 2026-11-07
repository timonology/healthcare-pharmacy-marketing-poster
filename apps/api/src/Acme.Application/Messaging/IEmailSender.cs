namespace Acme.Application.Messaging;

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// An image displayed inside the message body, referenced as
/// <c>&lt;img src="cid:{ContentId}"&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="EmailAttachment"/> because the two behave
/// differently in a mail client: an attachment appears in the paperclip list
/// and is downloaded on demand, an inline image is part of what the reader
/// sees and never appears as a file.
/// </para>
/// <para>
/// It is also the only workable way to put something large in a body.
/// Base64 data URLs are the obvious alternative and they are a trap:
/// <b>Gmail clips any message whose HTML exceeds roughly 102KB</b>, replacing
/// the rest with "[Message clipped]". A poster PNG encoded as a data URL runs
/// to several hundred kilobytes, so the design would be cut off in the client
/// most recipients use. An inline attachment travels as a separate MIME part
/// and does not count towards that budget.
/// </para>
/// <para>
/// The existing video thumbnails stay as data URLs on purpose — at 120px they
/// are small enough not to matter, and they dodge the image proxies that
/// cannot reach a private API host.
/// </para>
/// </remarks>
public sealed record EmailInlineImage(string ContentId, string ContentType, byte[] Content);

public sealed record EmailMessage(
    string FromAddress,
    string FromName,
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? TextBody,
    IReadOnlyList<EmailAttachment> Attachments,
    IReadOnlyList<EmailInlineImage>? InlineImages = null,
    // Marketing email only (security review R2-06): each recipient's own
    // unsubscribe links, keyed by lower-cased address. The sender puts the page
    // link where the body says UnsubscribeLinks.Placeholder and adds the
    // List-Unsubscribe headers. Null for transactional mail.
    IReadOnlyDictionary<string, UnsubscribeLinks>? Unsubscribe = null);

/// <summary>
/// One recipient's ways out of a marketing email (security review R2-06).
/// </summary>
/// <param name="PageUrl">
/// The visible link: a page on the web app with a button. Not a link that
/// unsubscribes when fetched — mail scanners fetch every link in a message.
/// </param>
/// <param name="OneClickUrl">
/// The List-Unsubscribe header's target, which the mail client POSTs to
/// (RFC 8058) when the person presses its own "Unsubscribe" button.
/// </param>
public sealed record UnsubscribeLinks(string PageUrl, string OneClickUrl)
{
    /// <summary>Stands in the body for the recipient's page link; replaced per recipient by the sender.</summary>
    public const string Placeholder = "%%UNSUBSCRIBE_URL%%";

    /// <summary>The body for one recipient: the placeholder replaced (HTML-encoded in HTML).</summary>
    public static (string Html, string? Text) Personalise(EmailMessage message, string recipient)
    {
        if (message.Unsubscribe is null
            || !message.HtmlBody.Contains(Placeholder, StringComparison.Ordinal)
               && (message.TextBody is null || !message.TextBody.Contains(Placeholder, StringComparison.Ordinal)))
            return (message.HtmlBody, message.TextBody);

        var url = message.Unsubscribe.TryGetValue(recipient.Trim().ToLowerInvariant(), out var links)
            ? links.PageUrl
            : string.Empty;
        return (
            message.HtmlBody.Replace(Placeholder, System.Net.WebUtility.HtmlEncode(url), StringComparison.Ordinal),
            message.TextBody?.Replace(Placeholder, url, StringComparison.Ordinal));
    }
}

/// <param name="Mocked">
/// True when the sender is in mock mode (<c>Messaging:UseMock</c>) and nothing
/// left the building. Counted as sent so flows behave as in production, but
/// recorded so a deployment accidentally left in mock mode cannot pass for one
/// that delivers — the smoke test's reset check fails on it.
/// </param>
public sealed record EmailDispatchResult(int Sent, int Failed, string? Note, bool Mocked = false);

public interface IEmailSender
{
    Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct);
}
