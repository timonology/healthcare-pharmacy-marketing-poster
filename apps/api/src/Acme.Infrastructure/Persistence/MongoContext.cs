using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Holds the <see cref="IMongoDatabase"/>. Cosmos DB for MongoDB and a vanilla
/// Mongo container both speak the same protocol, so the same code handles both
/// — but we apply Cosmos-specific tweaks only when the URL actually looks like
/// a Cosmos endpoint, otherwise we leave vanilla Mongo's defaults alone.
/// </summary>
public sealed class MongoContext
{
    static MongoContext()
    {
        // Tolerate BSON fields that no document class defines anymore.
        // Without this, deleting a property from a *Document class throws
        // FormatException when the driver tries to hydrate old rows.
        // The convention has to be registered exactly once per process — the
        // static ctor guarantees that.
        var pack = new ConventionPack { new IgnoreExtraElementsConvention(true) };
        ConventionRegistry.Register("IgnoreExtraElements", pack, _ => true);
    }

    public IMongoDatabase Database { get; }
    public string DatabaseName { get; }
    public string Host { get; }

    public MongoContext(IOptions<MongoOptions> options, ILogger<MongoContext> logger)
    {
        var raw = options.Value.ConnectionString;
        var dbName = options.Value.Database;

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Mongo:ConnectionString is empty. Set the environment variable " +
                "Mongo__ConnectionString (e.g. on Railway: ${{MongoDB.MONGO_URL}}).");

        if (string.IsNullOrWhiteSpace(dbName))
            throw new InvalidOperationException(
                "Mongo:Database is empty. Set Mongo__Database (e.g. 'acme').");

        MongoClientSettings settings;
        try
        {
            settings = MongoClientSettings.FromConnectionString(raw);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse Mongo connection string. Got value starting with: '{Redact(raw)}'. " +
                "Common causes: missing scheme (should start with 'mongodb://' or 'mongodb+srv://'); " +
                "unescaped special characters in the password (use percent-encoding for @, :, /, etc.); " +
                "or Railway template literal '${{MongoDB.MONGO_URL}}' wasn't expanded — check the " +
                "service name 'MongoDB' actually matches your Mongo plugin's display name.", ex);
        }

        // Cosmos DB for MongoDB requires retryWrites=false. Apply only when we
        // see a Cosmos hostname so vanilla Railway / Atlas Mongo keep retries on.
        var isCosmos = settings.Server is { Host: var host }
            && (host.EndsWith(".cosmos.azure.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".mongocluster.cosmos.azure.com", StringComparison.OrdinalIgnoreCase));
        if (isCosmos)
            settings.RetryWrites = false;

        // Fail fast at startup rather than at the first request.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);

        Host = settings.Server?.ToString() ?? "(unknown)";
        DatabaseName = dbName;

        logger.LogInformation(
            "Connecting to Mongo: host={Host} db={Database} cosmos={IsCosmos} scheme={Scheme} tls={Tls}",
            Host, dbName, isCosmos, settings.Scheme, settings.UseTls);

        try
        {
            var client = new MongoClient(settings);
            Database = client.GetDatabase(dbName);

            // Force a roundtrip so any auth/network issue throws now, with a
            // useful message, instead of much later from a random query path.
            var ping = Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1))
                .GetAwaiter().GetResult();
            logger.LogInformation("Mongo ping ok: {Ping}", ping.ToJson());
        }
        catch (MongoConfigurationException ex)
        {
            throw new InvalidOperationException(
                $"Mongo client misconfigured for host={Host}. {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Timed out connecting to Mongo at {Host}. " +
                "If this is Railway, make sure you used MONGO_URL (private) when " +
                "calling from another Railway service, and MONGO_PUBLIC_URL when " +
                "connecting from outside Railway. Also verify the Mongo plugin is " +
                "still attached and hasn't been deprecated.", ex);
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException(
                $"Mongo authentication failed against {Host}. Double-check the " +
                "username/password in the connection string — Railway rotates them " +
                "on plugin reset and special characters must be percent-encoded.", ex);
        }
    }

    public IMongoCollection<T> Collection<T>(string name) => Database.GetCollection<T>(name);

    /// <summary>Redacts credentials from a connection string for safe logging.</summary>
    private static string Redact(string raw)
    {
        var atIndex = raw.IndexOf('@');
        var schemeIndex = raw.IndexOf("://", StringComparison.Ordinal);
        if (atIndex > 0 && schemeIndex > 0 && atIndex > schemeIndex)
            return raw[..(schemeIndex + 3)] + "***:***" + raw[atIndex..];
        return raw.Length > 30 ? raw[..30] + "..." : raw;
    }
}
