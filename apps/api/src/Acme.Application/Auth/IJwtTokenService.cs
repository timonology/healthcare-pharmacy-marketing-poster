using Acme.Domain.Users;

namespace Acme.Application.Auth;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) Issue(User user);

    /// <summary>
    /// Issues a token that acts <i>as</i> one pharmacy but records who is
    /// really driving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The subject is the effective pharmacy, not the actor.</b> That is
    /// deliberate and load-bearing: every existing endpoint reads <c>sub</c> to
    /// decide whose data it is looking at, so a delegated token works
    /// everywhere without a single controller change. The alternative — an
    /// "acting-as" header each endpoint must remember to resolve — fails at the
    /// one endpoint that forgets, and that endpoint then quietly operates on
    /// the wrong pharmacy's patient list.
    /// </para>
    /// <para>
    /// The actor is carried separately in <c>act</c>, following the delegation
    /// claim from RFC 8693. Its presence is what marks a token as delegated,
    /// which is how chaining is refused and how writes are attributed to the
    /// parent rather than the child.
    /// </para>
    /// <para>
    /// Short-lived and issued without a refresh token, so it cannot be renewed
    /// into an open-ended session over someone else's account.
    /// </para>
    /// </remarks>
    (string Token, DateTime ExpiresAtUtc) IssueDelegated(
        User effective, User actor, TimeSpan validFor);
}

/// <summary>Claim names for delegated access.</summary>
public static class DelegationClaims
{
    /// <summary>The user really performing the action. RFC 8693 calls this the actor.</summary>
    public const string Actor = "act";

    /// <summary>Actor's email, so audit entries need no extra lookup.</summary>
    public const string ActorEmail = "act_email";

    /// <summary>
    /// The actor's own role, for attribution only.
    /// </summary>
    /// <remarks>
    /// A delegated token's role is the <i>effective</i> pharmacy's, so on its
    /// own it cannot say whether an administrator or a head office is driving.
    /// The audit needs that answer — staff work is reviewed separately from
    /// pharmacy work. <b>Never used for authorization</b>: a policy reading
    /// this would hand an administrator's permissions to a delegated session.
    /// </remarks>
    public const string ActorRole = "act_role";
}
