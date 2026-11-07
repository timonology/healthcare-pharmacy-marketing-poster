using Acme.Domain.Common;
using Acme.Domain.Subscriptions;

namespace Acme.Domain.Users;

public sealed class User : Entity
{
    public Email Email { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public PasswordHash PasswordHash { get; private set; } = default!;
    public SubscriptionTier Tier { get; private set; } = SubscriptionTier.Free;
    public PharmacyProfile Profile { get; private set; } = PharmacyProfile.Empty();
    public BillingProfile Billing { get; private set; } = BillingProfile.Empty();

    public UserRole Role { get; private set; } = UserRole.PharmacyUser;

    /// <summary>
    /// The Admin responsible for this pharmacy user, if any. Null for
    /// unassigned users and for staff accounts — admins are not managed by
    /// other admins.
    /// </summary>
    public string? ManagedByAdminId { get; private set; }

    /// <summary>
    /// Soft-delete. Deactivated users keep all their data and can be restored;
    /// login is refused while this is false. Preferred over hard deletion
    /// because a pharmacy's posters, campaigns, and patient lists have real
    /// retention value and deletion is unrecoverable.
    /// </summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Whether the account has been claimed by the person it belongs to.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsActive"/> on purpose — see
    /// <see cref="AccountStatus"/>. Defaults to Active so every account written
    /// before invitations existed keeps working exactly as it did.
    /// </remarks>
    public AccountStatus Status { get; private set; } = AccountStatus.Active;

    /// <summary>
    /// The pharmacy this one sits under, or null for a top-level pharmacy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One level only. A child cannot itself have children — enforced when the
    /// link is made, because arbitrary depth would need cycle detection and
    /// recursive permission checks on every read, and a franchise-of-franchise
    /// structure is not something this product has been asked for.
    /// </para>
    /// <para>
    /// The child remains a full account: it signs in, owns its own patients and
    /// posters, and works whether or not the parent ever looks at it. The
    /// parent gets access <i>in addition</i>, not instead.
    /// </para>
    /// </remarks>
    public string? ParentPharmacyId { get; private set; }

    /// <summary>True when this account sits under another pharmacy.</summary>
    public bool IsChildPharmacy => !string.IsNullOrEmpty(ParentPharmacyId);

    /// <summary>
    /// When this pharmacy was made a head office, or null if it never was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A head office runs branches: it can create sub-pharmacy accounts, see
    /// across them, and switch into them. That is a materially larger account
    /// than an ordinary pharmacy — creating a branch creates a real login and
    /// emails a credential to whatever address was typed — so it is granted by
    /// Marketing25 rather than assumed by anyone who asks.
    /// </para>
    /// <para>
    /// A timestamp rather than a boolean: "since when" is the first question
    /// asked about a privilege, and a bare true tells nobody anything. Withdrawn
    /// by setting it back to null, which leaves existing branches attached — the
    /// grant governs creating new ones, and quietly orphaning a group would be a
    /// far worse outcome than a head office that can no longer expand.
    /// </para>
    /// </remarks>
    public DateTime? HeadOfficeGrantedAtUtc { get; private set; }

    /// <summary>True when this pharmacy may run branches.</summary>
    public bool IsHeadOffice => HeadOfficeGrantedAtUtc is not null;

    /// <summary>Grants head-office status. Idempotent — re-granting keeps the original date.</summary>
    public void GrantHeadOffice()
    {
        if (HeadOfficeGrantedAtUtc is not null) return;
        HeadOfficeGrantedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Withdraws head-office status. Existing branches are deliberately left
    /// attached; only the ability to add more is removed.
    /// </summary>
    public void RevokeHeadOffice()
    {
        if (HeadOfficeGrantedAtUtc is null) return;
        HeadOfficeGrantedAtUtc = null;
        Touch();
    }

    public DateTime? LastLoginAtUtc { get; private set; }

    /// <summary>
    /// Access tokens issued before this moment are no longer accepted
    /// (security review R2-09). Set when the password is reset or the account
    /// is deactivated, so a token already handed out stops working within
    /// seconds rather than at its expiry.
    /// </summary>
    public DateTime? SessionsValidAfterUtc { get; private set; }

    /// <summary>Ends every session: tokens issued before now stop being accepted.</summary>
    public void EndAllSessions()
    {
        // Whole seconds, because token times are whole seconds: a token issued
        // in this same second, after this call, must still be accepted.
        var now = DateTime.UtcNow;
        SessionsValidAfterUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        Touch();
    }

    /// <summary>True when this account may sign in at all.</summary>
    /// <remarks>
    /// Both conditions, in one place. Two separate checks scattered through the
    /// auth paths is how one of them ends up missing from the refresh flow, and
    /// an invited account quietly mints tokens.
    /// </remarks>
    public bool CanSignIn => IsActive && Status == AccountStatus.Active;

    private User() { }

    private User(
        string id,
        Email email,
        string displayName,
        PasswordHash passwordHash,
        UserRole role,
        PharmacyProfile? profile = null)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        Tier = SubscriptionTier.Free;
        Profile = profile ?? PharmacyProfile.Empty();
        Billing = BillingProfile.Empty();
        Role = role;
        IsActive = true;
    }

    public static User Register(Email email, string displayName, PasswordHash passwordHash)
    {
        ValidateDisplayName(displayName);

        // Self-registration always yields a plain pharmacy user. Elevated roles
        // are granted only through the admin API, which is itself role-gated —
        // there is deliberately no code path that registers staff.
        return new User(
            Guid.NewGuid().ToString("N"), email, displayName.Trim(), passwordHash,
            UserRole.PharmacyUser);
    }

    /// <summary>
    /// Creates a staff account. The caller must already have established that
    /// the acting user may grant <paramref name="role"/>; this enforces the
    /// shape of the data, not the authorisation.
    /// </summary>
    /// <remarks>
    /// Onboarded from the outset — see <see cref="PharmacyProfile.ForStaff"/>.
    /// Staff have no pharmacy to describe, and leaving the flag false locked a
    /// freshly seeded super-admin out of the admin console behind a form asking
    /// for a pharmacy address.
    /// </remarks>
    public static User CreateStaff(
        Email email, string displayName, PasswordHash passwordHash, UserRole role)
    {
        ValidateDisplayName(displayName);
        if (role == UserRole.PharmacyUser)
            throw new DomainException("Use Register for pharmacy users.");

        return new User(
            Guid.NewGuid().ToString("N"), email, displayName.Trim(), passwordHash, role,
            PharmacyProfile.ForStaff());
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("Display name is required.");
        if (displayName.Length > 80)
            throw new DomainException("Display name must be 80 chars or fewer.");
    }

    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("Display name is required.");
        DisplayName = displayName.Trim();
        Touch();
    }

