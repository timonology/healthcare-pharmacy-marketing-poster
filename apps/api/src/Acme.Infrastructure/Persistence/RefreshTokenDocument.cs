using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class RefreshTokenDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("userId")] public string UserId { get; set; } = default!;
    [BsonElement("tokenHash")] public string TokenHash { get; set; } = default!;
    [BsonElement("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("revokedAtUtc")] public DateTime? RevokedAtUtc { get; set; }
    [BsonElement("replacedByTokenHash")] public string? ReplacedByTokenHash { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
