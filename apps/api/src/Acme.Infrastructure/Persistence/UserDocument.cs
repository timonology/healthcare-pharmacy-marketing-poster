using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

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

    [BsonElement("tier")]
    [BsonDefaultValue(0)]
    public int Tier { get; set; }

    [BsonElement("profile")]
    public PharmacyProfileDocument Profile { get; set; } = new();

    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class PharmacyProfileDocument
{
    [BsonElement("pharmacyName")] public string PharmacyName { get; set; } = string.Empty;
    [BsonElement("address")] public string Address { get; set; } = string.Empty;
    [BsonElement("postCode")] public string PostCode { get; set; } = string.Empty;
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("contactName")] public string ContactName { get; set; } = string.Empty;
    [BsonElement("contactPhone")] public string ContactPhone { get; set; } = string.Empty;
    [BsonElement("sonarFCode")] public string? SonarFCode { get; set; }
    [BsonElement("onboardingCompleted")] public bool OnboardingCompleted { get; set; }
}