    public void ChangePassword(PasswordHash newHash)
    {
        PasswordHash = newHash;
        Touch();
    }

    /// <summary>
    /// What to call this pharmacy in an email, a subject line or a report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because <c>Profile.PharmacyName ?? DisplayName</c> — written in
    /// three places — never fell through. <see cref="PharmacyProfile.PharmacyName"/>
    /// is non-nullable and the repository hydrates a missing one as
    /// <c>string.Empty</c>, and <c>??</c> tests for null, not for emptiness. So
    /// every account that had not finished onboarding sent campaigns with the
    /// subject <c>"Flu shots · "</c> and a blank sender name in the inbox.
    /// </para>
    /// <para>
    /// One property rather than a fixed idiom repeated at each call site: the
    /// idiom was already repeated, and was already wrong in all three copies.
    /// </para>
    /// </remarks>
    public string PharmacyDisplayName =>
        !string.IsNullOrWhiteSpace(Profile.PharmacyName) ? Profile.PharmacyName.Trim()
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName.Trim()
        : "Pharmacy";

    /// <summary>
    /// When the address on this account was confirmed. Null if it never was.
    /// </summary>
    /// <remarks>
    /// A timestamp rather than a boolean. "Verified" answers one question;
    /// "verified on 4 March" also answers when, which is what support needs
    /// when somebody insists they confirmed an address, and what a migration
    /// needs in order to distinguish accounts that predate verification from
    /// accounts that have ignored it.
    /// </remarks>
    public DateTime? EmailVerifiedAtUtc { get; private set; }

