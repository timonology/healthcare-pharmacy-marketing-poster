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
    [BsonElement("sentCount")] public int SentCount { get; set; }
    [BsonElement("sentAtUtc")] public DateTime? SentAtUtc { get; set; }
    [BsonElement("note")] public string? Note { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
