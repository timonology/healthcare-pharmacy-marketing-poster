using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class TemplateDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("category")] public int Category { get; set; }
    [BsonElement("tags")] public List<string> Tags { get; set; } = new();
    [BsonElement("thumbnailBlobKey")] public string? ThumbnailBlobKey { get; set; }
    [BsonElement("canvas")] public BsonDocument? Canvas { get; set; }
    [BsonElement("isPublished")] public bool IsPublished { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
