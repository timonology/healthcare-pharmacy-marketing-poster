using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class BrandKitDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("logoBlobKey")] public string? LogoBlobKey { get; set; }
    [BsonElement("colors")] public BrandColorsDocument Colors { get; set; } = new();
    [BsonElement("pharmacy")] public PharmacyDetailsDocument Pharmacy { get; set; } = new();
    [BsonElement("regulatoryFooter")] public string RegulatoryFooter { get; set; } = string.Empty;
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class BrandColorsDocument
{
    [BsonElement("primary")] public string Primary { get; set; } = "#000000";
    [BsonElement("secondary")] public string Secondary { get; set; } = "#000000";
    [BsonElement("accent")] public string Accent { get; set; } = "#000000";
}

internal sealed class PharmacyDetailsDocument
{
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("licenseNumber")] public string LicenseNumber { get; set; } = string.Empty;
    [BsonElement("phone")] public string Phone { get; set; } = string.Empty;
    [BsonElement("address")] public string Address { get; set; } = string.Empty;
}
