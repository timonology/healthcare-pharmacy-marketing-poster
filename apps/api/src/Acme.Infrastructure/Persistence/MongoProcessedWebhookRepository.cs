using Acme.Application.Billing;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoProcessedWebhookRepository : IProcessedWebhookRepository
{
    private const string CollectionName = "processed_webhooks";
    private readonly IMongoCollection<ProcessedWebhookDocument> _collection;

    public MongoProcessedWebhookRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<ProcessedWebhookDocument>(CollectionName);
    }

    public async Task<bool> TryMarkProcessedAsync(string eventId, string eventType, CancellationToken ct)
    {
        try
        {
            await _collection.InsertOneAsync(
                new ProcessedWebhookDocument
                {
                    Id = eventId,
                    EventType = eventType,
                    ProcessedAtUtc = DateTime.UtcNow,
                },
                cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another delivery of the same event already claimed it. Relying on
            // the unique _id rather than a read-then-write keeps this correct
            // even when Stripe retries land on two instances simultaneously.
            return false;
        }
    }
}

internal sealed class ProcessedWebhookDocument
{
    /// <summary>The Stripe event id (evt_…), used directly as the primary key.</summary>
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("eventType")]
    public string EventType { get; set; } = default!;

    [BsonElement("processedAtUtc")]
    public DateTime ProcessedAtUtc { get; set; }
}
