using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class PatientDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("fullName")] public string FullName { get; set; } = default!;
    [BsonElement("email")] public string? Email { get; set; }
    [BsonElement("phone")] public string? Phone { get; set; }
    [BsonElement("notes")] public string Notes { get; set; } = string.Empty;
    [BsonElement("groupIds")] public List<string> GroupIds { get; set; } = new();
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class PatientGroupDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("patientCount")] public int PatientCount { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
