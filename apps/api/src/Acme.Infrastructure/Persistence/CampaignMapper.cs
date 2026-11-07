using System.Reflection;
using Acme.Domain.Campaigns;

namespace Acme.Infrastructure.Persistence;

internal static class CampaignMapper
{
    private static readonly ConstructorInfo Ctor = typeof(Campaign)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Campaign parameterless ctor missing.");

    public static Campaign Hydrate(CampaignDocument d)
    {
        var c = (Campaign)Ctor.Invoke(null);
        Set(c, nameof(Campaign.Id), d.Id);
        Set(c, nameof(Campaign.OwnerId), d.OwnerId);
        Set(c, nameof(Campaign.PosterId), d.PosterId);
        Set(c, nameof(Campaign.VideoIds), (IReadOnlyList<string>)(d.VideoIds ?? new()).ToList());
        Set(c, nameof(Campaign.Name), d.Name);
        Set(c, nameof(Campaign.Channel), (CampaignChannel)d.Channel);
        Set(c, nameof(Campaign.Status), (CampaignStatus)d.Status);
        Set(c, nameof(Campaign.Recipients), (IReadOnlyList<string>)d.Recipients.ToList());
        Set(c, nameof(Campaign.DeliveryReport), (IReadOnlyList<RecipientResult>)(d.DeliveryReport ?? new())
            .Select(r => new RecipientResult
            {
                Recipient = r.Recipient,
                Status = (RecipientStatus)r.Status,
                Reason = r.Reason,
                AtUtc = r.AtUtc,
            }).ToList());
        Set(c, nameof(Campaign.SentCount), d.SentCount);
        Set(c, nameof(Campaign.SentAtUtc), d.SentAtUtc);
        Set(c, nameof(Campaign.Note), d.Note);
        Set(c, nameof(Campaign.LeaseOwner), d.LeaseOwner);
        Set(c, nameof(Campaign.LeaseExpiresAtUtc), d.LeaseExpiresAtUtc);
        Set(c, nameof(Campaign.CreatedAtUtc), d.CreatedAtUtc);
        Set(c, nameof(Campaign.UpdatedAtUtc), d.UpdatedAtUtc);
        return c;
    }

    public static CampaignDocument ToDocument(Campaign c) => new()
    {
        Id = c.Id,
        OwnerId = c.OwnerId,
        PosterId = c.PosterId,
        VideoIds = c.VideoIds.ToList(),
        Name = c.Name,
        Channel = (int)c.Channel,
        Status = (int)c.Status,
        Recipients = c.Recipients.ToList(),
        DeliveryReport = c.DeliveryReport
            .Select(r => new RecipientResultDocument
            {
                Recipient = r.Recipient,
                Status = (int)r.Status,
                Reason = r.Reason,
                AtUtc = r.AtUtc,
            }).ToList(),
        SentCount = c.SentCount,
        SentAtUtc = c.SentAtUtc,
        Note = c.Note,
        LeaseOwner = c.LeaseOwner,
        LeaseExpiresAtUtc = c.LeaseExpiresAtUtc,
        CreatedAtUtc = c.CreatedAtUtc,
        UpdatedAtUtc = c.UpdatedAtUtc,
    };

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
