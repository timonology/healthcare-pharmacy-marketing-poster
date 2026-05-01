using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

/// <summary>Persistence model. Kept separate from the domain entity.</summary>
internal sealed class UserDocument
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("email")]
    public string Email { get; set; } = default!;

    [BsonElement("displayName")]
    public string DisplayName { get; set; } = default!;

    [BsonElement("passwordHash")]
    public string PasswordHash { get; set; } = default!;

    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}
