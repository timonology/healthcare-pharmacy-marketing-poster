using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Invitations;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Hierarchy;

public sealed record ChildPharmacyDto(
    string UserId,
    string Email,
    string DisplayName,
    string PharmacyName,
    string? FCode,
    string PostCode,
    AccountStatus Status,
    bool IsActive,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc);

/// <summary>
/// Where the signed-in pharmacy sits in a group.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately an object with a flag rather than a nullable parent. The
/// earlier shape returned the parent or <c>null</c>, and a null body is not a
/// value a client can rely on: ASP.NET Core turns <c>Ok(null)</c> into a 204
/// with no body at all, which every layer in between then has to guess about.
/// One layer guessed wrong and reported every top-level pharmacy as a branch.
/// </para>
/// <para>
/// <see cref="IsChildPharmacy"/> is also the authoritative answer where
/// <see cref="Parent"/> cannot be: a branch whose parent account has been
/// deleted is still a branch, and must not be offered branches of its own.
/// </para>
/// </remarks>
/// <param name="IsHeadOffice">
/// Whether this pharmacy runs branches. Drives what the app shows: the head
/// office badge, the branch list, and whether "Add branch" is offered at
/// all rather than offered and then refused by the server.
/// </param>
public sealed record PharmacyContextDto(
    bool IsChildPharmacy,
    ChildPharmacyDto? Parent,
    bool IsHeadOffice = false);

public sealed record CreateChildPharmacyRequest(
    string FullName,
    string Email,
    string? PharmacyName = null,
    string? FCode = null,
    string? Address = null,
    string? PostCode = null,
    string? Phone = null);

/// <param name="ExpiresAtUtc">
/// Short. When it lapses the parent is back in their own account rather than
/// silently continuing to act on someone else's.
/// </param>
public sealed record ManageAsTokenDto(
    string Token,
    DateTime ExpiresAtUtc,
    string ChildUserId,
    string ChildPharmacyName);

