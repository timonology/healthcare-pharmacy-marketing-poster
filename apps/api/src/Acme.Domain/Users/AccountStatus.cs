namespace Acme.Domain.Users;

/// <summary>
/// Where an account is in its lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="User.IsActive"/>, which answers a
/// different question. <c>IsActive</c> is an administrator's switch — "may this
/// account be used right now" — and applies to accounts that have been fully
/// set up. This says whether the account has ever been claimed by the person it
/// belongs to.
/// </para>
/// <para>
/// Collapsing the two would have meant an invited pharmacist appearing as
/// "deactivated", which reads as a punishment rather than an unopened
/// invitation, and would have made "reactivate" the button someone pressed
/// hoping to resend the email.
/// </para>
/// <para>
/// Persisted; do not renumber. Zero is Active so that every account written
/// before this existed deserialises as a normal, working account.
/// </para>
/// </remarks>
public enum AccountStatus
{
    /// <summary>Set up and usable. The default, and what self-registration produces.</summary>
    Active = 0,

    /// <summary>
    /// Created by a SuperAdmin, waiting for the pharmacist to set a password.
    /// Cannot sign in.
    /// </summary>
    Invited = 1,
}
