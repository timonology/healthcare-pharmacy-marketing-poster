using Acme.Application.Ai;
using Acme.Domain.Ai;
using Acme.Domain.Subscriptions;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class AiGenerationDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("kind")] [BsonDefaultValue(0)] public int Kind { get; set; }
    [BsonElement("prompt")] public string Prompt { get; set; } = string.Empty;
    [BsonElement("resultName")] public string ResultName { get; set; } = string.Empty;
    [BsonElement("succeeded")] [BsonDefaultValue(false)] public bool Succeeded { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MongoAiGenerationRepository : IAiGenerationRepository
{
    /// <summary>The one spelling of this collection's name.</summary>
    /// <remarks>
    /// Internal and shared with <see cref="MongoIndexInitializer"/>, because a
    /// second hand-typed copy of a collection name has already cost this
    /// codebase a near-miss — see <see cref="MongoBrandKitRepository"/>.
    /// </remarks>
    internal const string CollectionName = "ai_generations";

    private readonly IMongoCollection<AiGenerationDocument> _collection;

    public MongoAiGenerationRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<AiGenerationDocument>(CollectionName);
    }

    public Task AddAsync(AiGeneration generation, CancellationToken ct) =>
        _collection.InsertOneAsync(
            new AiGenerationDocument
            {
                Id = generation.Id,
                OwnerId = generation.OwnerId,
                Kind = (int)generation.Kind,
                Prompt = generation.Prompt,
                ResultName = generation.ResultName,
                Succeeded = generation.Succeeded,
                CreatedAtUtc = generation.CreatedAtUtc,
                UpdatedAtUtc = generation.UpdatedAtUtc,
            },
            cancellationToken: ct);

    public Task MarkSucceededAsync(AiGeneration generation, CancellationToken ct) =>
        _collection.UpdateOneAsync(
            Builders<AiGenerationDocument>.Filter.Eq(x => x.Id, generation.Id),
            Builders<AiGenerationDocument>.Update
                .Set(x => x.Succeeded, true)
                .Set(x => x.ResultName, generation.ResultName)
                .Set(x => x.UpdatedAtUtc, generation.UpdatedAtUtc),
            cancellationToken: ct);

    public async Task<int> CountSucceededThisMonthAsync(string ownerId, CancellationToken ct) =>
        await CountSince(ownerId, BillingMonth.StartUtc(), succeededOnly: true, ct);

    public async Task<int> CountAttemptsSinceAsync(
        string ownerId, DateTime sinceUtc, CancellationToken ct) =>
        await CountSince(ownerId, sinceUtc, succeededOnly: false, ct);

    private async Task<int> CountSince(
        string ownerId, DateTime sinceUtc, bool succeededOnly, CancellationToken ct)
    {
        var f = Builders<AiGenerationDocument>.Filter;
        var filter = f.Eq(x => x.OwnerId, ownerId) & f.Gte(x => x.CreatedAtUtc, sinceUtc);

        // `Eq(true)` rather than `Ne(false)`: in MongoDB a document missing the
        // field does not match an equality test against its default, and `Ne`
        // matches missing fields — which would count an attempt that never
        // succeeded. Every document this repository writes has the field, so
        // the two agree today; they stop agreeing the moment anything else
        // inserts one.
        if (succeededOnly) filter &= f.Eq(x => x.Succeeded, true);

        return (int)await _collection.CountDocumentsAsync(filter, cancellationToken: ct);
    }
}
