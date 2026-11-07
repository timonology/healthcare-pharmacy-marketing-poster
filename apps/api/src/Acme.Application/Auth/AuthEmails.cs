using Acme.Application.Messaging;
using Acme.Domain.Users;

namespace Acme.Application.Auth;

/// <summary>
/// The account emails: welcome, password reset, verify address.
/// </summary>
/// <remarks>
/// <para>
/// All three go through <see cref="TransactionalEmail"/>, which escapes every
/// value it is given. Nothing here calls <c>HtmlEncode</c>, and nothing here
/// should — the display name is attacker-controlled on a product with open
/// registration, and "remember to escape at each call site" is a rule that
/// survives until someone is in a hurry.
/// </para>
/// <para>
/// Wording matters more than usual in these. A password-reset email is the
/// exact shape of a phishing message, so it says plainly what it will and will
/// not do, and it never asks for anything back.
/// </para>
/// </remarks>
public static class AuthEmails
{
    public static EmailMessage Welcome(User user, string appLink) =>
        TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: $"Welcome to {TransactionalEmail.ProductName}",
            heading: $"Welcome to {TransactionalEmail.ProductName}",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: new[]
            {
                "Your account is ready. You can design pharmacy posters, send campaigns "
                + "to your patients, and publish to your social channels — all from one place.",
                "A good first step is to set up your brand kit, so every poster you make "
                + "already carries your pharmacy's colours and logo.",
            },
            action: new TransactionalEmail.Action("Open my dashboard", appLink),
            footnotes: new[]
            {
                "If you did not create this account, please let us know by replying to this email.",
            });

    /// <summary>
    /// The reset link.
    /// </summary>
    /// <remarks>
    /// The expiry is stated in the body because it changes behaviour: someone
    /// who opens the email the next morning needs to know to request another,
    /// rather than concluding the product is broken.
    /// </remarks>
    public static EmailMessage PasswordReset(User user, string resetLink, DateTime expiresAtUtc) =>
        TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: $"Reset your {TransactionalEmail.ProductName} password",
            heading: "Reset your password",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: new[]
            {
                "We received a request to reset the password for this account. "
                + "Choose a new one here:",
            },
            action: new TransactionalEmail.Action("Choose a new password", resetLink),
            footnotes: new[]
            {
                $"This link works once, and stops working at "
                    + $"{expiresAtUtc:HH:mm} UTC on {expiresAtUtc:dddd d MMMM}.",
                "If you did not ask for this, you can ignore this email — your password "
                + "has not changed, and nobody can use this link without opening it from "
                + "your inbox.",
                "We will never ask you for your password by email.",
            });

    public static EmailMessage VerifyEmail(User user, string verifyLink, DateTime expiresAtUtc) =>
        TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: $"Confirm your email address",
            heading: "Confirm your email address",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: new[]
            {
                "Please confirm this is the right address for your "
                + $"{TransactionalEmail.ProductName} account. It is where campaign reports "
                + "and any password resets will be sent.",
            },
            action: new TransactionalEmail.Action("Confirm my address", verifyLink),
            footnotes: new[]
            {
                $"This link stops working at {expiresAtUtc:HH:mm} UTC on "
                    + $"{expiresAtUtc:dddd d MMMM}.",
                "If you did not create this account, you can ignore this email.",
            });

    /// <summary>
    /// Sent after a password actually changes.
    /// </summary>
    /// <remarks>
    /// The email nobody asks for and everybody wants. If an intruder resets a
    /// password, this is the only signal the real owner gets — the reset email
    /// itself went to a mailbox the attacker already controls, or was deleted.
    /// It deliberately has no action button: there is nothing to click, which
    /// makes it useless to anyone spoofing it.
    /// </remarks>
    public static EmailMessage PasswordChanged(User user, DateTime changedAtUtc) =>
        TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: $"Your {TransactionalEmail.ProductName} password was changed",
            heading: "Your password was changed",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: new[]
            {
                $"The password for this account was changed at {changedAtUtc:HH:mm} UTC "
                + $"on {changedAtUtc:dddd d MMMM}.",
                "You have been signed out everywhere, so you will need to sign in again "
                + "with the new password.",
            },
            footnotes: new[]
            {
                "If this was not you, someone else may have access to this mailbox. "
                + "Reset the password again immediately and contact us.",
            });
}
