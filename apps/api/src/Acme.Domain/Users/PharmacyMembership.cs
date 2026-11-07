using Acme.Domain.Common;

namespace Acme.Domain.Users;

/// <summary>What a member may do with a pharmacy.</summary>
public enum PharmacyMembershipRole
{
    /// <summary>May work as this pharmacy: posters, campaigns, videos, contacts.</summary>
    Member = 0,

    /// <summary>
    /// May also manage it — settings, sub-pharmacies, and who else belongs.
    /// </summary>
    /// <remarks>
    /// A pharmacy-side privilege, not a platform role. A head office admin has
    /// no more reach across Marketing25 than any other pharmacy user; they
    /// simply hold this over more than one pharmacy.
    /// </remarks>
    HeadOfficeAdmin = 1,
}

/// <summary>
/// A person's right to act as a pharmacy.
/// </summary>
/// <remarks>
/// <para>
/// The join that makes one account able to work across several pharmacies. It
/// is deliberately <b>not</b> a new identity model: a pharmacy is still a
/// <see cref="User"/>, patients and posters are still owned by that user's id,
/// and switching still issues the delegated token that manage-as already uses.
/// What changes is only <i>who is allowed</i> to be issued one.
/// </para>
/// <para>
/// <b>Everyone has a self-membership</b> — <c>UserId == PharmacyId</c> — created
/// when the account is registered. Without it "the pharmacies I can work as"
/// would be a special case joined to a list, and every caller would have to
/// remember to add the caller's own pharmacy. Making it a row means the list is
/// just a query.
/// </para>
/// <para>
/// Membership is checked server-side on every switch and on every refresh. A
/// pharmacy id from the client is a request, never a fact.
/// </para>
/// </remarks>
public sealed class PharmacyMembership : Entity
{
    /// <summary>The person.</summary>
    public string UserId { get; private set; } = default!;

    /// <summary>The pharmacy, which is itself a user id.</summary>
    public string PharmacyId { get; private set; } = default!;

    public PharmacyMembershipRole Role { get; private set; }

    /// <summary>
    /// Who granted it — a user id, or <see cref="System"/> for backfilled rows.
    /// </summary>
    /// <remarks>
    /// Kept because "why can this person see that pharmacy" is the first
    /// question asked when something looks wrong, and it is unanswerable
    /// afterwards if only the fact of the grant is stored.
    /// </remarks>
    public string GrantedBy { get; private set; } = default!;

    /// <summary>Marker for rows created by the backfill rather than by a person.</summary>
    public const string System = "system";

    /// <summary>True when this is the person's own pharmacy.</summary>
    public bool IsSelf => string.Equals(UserId, PharmacyId, StringComparison.Ordinal);

    private PharmacyMembership() { }

    public static PharmacyMembership Grant(
        string userId,
        string pharmacyId,
        PharmacyMembershipRole role,
        string grantedBy)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("A membership needs a person.");
        if (string.IsNullOrWhiteSpace(pharmacyId))
            throw new DomainException("A membership needs a pharmacy.");

        return new PharmacyMembership
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            PharmacyId = pharmacyId,
            Role = role,
            GrantedBy = string.IsNullOrWhiteSpace(grantedBy) ? System : grantedBy,
        };
    }

    /// <summary>The membership every account gets for its own pharmacy.</summary>
    public static PharmacyMembership Self(string userId) =>
        Grant(userId, userId, PharmacyMembershipRole.HeadOfficeAdmin, System);

    /// <summary>
    /// Raises or lowers the role.
    /// </summary>
    /// <remarks>
    /// A self-membership cannot be lowered. Demoting yourself out of your own
    /// pharmacy leaves an account that owns data it can no longer manage, and
    /// nobody to raise it back.
    /// </remarks>
    public void ChangeRole(PharmacyMembershipRole role)
    {
        if (IsSelf && role != PharmacyMembershipRole.HeadOfficeAdmin)
            throw new DomainException("You cannot reduce your own access to your own pharmacy.");

        Role = role;
        Touch();
    }

    /// <summary>True when this membership permits managing the pharmacy, not just working as it.</summary>
    public bool CanManage => Role == PharmacyMembershipRole.HeadOfficeAdmin;
}
