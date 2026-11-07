using System.Net;

namespace Acme.Application.Messaging;

/// <summary>
/// The shell every transactional email shares: welcome, verify, reset.
/// </summary>
/// <remarks>
/// <para>
/// Extracted once there were four of these rather than one. The invitation
/// email was written first and its markup was copied by hand for each new
/// message, which is how three of them ended up with slightly different link
/// colours and only one of them escaped the recipient's name.
/// </para>
/// <para>
/// <b>Escaping happens here, at the boundary.</b> Callers pass plain text and
/// this encodes it. The alternative — every caller remembering
/// <c>WebUtility.HtmlEncode</c> — is a rule that holds until someone is in a
/// hurry, and a display name is attacker-controlled on a product with open
/// registration.
/// </para>
/// <para>
/// Deliberately plain markup: a table-free single column, inline styles, no
/// web fonts, no images. Pharmacy mail is routinely read through locked-down
/// clients, and the one thing each of these emails must do is show a link that
/// works.
/// </para>
/// </remarks>
public static class TransactionalEmail
{
    public const string ProductName = "Marketing25";

    private const string Ink = "#0F2E33";
    private const string Muted = "#5A7175";
    private const string Brand = "#0E7C86";

    /// <summary>A call-to-action button and the same URL as copyable text.</summary>
    /// <remarks>
    /// Both, always. Some clients drop anchors, some rewrite them, and some
    /// people forward the message to a colleague who reads it in something
    /// stranger still. A visible URL costs two lines and rescues all three.
    /// </remarks>
    public sealed record Action(string Label, string Url);

    /// <summary>
    /// Builds the HTML and plain-text pair for a message.
    /// </summary>
    /// <param name="heading">Title line. Plain text; encoded here.</param>
    /// <param name="greetingName">First name, or null for a generic opening.</param>
    /// <param name="paragraphs">Body copy, in order. Plain text; encoded here.</param>
    /// <param name="action">The button, if the message has one.</param>
    /// <param name="footnotes">Smaller print below the action.</param>
    public static (string Html, string Text) Build(
        string heading,
        string? greetingName,
        IReadOnlyList<string> paragraphs,
        Action? action = null,
        IReadOnlyList<string>? footnotes = null)
    {
        var notes = footnotes ?? Array.Empty<string>();
        var greeting = string.IsNullOrWhiteSpace(greetingName)
            ? "Hi there,"
            : $"Hi {greetingName!.Trim()},";

        /* ── HTML ── */

        var body = string.Join("\n      ", paragraphs.Select(p =>
            $"<p style=\"margin:0 0 14px\">{WebUtility.HtmlEncode(p)}</p>"));

        var actionHtml = action is null ? "" : $"""
              <p style="margin:26px 0">
                <a href="{WebUtility.HtmlEncode(action.Url)}"
                   style="background:{Brand};color:#ffffff;text-decoration:none;
                          padding:12px 22px;border-radius:8px;display:inline-block;
                          font-weight:600">{WebUtility.HtmlEncode(action.Label)}</a>
              </p>
              <p style="font-size:13px;color:{Muted};margin:0 0 14px">
                Or paste this into your browser:<br>
                <span style="word-break:break-all">{WebUtility.HtmlEncode(action.Url)}</span>
              </p>
        """;

        var notesHtml = notes.Count == 0 ? "" : string.Join("\n      ", notes.Select(n =>
            $"<p style=\"font-size:13px;color:{Muted};margin:0 0 10px\">{WebUtility.HtmlEncode(n)}</p>"));

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;
                        max-width:520px;margin:0 auto;color:{Ink};line-height:1.6">
              <h1 style="font-size:20px;margin:0 0 16px">{WebUtility.HtmlEncode(heading)}</h1>
              <p style="margin:0 0 14px">{WebUtility.HtmlEncode(greeting)}</p>
              {body}
            {actionHtml}
              {notesHtml}
            </div>
            """;

        /* ── plain text ── */
        //
        // Not an afterthought. A message with no text part scores worse with
        // spam filters, and these are the emails that must not be filtered.

        var lines = new List<string> { heading, "", greeting, "" };
        lines.AddRange(paragraphs.SelectMany(p => new[] { p, "" }));

        if (action is not null)
        {
            lines.Add(action.Label + ":");
            lines.Add(action.Url);
            lines.Add("");
        }

        lines.AddRange(notes.SelectMany(n => new[] { n, "" }));

        return (html, string.Join("\n", lines).TrimEnd());
    }

    /// <summary>
    /// Assembles a message addressed to one person.
    /// </summary>
    /// <remarks>
    /// <b>FromAddress and FromName are left empty on purpose.</b> Both senders
    /// resolve message-first, so a value here would override the address the
    /// environment is configured — and the provider verified — to send from.
    /// See <c>EmailSenderTests</c>, which exists because two emails once did.
    /// </remarks>
    public static EmailMessage To(
        string recipient,
        string subject,
        string heading,
        string? greetingName,
        IReadOnlyList<string> paragraphs,
        Action? action = null,
        IReadOnlyList<string>? footnotes = null)
    {
        var (html, text) = Build(heading, greetingName, paragraphs, action, footnotes);

        return new EmailMessage(
            FromAddress: string.Empty,
            FromName: string.Empty,
            To: new[] { recipient },
            Subject: subject,
            HtmlBody: html,
            TextBody: text,
            Attachments: Array.Empty<EmailAttachment>());
    }

    /// <summary>
    /// First word of a display name, for the greeting.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty string when there is nothing usable, so the
    /// caller gets "Hi there," instead of "Hi ," — which looks like a broken
    /// mail merge and undermines a message whose job is to look legitimate
    /// enough to click.
    /// </remarks>
    public static string? FirstName(string? displayName)
    {
        var first = (displayName ?? string.Empty).Trim().Split(' ').FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }
}
