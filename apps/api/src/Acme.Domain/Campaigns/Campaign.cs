using Acme.Domain.Common;

namespace Acme.Domain.Campaigns;

public enum CampaignChannel { Email = 0, Sms = 1 }
public enum CampaignStatus { Draft = 0, Sending = 1, Sent = 2, Stopped = 3, Failed = 4 }

public sealed class Campaign : Entity
{
    public string OwnerId { get; private set; } = default!;
    public string PosterId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public CampaignChannel Channel { get; private set; }
    public CampaignStatus Status { get; private set; } = CampaignStatus.Draft;
    public IReadOnlyList<string> Recipients { get; private set; } = Array.Empty<string>();
    public int SentCount { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public string? Note { get; private set; }

    private Campaign() { }

    public static Campaign Create(
        string ownerId,
        string posterId,
        string name,
        CampaignChannel channel,
        IEnumerable<string> recipients)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(posterId))
            throw new DomainException("Poster id is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Campaign name is required.");

        var list = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
            throw new DomainException("At least one recipient is required.");

        return new Campaign
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            PosterId = posterId,
            Name = name.Trim(),
            Channel = channel,
            Recipients = list,
            Status = CampaignStatus.Draft,
        };
    }

    public void MarkSending() { Status = CampaignStatus.Sending; Touch(); }

    public void MarkSent(int sentCount, string? note = null)
    {
        SentCount = sentCount;
        SentAtUtc = DateTime.UtcNow;
        Note = note;
        Status = CampaignStatus.Sent;
        Touch();
    }

    public void MarkFailed(string reason)
    {
        Status = CampaignStatus.Failed;
        Note = reason;
        Touch();
    }

    public void Stop()
    {
        if (Status is CampaignStatus.Sent or CampaignStatus.Stopped)
            throw new DomainException("Campaign cannot be stopped from its current state.");
        Status = CampaignStatus.Stopped;
        Touch();
    }
}
