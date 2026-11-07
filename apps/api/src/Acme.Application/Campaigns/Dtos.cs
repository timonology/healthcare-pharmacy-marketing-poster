using Acme.Domain.Campaigns;

namespace Acme.Application.Campaigns;

public sealed record RecipientResultDto(
    string Recipient,
    RecipientStatus Status,
    string? Reason,
    DateTime AtUtc);

public sealed record CampaignDto(
    string Id,
    string OwnerId,
    string PosterId,
    IReadOnlyList<string> VideoIds,
    string Name,
    CampaignChannel Channel,
    CampaignStatus Status,
    int RecipientCount,
    int SentCount,
    DateTime? SentAtUtc,
    string? Note,
    IReadOnlyList<RecipientResultDto> DeliveryReport,
    DateTime CreatedAtUtc);

public sealed record CreateCampaignRequest(
    string PosterId,
    string Name,
    CampaignChannel Channel,
    IReadOnlyList<string>? Recipients = null,
    IReadOnlyList<string>? PatientIds = null,
    IReadOnlyList<string>? GroupIds = null,
    IReadOnlyList<string>? VideoIds = null);

public sealed record CampaignAudiencePreview(
    int TotalUnique,
    int FromManual,
    int FromPatients,
    int FromGroups,
    int InvalidCount,
    IReadOnlyList<string> SampleRecipients,
    // Addresses left out because they unsubscribed from this pharmacy (R2-06).
    int UnsubscribedCount = 0);

public sealed record CampaignAudienceRequest(
    CampaignChannel Channel,
    IReadOnlyList<string>? Recipients = null,
    IReadOnlyList<string>? PatientIds = null,
    IReadOnlyList<string>? GroupIds = null);
