using Acme.Infrastructure.Options;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Holds the <see cref="IMongoDatabase"/>. Cosmos DB for MongoDB and a vanilla
/// Mongo container both speak the same protocol, so the same code handles both.
/// </summary>
public sealed class MongoContext
{
    public IMongoDatabase Database { get; }

    public MongoContext(IOptions<MongoOptions> options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.Value.ConnectionString);
        // Cosmos DB recommends a small pool and retry-disabled writes for the MongoDB API.
        settings.RetryWrites = false;
        var client = new MongoClient(settings);
        Database = client.GetDatabase(options.Value.Database);
    }

    public IMongoCollection<T> Collection<T>(string name) => Database.GetCollection<T>(name);
}
