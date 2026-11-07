using Acme.Domain.Common;

namespace Acme.Domain.Admin;

/// <summary>
/// What an administrator did, to whom, and when.
///
/// Append-only by construction — there are no mutators. An audit trail that
/// can be edited is not an audit trail, and for a system holding pharmacy
/// patient lists the question "who deactivated this account?" needs an answer
/// that nobody could have quietly rewritten.
/// </summary>
public sealed class AdminAuditEntry : Entity
{
    public string ActorId { get; private set; } = default!;
    public string ActorEmail { get; private set; } = default!;
    public AdminAction Action { get; private set; }

    /// <summary>The user acted upon. Null for actions with no single subject.</summary>
    public string? TargetUserId { get; private set; }
    public string? TargetEmail { get; private set; }

    /// <summary>Human-readable detail, e.g. "Starter → Pro".</summary>
    public string? Detail { get; private set; }

    private AdminAuditEntry() { }

    public static AdminAuditEntry Record(
        string actorId,
        string actorEmail,
        AdminAction action,
        string? targetUserId = null,
        string? targetEmail = null,
        string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new DomainException("Audit entries require an actor.");

        return new AdminAuditEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ActorId = actorId,
            ActorEmail = actorEmail,
            Action = action,
            TargetUserId = targetUserId,
            TargetEmail = targetEmail,
            Detail = Truncate(detail, 400),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}

public enum AdminAction
{
    // Values are stored as integers and queried as integers, so each one is
    // permanent and must be unique.
    //
    // Two were not. InvitePharmacist and ManageGlobalGroup were declared as 13
    // and 14, which C# accepts as aliases of StartActingAs and StopActingAs —
    // it compiles, and the audit log quietly stopped being able to tell an
    // invitation apart from somebody starting to act as another pharmacy.
    // Filtering by either returned both.
    //
    // They now have their own values. Rows already written as 13 or 14 stay
    // ambiguous: nothing recorded which of the two they were, so they read as
    // StartActingAs and StopActingAs. That is a gap in the history and cannot
    // be recovered — only stopped from growing.
    CreateAdmin = 0,
    UpdateAdmin = 1,
    DeactivateUser = 2,
    ReactivateUser = 3,
    AssignUserToAdmin = 4,
    UnassignUserFromAdmin = 5,
    OverrideSubscriptionTier = 6,
    ChangeRole = 7,
    GrantVideoCredits = 8,

    /// <summary>
    /// An AI poster template was generated. Has no target user — the subject is
    /// the gallery, which every pharmacy sees. Worth recording because it
    /// spends model budget and because "who added this template?" is exactly
    /// the question asked when a design turns out to be wrong.
    /// </summary>
    GenerateTemplate = 9,

    /// <summary>
    /// An administrator unmasked a patient's email or phone number.
    ///
    /// This is the only entry in the log that records a *read* rather than a
    /// change, and it is here deliberately. Support work needs occasional
    /// access to a patient's contact details — "she says she never got the
    /// email" cannot be answered otherwise — but a pharmacy's patient list is
    /// personal data the pharmacy is accountable for, and we hold it on their
    /// behalf. Recording each reveal, with the reason the administrator gave,
    /// is what makes that access answerable rather than merely possible.
    /// </summary>
    ViewPatientContact = 10,

    /// <summary>
    /// An administrator re-sent a failed campaign for a pharmacy.
    ///
    /// The one action in the support view that reaches the outside world:
    /// messages go to the pharmacy's patients, from the pharmacy's name, at
    /// the administrator's instigation. If a patient later asks why they were
    /// contacted, this is the entry that answers it.
    /// </summary>
    ResendCampaign = 11,

    /// <summary>
    /// A SuperAdmin changed a platform-wide setting.
    /// </summary>
    /// <remarks>
    /// No target user, because the subject is every pharmacy at once. Switching
    /// the video model changes what every future video costs and how it reads,
    /// so "who did this and when" needs an answer that outlives the person who
    /// did it.
    /// </remarks>
    ChangePlatformSetting = 12,

    /// <summary>
    /// Somebody began working as a pharmacy that is not their own.
    /// </summary>
    /// <remarks>
    /// Covers both routes into another pharmacy: a head office opening one of
    /// its branches, and platform staff supporting a customer. Both mint a
    /// token whose subject is somebody else's account, and from that moment
    /// every poster, campaign and contact they touch is recorded as the
    /// pharmacy's own work. "Who was actually at the keyboard" is unanswerable
    /// afterwards without this entry.
    /// </remarks>
    StartActingAs = 13,

    /// <summary>
    /// The end of a period of working as another pharmacy.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="StartActingAs"/> so the log shows a window
    /// rather than a moment. A start with no end is a session that lapsed on
    /// its own, which is also worth being able to see.
    /// </remarks>
    StopActingAs = 14,

    /// <summary>Somebody asked to be given access to a pharmacy.</summary>
    RequestPharmacyAccess = 15,

    /// <summary>An administrator approved or rejected such a request.</summary>
    /// <remarks>
    /// The decision that creates a membership, and therefore the decision that
    /// lets one account read another pharmacy's patient list. It belongs in
    /// the same log as tier overrides and contact reveals.
    /// </remarks>
    ReviewPharmacyAccess = 16,

    /// <summary>
    /// A pharmacist was invited, or an invitation resent.
    /// </summary>
    /// <remarks>
    /// Worth recording because an invitation creates an account and emails a
    /// credential to an address someone typed. "Who added this pharmacy, and
    /// where did the link go?" needs an answer that outlives the import.
    /// </remarks>
    InvitePharmacist = 17,

    /// <summary>
    /// A general (global) patient group was created, edited or deleted.
    /// </summary>
    /// <remarks>
    /// Recorded because the subject is every pharmacy at once. Deleting one
    /// silently removes it from patients across the whole platform, and
    /// "where did that group go?" needs an answer.
    /// </remarks>
    ManageGlobalGroup = 18,

    /// <summary>
    /// A pharmacy was granted, or had withdrawn, the ability to run branches.
    /// </summary>
    /// <remarks>
    /// Head office is the status that lets one account create other pharmacy
    /// accounts and read across them, so who granted it — and when — is exactly
    /// the sort of thing that has to be answerable later.
    /// </remarks>
    SetHeadOffice = 19,

    /// <summary>
    /// A head office opened one of its branches' records.
    /// </summary>
    /// <remarks>
    /// The second entry in this log that records a <i>read</i> rather than a
    /// change, and it is here for the same reason as the first.
    ///
    /// A head office can see across its branches without switching into them.
    /// That is convenient and it is also the moment the trail would otherwise
    /// go cold: switching is audited, so "who looked at this branch's patients"
    /// had an answer only while looking required switching. Reading without
    /// switching must not be the quiet way round that.
    ///
    /// The pharmacy is the data controller for its patients and we hold them on
    /// its behalf. Every route to that data has to be answerable, including the
    /// ones inside the same group.
    /// </remarks>
    ViewBranchDetail = 20,
}
