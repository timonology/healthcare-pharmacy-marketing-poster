using Acme.Domain.Campaigns;

namespace Acme.Application.Campaigns;

public sealed record CampaignDto(
    string Id,
    string OwnerId,
    string PosterId,
    string Name,
    CampaignChannel Channel,
    CampaignStatus Status,
    int RecipientCount,
    int SentCount,
    DateTime? SentAtUtc,
    string? Note,
    DateTime CreatedAtUtc);

public sealed record CreateCampaignRequest(
    string PosterId,
    string Name,
    CampaignChannel Channel,
    IReadOnlyList<string>? Recipients = null,
    IReadOnlyList<string>? PatientIds = null,
    IReadOnlyList<string>? GroupIds = null);

public sealed record CampaignAudiencePreview(
    int TotalUnique,
    int FromManual,
    int FromPatients,
    int FromGroups,
    int InvalidCount,
    IReadOnlyList<string> SampleRecipients);

public sealed record CampaignAudienceRequest(
    CampaignChannel Channel,
    IReadOnlyList<string>? Recipients = null,
    IReadOnlyList<string>? PatientIds = null,
    IReadOnlyList<string>? GroupIds = null);
