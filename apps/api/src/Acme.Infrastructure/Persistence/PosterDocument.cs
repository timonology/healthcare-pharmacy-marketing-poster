using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class PosterDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("sourceTemplateId")] public string? SourceTemplateId { get; set; }
    [BsonElement("thumbnailBlobKey")] public string? ThumbnailBlobKey { get; set; }
    [BsonElement("canvas")] public BsonDocument? Canvas { get; set; }
    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
