using Acme.Application.Auth;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// Meta's deauthorize and data-deletion callbacks: anonymous endpoints whose
/// only credential is the signed_request, so the verification is the feature.
/// </summary>
public class MetaCallbackTests
{
    private const string Secret = "app-secret";

    private static readonly MetaSignedRequestReader Reader =
        new(Options.Create(new MetaOptions { AppId = "app", AppSecret = Secret }));

    private static string PayloadIssued(DateTimeOffset at) =>
        $$"""{"algorithm":"HMAC-SHA256","issued_at":{{at.ToUnixTimeSeconds()}},"user_id":"fb-user-1"}""";

    private static readonly string Payload = PayloadIssued(DateTimeOffset.UtcNow);

    /* ---------------- signed_request ---------------- */

    [Fact]
    public void A_request_signed_with_our_secret_is_read()
    {
        Assert.Equal("fb-user-1", Reader.ReadUserId(MetaSignedRequestReader.Sign(Payload, Secret)));
    }

    [Fact]
    public void A_request_signed_with_another_secret_is_refused()
    {
        Assert.Null(Reader.ReadUserId(MetaSignedRequestReader.Sign(Payload, "someone-elses")));
    }

    [Fact]
    public void A_tampered_payload_is_refused()
    {
        var genuine = MetaSignedRequestReader.Sign(Payload, Secret);
        var forged = MetaSignedRequestReader.Sign(
            """{"algorithm":"HMAC-SHA256","user_id":"victim"}""", Secret).Split('.')[1];

        Assert.Null(Reader.ReadUserId($"{genuine.Split('.')[0]}.{forged}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void Malformed_requests_are_refused(string? signedRequest)
    {
        Assert.Null(Reader.ReadUserId(signedRequest));
    }

    [Fact]
    public void A_replayed_old_request_is_refused()
    {
        // Captured once, it must not delete accounts the person reconnects later.
        var stale = MetaSignedRequestReader.Sign(PayloadIssued(DateTimeOffset.UtcNow.AddDays(-2)), Secret);
        Assert.Null(Reader.ReadUserId(stale));
    }

    [Fact]
    public void A_payload_naming_a_weaker_algorithm_is_refused()
    {
        var signed = MetaSignedRequestReader.Sign("""{"algorithm":"NONE","user_id":"fb-user-1"}""", Secret);
        Assert.Null(Reader.ReadUserId(signed));
    }

    [Fact]
    public void Nothing_is_accepted_when_no_app_secret_is_configured()
    {
        var unconfigured = new MetaSignedRequestReader(Options.Create(new MetaOptions()));
        Assert.Null(unconfigured.ReadUserId(MetaSignedRequestReader.Sign(Payload, "")));
    }

    /* ---------------- what the callbacks do ---------------- */

    [Fact]
    public async Task Deauthorize_revokes_only_the_signers_meta_connections()
    {
        var store = new Store();
        var mine = store.Add(SocialPlatform.FacebookPage, "fb-user-1");
        var myInstagram = store.Add(SocialPlatform.InstagramBusiness, "fb-user-1");
        var someoneElses = store.Add(SocialPlatform.FacebookPage, "fb-user-2");

        var revoked = await Service(store).DeauthorizeAsync("fb-user-1", default);

        Assert.Equal(2, revoked);
        Assert.Equal(ConnectionStatus.Revoked, mine.Status);
        Assert.Null(mine.AccessToken);
        Assert.Equal(ConnectionStatus.Revoked, myInstagram.Status);
        Assert.Equal(ConnectionStatus.Active, someoneElses.Status);
    }

    [Fact]
    public async Task Data_deletion_removes_the_records_and_returns_a_status_link()
    {
        var store = new Store();
        store.Add(SocialPlatform.FacebookPage, "fb-user-1");
        var other = store.Add(SocialPlatform.FacebookPage, "fb-user-2");

        var result = await Service(store).DeleteDataAsync("fb-user-1", default);

        Assert.Equal(1, result.Deleted);
        Assert.Single(store.Items);
        Assert.Contains(other.Id, store.Items.Keys);
        Assert.Matches("^[0-9a-f]{16}$", result.ConfirmationCode);
        Assert.Equal($"https://app.test/privacy/data-deletion?code={result.ConfirmationCode}", result.Url);
    }

    private static MetaCallbackService Service(Store store) =>
        new(store, new Links(), NullLogger<MetaCallbackService>.Instance);

    private sealed class Store : ISocialConnectionRepository
    {
        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct) =>
            Task.CompletedTask;

        public Dictionary<string, SocialConnection> Items { get; } = new();

        public SocialConnection Add(SocialPlatform platform, string authorizer)
        {
            var c = SocialConnection.Create("owner-1", platform, Guid.NewGuid().ToString("N"), "Page",
                EncryptedSecret.Create("t", "n", "t", 1), null, null);
            c.UpdateAuthorizer(authorizer);
            Items[c.Id] = c;
            return c;
        }

        public Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(
            IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Items.Values
                .Where(c => c.AuthorizedByUserId == userId && platforms.Contains(c.Platform)).ToList());

        public Task DeleteAsync(string id, CancellationToken ct) { Items.Remove(id); return Task.CompletedTask; }

        public Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct) =>
            Task.FromResult(Items.Values.Any(c =>
                c.GrantFingerprint == fingerprint && c.Id != exceptId && c.Status != ConnectionStatus.Revoked));

        public Task<SocialConnection?> ClaimForHealthCheckAsync(
            IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UpdateAsync(SocialConnection connection, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAsync(SocialConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Links : IAuthLinkBuilder
    {
        public string BuildPasswordResetLink(string rawToken) => throw new NotSupportedException();
        public string BuildEmailVerificationLink(string rawToken) => throw new NotSupportedException();
        public string BuildAppLink(string path = "/dashboard") => $"https://app.test/{path.TrimStart('/')}";
    }
}
