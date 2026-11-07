using System.Reflection;
using Acme.Application.Auth;
using Acme.Domain.Users;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class AuthTokenDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("userId")] public string UserId { get; set; } = default!;
    [BsonElement("purpose")] public int Purpose { get; set; }

    /// <summary>SHA-256 hex. The raw token is never stored — see AuthToken.</summary>
    [BsonElement("tokenHash")] public string TokenHash { get; set; } = default!;

    [BsonElement("sentToEmail")] public string SentToEmail { get; set; } = default!;
    [BsonElement("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("usedAtUtc")] public DateTime? UsedAtUtc { get; set; }

    [BsonElement("isSuperseded")]
    [BsonDefaultValue(false)]
    public bool IsSuperseded { get; set; }

    /// <summary>accepted / rejected / error; absent on tokens from before it was recorded.</summary>
    [BsonElement("emailOutcome")]
    [BsonIgnoreIfNull]
    public string? EmailOutcome { get; set; }

    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MongoAuthTokenRepository(MongoContext ctx) : IAuthTokenRepository
{
    private IMongoCollection<AuthTokenDocument> Col =>
        ctx.Collection<AuthTokenDocument>("auth_tokens");

    private static readonly ConstructorInfo Ctor =
        typeof(AuthToken).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("AuthToken parameterless ctor missing.");

    public Task AddAsync(AuthToken token, CancellationToken ct) =>
        Col.InsertOneAsync(ToDocument(token), cancellationToken: ct);

    public Task UpdateAsync(AuthToken token, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == token.Id, ToDocument(token),
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<AuthToken?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.TokenHash == tokenHash).FirstOrDefaultAsync(ct);
        return doc is null ? null : Hydrate(doc);
    }

    public async Task<IReadOnlyList<AuthToken>> ListOutstandingAsync(
        string userId, AuthTokenPurpose purpose, CancellationToken ct)
    {
        // Unused and unexpired only. Filtering in the query rather than in
        // memory keeps this cheap on an account that has requested a lot of
        // resets, which is exactly the account under attack.
        var docs = await Col
            .Find(x => x.UserId == userId
                    && x.Purpose == (int)purpose
                    && x.UsedAtUtc == null
                    && !x.IsSuperseded
                    && x.ExpiresAtUtc > DateTime.UtcNow)
            .ToListAsync(ct);

        return docs.Select(Hydrate).ToList();
    }

    public async Task<AuthToken?> FindLatestAsync(
        string userId, AuthTokenPurpose purpose, CancellationToken ct)
    {
        var doc = await Col
            .Find(x => x.UserId == userId && x.Purpose == (int)purpose)
            .SortByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : Hydrate(doc);
    }

    private static AuthTokenDocument ToDocument(AuthToken t) => new()
    {
        Id = t.Id,
        UserId = t.UserId,
        Purpose = (int)t.Purpose,
        TokenHash = t.TokenHash,
        SentToEmail = t.SentToEmail,
        ExpiresAtUtc = t.ExpiresAtUtc,
        UsedAtUtc = t.UsedAtUtc,
        IsSuperseded = t.IsSuperseded,
        EmailOutcome = t.EmailOutcome,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc,
    };

    private static AuthToken Hydrate(AuthTokenDocument d)
    {
        var t = (AuthToken)Ctor.Invoke(null);
        Set(t, nameof(AuthToken.Id), d.Id);
        Set(t, nameof(AuthToken.UserId), d.UserId);
        Set(t, nameof(AuthToken.Purpose), (AuthTokenPurpose)d.Purpose);
        Set(t, nameof(AuthToken.TokenHash), d.TokenHash);
        Set(t, nameof(AuthToken.SentToEmail), d.SentToEmail);
        Set(t, nameof(AuthToken.ExpiresAtUtc), d.ExpiresAtUtc);
        Set(t, nameof(AuthToken.UsedAtUtc), d.UsedAtUtc);
        Set(t, nameof(AuthToken.IsSuperseded), d.IsSuperseded);
        Set(t, nameof(AuthToken.EmailOutcome), d.EmailOutcome);
        Set(t, nameof(AuthToken.CreatedAtUtc), d.CreatedAtUtc);
        Set(t, nameof(AuthToken.UpdatedAtUtc), d.UpdatedAtUtc);
        return t;
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}

/// <summary>Builds account URLs from the configured public web address.</summary>
/// <remarks>
/// Shares its base-URL resolution with the invitation links, including the
/// fallback to the CORS origin — which is already the web app's public address,
/// so a new environment has one fewer setting to forget and cannot silently
/// point somewhere else.
/// </remarks>
public sealed class ConfiguredAuthLinkBuilder(IConfiguration configuration) : IAuthLinkBuilder
{
    private string BaseUrl =>
        (configuration["Public:WebBaseUrl"]
         ?? configuration["Cors:Origins:0"]
         ?? "http://localhost:3000").TrimEnd('/');

    public string BuildPasswordResetLink(string rawToken) =>
        $"{BaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";

    public string BuildEmailVerificationLink(string rawToken) =>
        $"{BaseUrl}/verify-email?token={Uri.EscapeDataString(rawToken)}";

    public string BuildAppLink(string path = "/dashboard") =>
        $"{BaseUrl}/{path.TrimStart('/')}";
}
