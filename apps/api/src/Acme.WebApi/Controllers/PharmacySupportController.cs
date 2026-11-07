using Acme.Application.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// The admin support view of a single pharmacy: their patients, groups,
/// posters, campaigns and videos.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="AdminController"/> because the subject is
/// different — that controller administers accounts, this one reads the data
/// inside one. Keeping them apart means the reader of either can see the whole
/// of what it does, and means the routes that touch patient data are in one
/// file rather than scattered through four hundred lines of account
/// management.
/// </para>
/// <para>
/// Everything is under <c>/api/admin</c> so it inherits the admin-area policy
/// and is carried by the existing web proxy without a new route file. The
/// policy is the coarse gate; <see cref="PharmacySupportService"/> re-derives
/// per-pharmacy authority on every call.
/// </para>
/// <para>
/// One route here is not a read: <c>campaigns/{id}/resend</c>. It is a POST,
/// it only works on a campaign that already failed, and it writes to the audit
/// trail. Everything else is a projection.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/pharmacies/{pharmacyId}")]
[Authorize(Policy = AuthPolicies.AdminArea)]
public sealed class PharmacySupportController(PharmacySupportService support) : AdminApiController
{
    /* ---------------- Overview ---------------- */

    /// <summary>Headline counts and account facts for one pharmacy.</summary>
    [ActivityLog("admin.pharmacy.view", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("overview")]
    [ProducesResponseType(typeof(PharmacySupportOverviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(string pharmacyId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.GetOverviewAsync(actor, pharmacyId, ct));
    }

    /* ---------------- Patients ---------------- */

    /// <summary>
    /// The pharmacy's patients, with email and phone masked.
    /// </summary>
    /// <remarks>
    /// The masked values are produced server-side, so this response does not
    /// contain the real addresses in any form. Use the reveal route below to
    /// obtain one, which records that you did.
    /// </remarks>
    [ActivityLog("admin.pharmacy.patients_view", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("patients")]
    [ProducesResponseType(typeof(SupportPatientListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Patients(
        string pharmacyId,
        [FromQuery] string? search,
        [FromQuery] string? groupId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken ct = default)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(
            await support.ListPatientsAsync(actor, pharmacyId, search, groupId, skip, take, ct));
    }

    /// <summary>
    /// Unmasks one patient's contact details and records the access.
    /// </summary>
    /// <remarks>
    /// A POST despite reading rather than writing patient data, because it
    /// does write — an audit entry — and because it is not safe to repeat
    /// idly. A GET would be pre-fetched by a browser, retried by a proxy and
    /// cached, any of which would put entries in the log that no administrator
    /// asked for and make the trail untrustworthy.
    ///
    /// The reason in the body is required and stored verbatim.
    /// </remarks>
    [Audit("admin.patient.view_contact", "patient", IdRoute = "patientId", PharmacyRoute = "pharmacyId")]
    [HttpPost("patients/{patientId}/reveal")]
    [ProducesResponseType(typeof(RevealedPatientContactDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevealPatient(
        string pharmacyId,
        string patientId,
        [FromBody] RevealPatientRequest body,
        CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(
            await support.RevealPatientContactAsync(actor, pharmacyId, patientId, body?.Reason, ct));
    }

    /// <summary>The pharmacy's patient groups, with membership recounted.</summary>
    [ActivityLog("admin.pharmacy.groups_view", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("groups")]
    [ProducesResponseType(typeof(IReadOnlyList<SupportGroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Groups(string pharmacyId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.ListGroupsAsync(actor, pharmacyId, ct));
    }

    /* ---------------- Posters and videos ---------------- */

    [ActivityLog("admin.pharmacy.posters_view", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("posters")]
    [ProducesResponseType(typeof(IReadOnlyList<SupportPosterDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Posters(string pharmacyId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.ListPostersAsync(actor, pharmacyId, ct));
    }

    [ActivityLog("admin.pharmacy.videos_view", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("videos")]
    [ProducesResponseType(typeof(IReadOnlyList<SupportVideoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Videos(string pharmacyId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.ListVideosAsync(actor, pharmacyId, ct));
    }

    /* ---------------- Campaigns ---------------- */

    [ActivityLog("admin.pharmacy.campaigns_view", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("campaigns")]
    [ProducesResponseType(typeof(IReadOnlyList<SupportCampaignDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Campaigns(string pharmacyId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.ListCampaignsAsync(actor, pharmacyId, ct));
    }

    /// <summary>One campaign, including its per-recipient delivery report.</summary>
    [ActivityLog("admin.campaign.view", "campaign", IdRoute = "campaignId", PharmacyRoute = "pharmacyId")]
    [HttpGet("campaigns/{campaignId}")]
    [ProducesResponseType(typeof(SupportCampaignDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Campaign(
        string pharmacyId, string campaignId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.GetCampaignAsync(actor, pharmacyId, campaignId, ct));
    }

    /// <summary>
    /// Re-sends a failed campaign on the pharmacy's behalf.
    /// </summary>
    /// <remarks>
    /// Refused for any campaign not in the Failed state, so this cannot be
    /// used to send a pharmacy's patients anything they did not already
    /// compose and attempt to send themselves. The plan's monthly recipient
    /// cap is re-checked before anything leaves.
    /// </remarks>
    [Audit("admin.campaign.resend", "campaign", IdRoute = "campaignId", PharmacyRoute = "pharmacyId")]
    [HttpPost("campaigns/{campaignId}/resend")]
    [ProducesResponseType(typeof(SupportCampaignDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendCampaign(
        string pharmacyId, string campaignId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await support.ResendCampaignAsync(actor, pharmacyId, campaignId, ct));
    }
}
