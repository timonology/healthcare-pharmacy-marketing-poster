using System.Reflection;
using Acme.Application.Platform;
using Acme.Domain.Platform;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class PlatformSettingDocument
{
    /// <summary>The setting key is the document id — one row per key, no duplicates possible.</summary>
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("value")] public string Value { get; set; } = default!;
    [BsonElement("updatedByEmail")] public string UpdatedByEmail { get; set; } = default!;
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Platform settings, one document per key.
/// </summary>
/// <remarks>
/// The key is the <c>_id</c>, which makes duplicates impossible without a
/// separate unique index and makes the upsert a single round trip. The
/// collection is tiny and read on a hot path (every video creation), so it is
/// worth keeping the read to one indexed lookup by primary key.
/// </remarks>
public sealed class MongoPlatformSettingRepository(MongoContext ctx) : IPlatformSettingRepository
{
    private IMongoCollection<PlatformSettingDocument> Col =>
        ctx.Collection<PlatformSettingDocument>("platform_settings");

    private static readonly ConstructorInfo Ctor =
        typeof(PlatformSetting).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("PlatformSetting parameterless ctor missing.");

    public async Task<PlatformSetting?> FindAsync(string key, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == key).FirstOrDefaultAsync(ct);
        if (doc is null) return null;

        var setting = (PlatformSetting)Ctor.Invoke(null);
        Set(setting, nameof(PlatformSetting.Id), doc.Id);
        Set(setting, nameof(PlatformSetting.Key), doc.Id);
        Set(setting, nameof(PlatformSetting.Value), doc.Value);
        Set(setting, nameof(PlatformSetting.UpdatedByEmail), doc.UpdatedByEmail);
        Set(setting, nameof(PlatformSetting.CreatedAtUtc), doc.CreatedAtUtc);
        Set(setting, nameof(PlatformSetting.UpdatedAtUtc), doc.UpdatedAtUtc);
        return setting;
    }

    public Task UpsertAsync(PlatformSetting setting, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == setting.Key,
            new PlatformSettingDocument
            {
                Id = setting.Key,
                Value = setting.Value,
                UpdatedByEmail = setting.UpdatedByEmail,
                CreatedAtUtc = setting.CreatedAtUtc,
                UpdatedAtUtc = setting.UpdatedAtUtc,
            },
            new ReplaceOptions { IsUpsert = true },
            ct);

    public Task DeleteAsync(string key, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == key, ct);

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