    /// <summary>True once the address has been confirmed.</summary>
    public bool IsEmailVerified => EmailVerifiedAtUtc is not null;

    /// <summary>Records that the address was confirmed. Idempotent.</summary>
    /// <remarks>
    /// Keeps the first timestamp on a second call rather than moving it. The
    /// date somebody confirmed their address is a fact about the past.
    /// </remarks>
    public void MarkEmailVerified()
    {
        if (EmailVerifiedAtUtc is not null) return;
        EmailVerifiedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Clears verification because the address changed.
    /// </summary>
    /// <remarks>
    /// Must be called by anything that changes <see cref="Email"/>. A verified
    /// flag that survives an address change means one confirmed mailbox
    /// verifies every address the account ever moves to.
    /// </remarks>
    public void ClearEmailVerification()
    {
        if (EmailVerifiedAtUtc is null) return;
        EmailVerifiedAtUtc = null;
        Touch();
    }

    /// <summary>
    /// Creates an account for a pharmacist who has not set a password yet.
    /// </summary>
    /// <remarks>
    /// The password hash is unusable rather than empty. An empty or null hash
    /// would depend on every verify path rejecting it correctly; an unguessable
    /// random one fails verification by construction, so even a bug in the
    /// invitation flow cannot produce a sign-in.
    /// </remarks>
    public static User Invite(Email email, string displayName, PasswordHash placeholderHash)
    {
        ValidateDisplayName(displayName);

        var user = new User(
            Guid.NewGuid().ToString("N"), email, displayName.Trim(), placeholderHash,
            UserRole.PharmacyUser);

        user.Status = AccountStatus.Invited;
        return user;
    }

    /// <summary>
    /// Places this pharmacy under a parent.
    /// </summary>
    /// <remarks>
    /// Refuses anything that would create a chain or a cycle. The depth limit
    /// is checked by the caller, which can see whether the proposed parent is
    /// itself a child; the two rules here are the ones expressible from a
    /// single account.
    /// </remarks>
    public void SetParent(string parentPharmacyId)
    {
        if (string.IsNullOrWhiteSpace(parentPharmacyId))
            throw new DomainException("A parent pharmacy is required.");

        if (parentPharmacyId == Id)
            throw new DomainException("A pharmacy cannot be its own parent.");

        if (Role != UserRole.PharmacyUser)
            throw new DomainException("Only pharmacy accounts can sit under a parent.");

        ParentPharmacyId = parentPharmacyId;
        Touch();
    }

    /// <summary>
    /// Detaches from the parent, leaving a normal top-level pharmacy.
    /// </summary>
    /// <remarks>
    /// Nothing is deleted. The child keeps its patients, posters and campaigns
    /// and carries on signing in — the parent simply loses access.
    /// </remarks>
    public void DetachFromParent()
    {
        ParentPharmacyId = null;
        Touch();
    }

    /// <summary>
    /// Completes activation: sets the real password and opens the account.
    /// </summary>
    public void Activate(PasswordHash passwordHash)
    {
        if (Status == AccountStatus.Active)
            throw new DomainException("This account has already been activated.");

        PasswordHash = passwordHash;
        Status = AccountStatus.Active;
        Touch();
    }

    public void ChangeTier(SubscriptionTier tier)
    {
        Tier = tier;
        Touch();
    }

    public void SetProfile(PharmacyProfile profile)
    {
        Profile = profile;
        Touch();
    }

    /* ---------------- Role and lifecycle ---------------- */

    public void ChangeRole(UserRole role)
    {
        if (Role == role) return;

        // Demoting an admin who still owns pharmacy relationships would leave
        // those accounts without an owner. That check lives in AdminService
        // rather than here: the entity can only see itself, and answering
        // "does anyone still point at me?" requires querying other users.

        // Staff are never themselves managed — an admin belonging to another
        // admin has no meaning in this model and would make scope checks
        // ambiguous.
        if (role.IsStaff()) ManagedByAdminId = null;

        Role = role;
        Touch();
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        EndAllSessions();
    }

    public void Reactivate()
    {
        if (IsActive) return;
        IsActive = true;
        Touch();
    }

    /// <summary>Links this pharmacy user to an admin.</summary>
    public void AssignToAdmin(string adminId)
    {
        if (string.IsNullOrWhiteSpace(adminId))
            throw new DomainException("Admin id is required.");
        if (Role != UserRole.PharmacyUser)
            throw new DomainException("Only pharmacy users can be assigned to an admin.");
        if (adminId == Id)
            throw new DomainException("A user cannot manage themselves.");

        ManagedByAdminId = adminId;
        Touch();
    }

    public void UnassignFromAdmin()
    {
        if (ManagedByAdminId is null) return;
        ManagedByAdminId = null;
        Touch();
    }

    public void RecordLogin()
    {
        LastLoginAtUtc = DateTime.UtcNow;
        // Deliberately no Touch(): a login is not a change to the account, and
        // letting it move UpdatedAtUtc would make that field useless for
        // spotting genuine administrative edits.
    }

    /* ---------------- Billing ---------------- */

    /// <summary>
    /// Records the Stripe customer we created for this user. Set once, on the
    /// first checkout — reusing the same customer keeps their payment methods,
    /// invoice history, and tax details together instead of fragmenting them
    /// across a new customer per purchase.
    /// </summary>
    public void LinkStripeCustomer(string stripeCustomerId)
    {
        if (string.IsNullOrWhiteSpace(stripeCustomerId))
            throw new DomainException("Stripe customer id is required.");

        Billing = Billing with { StripeCustomerId = stripeCustomerId.Trim() };
        Touch();
    }

    /// <summary>
    /// Applies the subscription state Stripe reported, and derives the tier
    /// from it. Tier is deliberately *not* a separate input: it is always a
    /// function of what Stripe says, so there is no code path where the two
    /// can drift apart.
    /// </summary>
    public void ApplySubscription(
        string? stripeSubscriptionId,
        SubscriptionStatus status,
        SubscriptionTier tierIfActive,
        DateTime? currentPeriodEndUtc,
        bool cancelAtPeriodEnd)
    {
        Billing = Billing with
        {
            StripeSubscriptionId = stripeSubscriptionId,
            Status = status,
            CurrentPeriodEndUtc = currentPeriodEndUtc,
            CancelAtPeriodEnd = cancelAtPeriodEnd,
        };

        Tier = Billing.GrantsPaidTier ? tierIfActive : SubscriptionTier.Free;
        Touch();
    }

    /// <summary>Subscription ended — drop to Free but keep any bought credits.</summary>
    public void ClearSubscription()
    {
        Billing = Billing with
        {
            StripeSubscriptionId = null,
            Status = SubscriptionStatus.Canceled,
            CurrentPeriodEndUtc = null,
            CancelAtPeriodEnd = false,
        };
        Tier = SubscriptionTier.Free;
        Touch();
    }

    public void AddVideoCredits(int amount)
    {
        if (amount <= 0) throw new DomainException("Credit amount must be positive.");
        Billing = Billing with { VideoCredits = Billing.VideoCredits + amount };
        Touch();
    }

    /// <summary>
    /// Spends one credit. Returns false when the balance is empty so callers
    /// can fall through to a quota error rather than going negative.
    /// </summary>
    public bool TryConsumeVideoCredit()
    {
        if (Billing.VideoCredits <= 0) return false;
        Billing = Billing with { VideoCredits = Billing.VideoCredits - 1 };
        Touch();
        return true;
    }
}