/// <summary>
/// Parent pharmacies and the sub-pharmacies under them.
/// </summary>
/// <remarks>
/// <para>
/// One level, by design. A parent has children; a child cannot have its own.
/// Arbitrary depth would need cycle detection and recursive permission checks
/// on every read, for a structure nobody has asked for.
/// </para>
/// <para>
/// A child is a real, independent account. It signs in, owns its own patients
/// and posters, and works whether or not the parent ever looks at it. The
/// parent gets access in addition — never instead.
/// </para>
/// </remarks>
public sealed class PharmacyHierarchyService(
    IUserRepository users,
    IJwtTokenService tokens,
    InvitationService invitations,
    IPharmacyMembershipRepository memberships,
    ILogger<PharmacyHierarchyService> log)
{
    /// <summary>
    /// How long a "manage as" session lasts.
    /// </summary>
    /// <remarks>
    /// Thirty minutes. Long enough to do a piece of support work, short enough
    /// that a parent who wanders off is not left holding live access to another
    /// account. There is no renewal — re-entering is one click.
    /// </remarks>
    private static readonly TimeSpan ManageAsWindow = TimeSpan.FromMinutes(30);

    /// <summary>A cap, so one account cannot quietly become a directory.</summary>
    private const int MaxChildren = 50;

    public async Task<Result<IReadOnlyList<ChildPharmacyDto>>> ListChildrenAsync(
        string parentId, CancellationToken ct)
    {
        var children = await users.ListChildPharmaciesAsync(parentId, ct);
        return Result<IReadOnlyList<ChildPharmacyDto>>.Success(children.Select(ToDto).ToList());
    }

    /// <summary>
    /// Creates a sub-pharmacy and invites it.
    /// </summary>
    /// <remarks>
    /// The child sets its own password from an activation email rather than
    /// having one chosen for it. A parent who picked the password would know
    /// the child's credentials permanently, which is a different and much
    /// broader kind of access than "manage as" — and one nothing would record.
    /// </remarks>
    public async Task<Result<ChildPharmacyDto>> CreateChildAsync(
        string parentId, CreateChildPharmacyRequest request, CancellationToken ct)
    {
        var parent = await users.FindByIdAsync(parentId, ct);
        if (parent is null) return Result<ChildPharmacyDto>.NotFound("Account not found.");

        if (parent.Role != UserRole.PharmacyUser)
            return Result<ChildPharmacyDto>.Forbidden(
                "Branches belong to a pharmacy account.");

        // One level. A child creating children is how this becomes a tree, and
        // every permission check becomes recursive.
        if (parent.IsChildPharmacy)
            return Result<ChildPharmacyDto>.Forbidden(
                "This pharmacy already sits under another one, so it cannot have branches of its own.");

        // Head office is granted, not assumed.
        //
        // Creating a branch is not a small act: it creates a real login for
        // another pharmacy and emails a credential to whatever address was
        // typed. Before this gate any pharmacy account could do it, which made
        // the head-office role something you took rather than something you
        // were given. Existing groups keep the ability — the backfill grants it
        // to every pharmacy that already had branches.
        //
        // The message names the way forward. "Forbidden" tells somebody they
        // cannot, and leaves them with nowhere to go.
        if (!parent.IsHeadOffice)
            return Result<ChildPharmacyDto>.Forbidden(
                "Running branches is a head office feature. Contact Marketing25 support "
                + "to have head office enabled for this pharmacy.");

        var existing = await users.ListChildPharmaciesAsync(parentId, ct);
        if (existing.Count >= MaxChildren)
            return Result<ChildPharmacyDto>.Invalid(
                $"You have reached the limit of {MaxChildren} branches.");

        var invite = await invitations.InviteAccountAsync(
            new InvitePharmacistRequest(
                FullName: request.FullName,
                Email: request.Email,
                FCode: request.FCode,
                Address: request.Address,
                PostCode: request.PostCode,
                Phone: request.Phone,
                PharmacyName: request.PharmacyName ?? request.FullName),
            invitedById: parent.Id,
            invitedByEmail: parent.Email.Value,
            ct);

        if (!invite.Success || invite.UserId is null)
            return Result<ChildPharmacyDto>.Invalid(
                invite.Error ?? "Could not create that branch.");

        var child = await users.FindByIdAsync(invite.UserId, ct);
        if (child is null)
            return Result<ChildPharmacyDto>.Invalid("The branch account could not be read back.");

        try { child.SetParent(parentId); }
        catch (Domain.Common.DomainException ex)
        {
            return Result<ChildPharmacyDto>.Invalid(ex.Message);
        }

        await users.UpdateAsync(child, ct);

        // Grant the membership that makes the branch switchable, now rather
        // than at the next restart.
        //
        // MembershipBackfill would derive this from ParentPharmacyId — but only
        // on boot. Without granting it here, a head office that has just created
        // a branch cannot open it until somebody restarts the API, which is a
        // baffling way for a brand-new feature to appear broken.
        try
        {
            await memberships.AddAsync(
                PharmacyMembership.Grant(
                    userId: parentId,
                    pharmacyId: child.Id,
                    role: PharmacyMembershipRole.HeadOfficeAdmin,
                    grantedBy: parentId),
                ct);
        }
        catch (Exception ex)
        {
            // The sub-pharmacy exists and is attached; only the shortcut to it
            // is missing, and the backfill will add it. Worth a warning, not
            // worth failing a creation that otherwise succeeded.
            log.LogWarning(ex,
                "Created sub-pharmacy {Child} but could not grant {Parent} a membership",
                child.Id, parentId);
        }

        log.LogInformation(
            "Pharmacy {Parent} created sub-pharmacy {Child} ({Email})",
            PersonalData.Mask(parent.Email.Value), child.Id, PersonalData.Mask(child.Email.Value));

        return Result<ChildPharmacyDto>.Success(ToDto(child));
    }

    /// <summary>
    /// Removes a child from the hierarchy. The account itself is untouched.
    /// </summary>
    public async Task<Result<bool>> DetachChildAsync(
        string parentId, string childId, CancellationToken ct)
    {
        var child = await users.FindByIdAsync(childId, ct);
        if (child is null || child.ParentPharmacyId != parentId)
            return Result<bool>.NotFound("Branch not found.");

        child.DetachFromParent();
        await users.UpdateAsync(child, ct);

        log.LogInformation("Pharmacy {Parent} detached sub-pharmacy {Child}", parentId, childId);
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Issues a short-lived token that acts as a child pharmacy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guards here are the whole security model, so they are explicit
    /// rather than implied:
    /// </para>
    /// <list type="bullet">
    ///   <item>the child must actually be this caller's child;</item>
    ///   <item>a delegated token cannot request another — no chaining, so a
    ///         compromised parent cannot hop along a chain of accounts;</item>
    ///   <item>the child must be able to sign in itself, so managing a
    ///         deactivated or never-activated account is refused rather than
    ///         becoming a way to use an account its owner never opened.</item>
    /// </list>
    /// </remarks>
    public async Task<Result<ManageAsTokenDto>> IssueManageAsTokenAsync(
        string actorId, bool actorIsAlreadyDelegated, string childId, CancellationToken ct)
    {
        if (actorIsAlreadyDelegated)
            return Result<ManageAsTokenDto>.Forbidden(
                "You are already managing another pharmacy. Return to your own account first.");

        var actor = await users.FindByIdAsync(actorId, ct);
        if (actor is null) return Result<ManageAsTokenDto>.NotFound("Account not found.");

        var child = await users.FindByIdAsync(childId, ct);

        // Not-yours and not-found give the same answer, so a parent cannot
        // probe for other pharmacies' account ids.
        if (child is null || child.ParentPharmacyId != actorId)
            return Result<ManageAsTokenDto>.NotFound("Branch not found.");

        if (!child.CanSignIn)
            return Result<ManageAsTokenDto>.Invalid(
                child.Status == AccountStatus.Invited
                    ? "That branch has not activated its account yet."
                    : "That branch is deactivated.");

        var (token, expiresAt) = tokens.IssueDelegated(child, actor, ManageAsWindow);

        // Logged at information level on purpose. One pharmacy operating
        // another's account is exactly the event someone will want to find
        // later, and it should not require debug logging to be on.
        log.LogInformation(
            "Pharmacy {Actor} started managing sub-pharmacy {Child} until {Expires}",
            PersonalData.Mask(actor.Email.Value), PersonalData.Mask(child.Email.Value), expiresAt);

        return Result<ManageAsTokenDto>.Success(new ManageAsTokenDto(
            token, expiresAt, child.Id, child.Profile.PharmacyName));
    }

    /// <summary>
    /// The parent above a pharmacy, if any — for the "managed by" banner.
    /// </summary>
    /// <summary>
    /// Whether the caller is a branch, and of whom.
    /// </summary>
    /// <remarks>
    /// Always succeeds with a body for an account that exists, so the caller
    /// never has to interpret an empty response. The two facts are reported
    /// independently on purpose: an orphaned branch — parent id set, parent
    /// account gone — is a branch with no parent to name, and collapsing that
    /// into "no parent" would hand it the ability to create branches itself.
    /// </remarks>
    public async Task<Result<PharmacyContextDto>> GetContextAsync(
        string pharmacyId, CancellationToken ct)
    {
        var self = await users.FindByIdAsync(pharmacyId, ct);
        if (self is null) return Result<PharmacyContextDto>.NotFound("Account not found.");

        if (!self.IsChildPharmacy)
            return Result<PharmacyContextDto>.Success(
                new PharmacyContextDto(false, null, self.IsHeadOffice));

        var parent = await users.FindByIdAsync(self.ParentPharmacyId!, ct);
        return Result<PharmacyContextDto>.Success(
            new PharmacyContextDto(true, parent is null ? null : ToDto(parent), self.IsHeadOffice));
    }

    private static ChildPharmacyDto ToDto(User u) => new(
        u.Id,
        u.Email.Value,
        u.DisplayName,
        u.Profile.PharmacyName,
        u.Profile.SonarFCode,
        u.Profile.PostCode,
        u.Status,
        u.IsActive,
        u.LastLoginAtUtc,
        u.CreatedAtUtc);
}
