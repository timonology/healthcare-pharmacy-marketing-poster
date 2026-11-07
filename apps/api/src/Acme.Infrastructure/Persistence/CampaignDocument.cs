using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class CampaignDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("posterId")] public string PosterId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("channel")] public int Channel { get; set; }
    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("recipients")] public List<string> Recipients { get; set; } = new();
    [BsonElement("videoIds")] public List<string> VideoIds { get; set; } = new();
    [BsonElement("deliveryReport")] public List<RecipientResultDocument> DeliveryReport { get; set; } = new();
    [BsonElement("sentCount")] public int SentCount { get; set; }
    [BsonElement("sentAtUtc")] public DateTime? SentAtUtc { get; set; }
    [BsonElement("note")] public string? Note { get; set; }

    // The worker's claim. Absent on every campaign written before dispatch moved
    // off the request thread, which is why both are nullable rather than
    // defaulted — a missing lease means "not claimed", and that is the correct
    // reading of an old row.
    [BsonElement("leaseOwner")] public string? LeaseOwner { get; set; }
    [BsonElement("leaseExpiresAtUtc")] public DateTime? LeaseExpiresAtUtc { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class RecipientResultDocument
{
    [BsonElement("recipient")] public string Recipient { get; set; } = default!;
    [BsonElement("status")]    public int Status { get; set; }
    [BsonElement("reason")]    public string? Reason { get; set; }
    [BsonElement("atUtc")]     public DateTime AtUtc { get; set; }
}
