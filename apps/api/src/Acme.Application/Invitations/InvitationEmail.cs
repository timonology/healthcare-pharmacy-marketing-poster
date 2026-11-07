using System.Net;
using Acme.Application.Messaging;
using Acme.Domain.Users;

namespace Acme.Application.Invitations;

/// <summary>
/// Turns a raw token into the link the pharmacist clicks.
/// </summary>
/// <remarks>
/// An abstraction because the application layer must not know the web app's
/// address — that is deployment configuration, and hardcoding it is how a
/// staging invitation ends up pointing at production.
/// </remarks>
public interface IInvitationLinkBuilder
{
    string BuildActivationLink(string rawToken);
}

/// <summary>The activation email.</summary>
/// <remarks>
/// <para>
/// Plain text alongside the HTML, deliberately. Pharmacy mail is often read
/// through locked-down clients that strip HTML, and an activation email that
/// renders as a blank page is an account nobody can claim.
/// </para>
/// <para>
/// The link appears as visible text as well as a button, so it survives a
/// client that drops anchors and can be copied by hand if all else fails.
/// </para>
/// </remarks>
public static class InvitationEmail
{
    /// <summary>
    /// Left blank on purpose so the configured sender is used.
    /// </summary>
    /// <remarks>
    /// This used to be <c>no-reply@pharmacyposter.app</c>, and the sender
    /// resolves message-first: a non-empty value here <i>overrides</i>
    /// <c>Messaging:Mailjet:FromAddress</c>. Mailjet refuses to send from a
    /// domain that has not been verified on the account, so every invitation
    /// was being rejected at the provider while the code looked correct and
    /// the account and token were created regardless — an invited pharmacist
    /// simply never heard anything.
    ///
    /// Blank means "whatever this environment is configured and verified to
    /// send as", which is also what keeps test mail off production's sending
    /// domain. <c>EmailSenderTests</c> keeps it that way.
    /// </remarks>
    private const string FromAddress = "";

    /// <summary>Blank for the same reason — the configured display name wins.</summary>
    private const string FromName = "";

    public static EmailMessage Build(User user, string activationLink, DateTime expiresAtUtc)
    {
        var name = WebUtility.HtmlEncode(FirstName(user.DisplayName));
        var link = WebUtility.HtmlEncode(activationLink);
        var expires = expiresAtUtc.ToString("dddd d MMMM 'at' HH:mm 'UTC'");

        var html = $"""
            <div style="font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;
                        max-width:520px;margin:0 auto;color:#0F2E33;line-height:1.6">
              <h1 style="font-size:20px;margin:0 0 16px">Your Marketing25 account is ready</h1>
              <p>Hi {name},</p>
              <p>
                An account has been created for you on Marketing25, where you can design
                pharmacy posters, run patient campaigns and publish to your social channels.
              </p>
              <p>Set your password to get started:</p>
              <p style="margin:24px 0">
                <a href="{link}"
                   style="background:#0E7C86;color:#ffffff;text-decoration:none;
                          padding:12px 22px;border-radius:8px;display:inline-block;
                          font-weight:600">Activate my account</a>
              </p>
              <p style="font-size:13px;color:#5A7175">
                Or paste this into your browser:<br>
                <span style="word-break:break-all">{link}</span>
              </p>
              <p style="font-size:13px;color:#5A7175">
                This link stops working on <strong>{WebUtility.HtmlEncode(expires)}</strong>.
                If it expires, ask your administrator to send a new one.
              </p>
              <p style="font-size:13px;color:#5A7175">
                If you were not expecting this, you can ignore this email — the account
                cannot be used until someone sets a password.
              </p>
            </div>
            """;

        var text = $"""
            Your Marketing25 account is ready

            Hi {FirstName(user.DisplayName)},

            An account has been created for you on Marketing25. Set your password
            to get started:

            {activationLink}

            This link stops working on {expires}. If it expires, ask your administrator
            to send a new one.

            If you were not expecting this, you can ignore this email — the account
            cannot be used until someone sets a password.
            """;

        return new EmailMessage(
            FromAddress: FromAddress,
            FromName: FromName,
            To: new[] { user.Email.Value },
            Subject: "Activate your Marketing25 account",
            HtmlBody: html,
            TextBody: text,
            Attachments: Array.Empty<EmailAttachment>());
    }

    /// <summary>
    /// First word of the display name, for the greeting.
    /// </summary>
    /// <remarks>
    /// Falls back to "there" rather than printing an empty greeting. "Hi ,"
    /// looks like a broken mail merge and undermines a message whose whole job
    /// is to look legitimate enough to click.
    /// </remarks>
    private static string FirstName(string displayName)
    {
        var first = (displayName ?? string.Empty).Trim().Split(' ').FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "there" : first;
    }
}
