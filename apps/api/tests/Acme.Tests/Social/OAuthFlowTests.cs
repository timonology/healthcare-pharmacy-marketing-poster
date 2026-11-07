using System.Reflection;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// The OAuth round trip: that <c>state</c> is bound server-side, single use,
/// and that the server — not the browser — decides where the code goes.
/// </summary>
public class OAuthFlowTests
{
    private const string WebBase = "https://app.example.test";

    [Fact]
    public async Task The_redirect_is_the_servers_own_callback_page()
    {
        var h = new Harness();

        var started = await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);

        Assert.Equal(ResultKind.Success, started.Kind);
        Assert.Equal($"{WebBase}/social/callback/meta", h.Provider.LastRedirectUri);
    }

    [Fact]
    public async Task A_different_redirect_from_the_browser_is_refused()
    {
        var h = new Harness();

        var started = await h.Service.StartAsync(
            h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "https://evil.example/steal", "state-1", default);

        Assert.Equal(ResultKind.Invalid, started.Kind);
        Assert.Contains(WebBase, started.Error);
    }

    /// <summary>
    /// Production regression: the page open at exactly Public:WebBaseUrl must
    /// be allowed, whatever host the API itself is served from. The check is
    /// against the browser's address (sent as the redirect it expects), never
    /// the API's own Request.Host.
    /// </summary>
    [Theory]
    [InlineData("/social/callback/meta")]
    [InlineData("/social/callback/meta/")]
    public async Task A_page_open_at_the_configured_address_is_allowed(string path)
    {
        var h = new Harness();

        var started = await h.Service.StartAsync(
            h.Pharmacy.Id, SocialOAuthProviderKind.Meta, $"{WebBase}{path}", "state-1", default);

        Assert.Equal(ResultKind.Success, started.Kind);
        Assert.Equal($"{WebBase}/social/callback/meta", h.Provider.LastRedirectUri);
    }

    [Theory]
    [InlineData("https://evil.example/social/callback/meta")]
    [InlineData("http://app.example.test/social/callback/meta")]      // scheme differs
    [InlineData("https://www.app.example.test/social/callback/meta")] // host differs
    public async Task A_page_open_anywhere_else_is_refused_and_told_both_addresses(string requested)
    {
        var h = new Harness();

        var started = await h.Service.StartAsync(
            h.Pharmacy.Id, SocialOAuthProviderKind.Meta, requested, "state-1", default);

        Assert.Equal(ResultKind.Invalid, started.Kind);
        Assert.Contains(WebBase, started.Error);
        Assert.Contains(new Uri(requested).GetLeftPart(UriPartial.Authority), started.Error);
        Assert.Null(h.Provider.LastRedirectUri);
    }

    [Fact]
    public async Task A_callback_with_a_state_that_was_never_issued_is_refused()
    {
        var h = new Harness();

        var result = await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "forged", default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Equal(0, h.Provider.Exchanges);   // the code is never even redeemed
    }

    [Fact]
    public async Task A_state_issued_to_another_pharmacy_is_refused()
    {
        var h = new Harness();
        await h.Service.StartAsync(h.Other.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);

        var result = await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "state-1", default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Equal(0, h.Provider.Exchanges);
    }

    [Fact]
    public async Task A_state_is_redeemed_once()
    {
        var h = new Harness();
        await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);

        var first = await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "state-1", default);
        var replay = await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "state-1", default);

        Assert.Equal(ResultKind.Success, first.Kind);
        Assert.Equal(ResultKind.Invalid, replay.Kind);
        Assert.Equal(1, h.Provider.Exchanges);
    }

    [Fact]
    public async Task The_exchange_uses_the_redirect_recorded_at_start()
    {
        var h = new Harness();
        await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);

        await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "state-1", default);

        Assert.Equal($"{WebBase}/social/callback/meta", h.Provider.ExchangeRedirectUri);
    }

    [Fact]
    public async Task The_exchange_is_given_the_flows_state()
    {
        // X derives its PKCE verifier from the state; the exchange needs it.
        var h = new Harness();
        await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);

        await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "state-1", default);

        Assert.Equal("state-1", h.Provider.ExchangeState);
    }

    [Fact]
    public async Task Discovered_accounts_can_be_connected_once_and_only_by_their_pharmacy()
    {
        var h = new Harness();
        await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);
        await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", "state-1", default);

        var stolen = await h.Service.ConnectAsync(h.Other.Id, new ConnectAccountsRequest("state-1", new[] { "0" }), default);
        var mine = await h.Service.ConnectAsync(h.Pharmacy.Id, new ConnectAccountsRequest("state-1", new[] { "0" }), default);
        var again = await h.Service.ConnectAsync(h.Pharmacy.Id, new ConnectAccountsRequest("state-1", new[] { "0" }), default);

        Assert.NotEqual(ResultKind.Success, stolen.Kind);
        Assert.Equal(ResultKind.Success, mine.Kind);
        Assert.NotEqual(ResultKind.Success, again.Kind);
        Assert.Single(h.Connections.Items);
    }

    [Fact]
    public async Task Reconnecting_instagram_follows_it_to_its_new_page()
    {
        var h = new Harness();
        h.Provider.Accounts = new[]
        {
            new DiscoveredAccount("ig-1", "@pharmacy", SocialPlatform.InstagramBusiness, "tok", null, null,
                Array.Empty<string>(), ParentAccountId: "page-old"),
        };
        await Connect(h, "s1");

        h.Provider.Accounts = new[]
        {
            new DiscoveredAccount("ig-1", "@pharmacy", SocialPlatform.InstagramBusiness, "tok2", null, null,
                Array.Empty<string>(), ParentAccountId: "page-new"),
        };
        await Connect(h, "s2");

        Assert.Equal("page-new", h.Connections.Items.Values.Single().ParentAccountId);
    }

    [Fact]
    public async Task Without_an_encryption_key_nobody_is_sent_to_consent()
    {
        // Otherwise the pharmacy grants access at Meta and then hits a server
        // error when we try to store what they granted.
        var h = new Harness();
        h.Cipher.Configured = false;

        var started = await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, "state-1", default);
        var availability = await h.Service.AvailabilityAsync(h.Pharmacy.Id, default);

        Assert.Equal(ResultKind.Invalid, started.Kind);
        Assert.Null(h.Provider.LastRedirectUri);
        Assert.All(availability.Value!.Providers, p => Assert.False(p.Available));
    }

    [Fact]
    public async Task A_free_pharmacy_is_told_why_rather_than_offered_buttons_that_fail()
    {
        var h = new Harness();
        h.Pharmacy.ChangeTier(SubscriptionTier.Free);

        var availability = (await h.Service.AvailabilityAsync(h.Pharmacy.Id, default)).Value!;

        Assert.False(availability.PlanAllows);
        Assert.Contains("Starter", availability.PlanReason);
        Assert.All(availability.Providers, p => Assert.False(p.Available));
    }

    [Fact]
    public async Task A_paid_pharmacy_can_connect_a_configured_provider()
    {
        var h = new Harness();

        var availability = (await h.Service.AvailabilityAsync(h.Pharmacy.Id, default)).Value!;

        Assert.True(availability.PlanAllows);
        Assert.True(availability.Providers.Single(p => p.Kind == SocialOAuthProviderKind.Meta).Available);
        // No Google provider registered in this harness: unavailable, with a reason.
        var google = availability.Providers.Single(p => p.Kind == SocialOAuthProviderKind.Google);
        Assert.False(google.Available);
        Assert.NotNull(google.Reason);
    }

    [Theory]
    [InlineData(SocialOAuthProviderKind.Meta, "meta")]
    [InlineData(SocialOAuthProviderKind.YouTube, "youtube")]
    [InlineData(SocialOAuthProviderKind.LinkedIn, "linkedin")]
    [InlineData(SocialOAuthProviderKind.TikTok, "tiktok")]
    [InlineData(SocialOAuthProviderKind.X, "x")]
    public void Each_provider_returns_to_its_own_callback_page(SocialOAuthProviderKind kind, string path)
    {
        // The web app's /social/callback/[provider] page and the address
        // registered with each platform both depend on this exact spelling.
        Assert.Equal($"{WebBase}/social/callback/{path}", new Harness().Service.CallbackUrlFor(kind));
    }

    [Fact]
    public void The_stored_key_is_a_hash_of_the_state_not_the_state()
    {
        var key = MongoOAuthSessionStore.KeyFor("state-value");

        Assert.DoesNotContain("state-value", key);
        Assert.Equal(64, key.Length);
        Assert.Equal(key, MongoOAuthSessionStore.KeyFor("state-value"));
    }

    private static async Task Connect(Harness h, string state)
    {
        await h.Service.StartAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, null, state, default);
        await h.Service.CompleteAsync(h.Pharmacy.Id, SocialOAuthProviderKind.Meta, "code", state, default);
        var connected = await h.Service.ConnectAsync(h.Pharmacy.Id, new ConnectAccountsRequest(state, new[] { "0" }), default);
        Assert.Equal(ResultKind.Success, connected.Kind);
    }

    /* ================================================================ */

    private sealed class Harness
    {
        public User Pharmacy { get; } = Paid("a");
        public User Other { get; } = Paid("b");
        public FakeProvider Provider { get; } = new();
        public MemoryConnections Connections { get; } = new();
        private readonly MemoryFlows _flows = new();

        public PlainCipher Cipher { get; } = new();

        public SocialConnectionService Service => new(
            Connections,
            PublishExecutionTests.Unused<IScheduledPostRepository>.Instance,
            UsersOf.These(Pharmacy, Other),
            Cipher,
            _flows,
            new ISocialOAuthProvider[] { Provider },
            new Links(),
            NullLogger<SocialConnectionService>.Instance);

        private static User Paid(string tag)
        {
            var u = User.Register(Email.Create($"{tag}-{Guid.NewGuid():N}@example.test"), "Pharmacy", PasswordHash.FromHash("x"));
            u.ChangeTier(SubscriptionTier.Starter);
            return u;
        }
    }

    private sealed class FakeProvider : ISocialOAuthProvider
    {
        public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.Meta;
        public bool IsConfigured => true;
        public string? LastRedirectUri { get; private set; }
        public string? ExchangeRedirectUri { get; private set; }
        public int Exchanges { get; private set; }

        public IReadOnlyList<DiscoveredAccount> Accounts { get; set; } = new[]
        {
            new DiscoveredAccount("page-1", "Main St Pharmacy", SocialPlatform.FacebookPage, "page-token", null, null,
                Array.Empty<string>()),
        };

        public string BuildAuthorizationUrl(string state, string redirectUri)
        {
            LastRedirectUri = redirectUri;
            return $"https://provider.test/auth?state={state}";
        }

        public string? ExchangeState { get; private set; }

        public Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
        {
            Exchanges++;
            ExchangeRedirectUri = redirectUri;
            return Task.FromResult(Accounts);
        }

        public Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
            string code, string redirectUri, string state, CancellationToken ct)
        {
            ExchangeState = state;
            return ExchangeCodeAsync(code, redirectUri, ct);
        }
    }

    /// <summary>The store's contract — owner-bound, single-use takes — without Mongo.</summary>
    private sealed class MemoryFlows : IOAuthSessionStore
    {
        private readonly Dictionary<string, (OAuthFlow Flow, int Phase)> _items = new();

        public Task BeginAsync(string state, OAuthFlow flow, TimeSpan ttl, CancellationToken ct)
        {
            _items[state] = (flow, 0);
            return Task.CompletedTask;
        }

        public Task<OAuthFlow?> TakeStartedAsync(string state, string ownerId, SocialOAuthProviderKind kind, CancellationToken ct)
        {
            if (!_items.TryGetValue(state, out var e) || e.Phase != 0
                || e.Flow.OwnerId != ownerId || e.Flow.Kind != kind)
                return Task.FromResult<OAuthFlow?>(null);
            _items[state] = (e.Flow, 1);
            return Task.FromResult<OAuthFlow?>(e.Flow);
        }

        public Task SaveDiscoveredAsync(string state, OAuthFlow flow, TimeSpan ttl, CancellationToken ct)
        {
            if (_items.TryGetValue(state, out var e) && e.Phase == 1 && e.Flow.OwnerId == flow.OwnerId)
                _items[state] = (flow, 2);
            return Task.CompletedTask;
        }

        public Task<OAuthFlow?> TakeDiscoveredAsync(string state, string ownerId, CancellationToken ct)
        {
            if (!_items.TryGetValue(state, out var e) || e.Phase != 2 || e.Flow.OwnerId != ownerId)
                return Task.FromResult<OAuthFlow?>(null);
            _items.Remove(state);
            return Task.FromResult<OAuthFlow?>(e.Flow);
        }
    }

    private sealed class MemoryConnections : ISocialConnectionRepository
    {
        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct) =>
            Task.CompletedTask;

        public Dictionary<string, SocialConnection> Items { get; } = new();

        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Items.Values.Where(c => c.OwnerId == ownerId).ToList());

        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(Items.GetValueOrDefault(id));

        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) =>
            Task.FromResult(Items.Values.FirstOrDefault(c =>
                c.OwnerId == ownerId && c.Platform == platform && c.ExternalAccountId == externalAccountId));

        public Task AddAsync(SocialConnection connection, CancellationToken ct) { Items[connection.Id] = connection; return Task.CompletedTask; }
        public Task UpdateAsync(SocialConnection connection, CancellationToken ct) { Items[connection.Id] = connection; return Task.CompletedTask; }
        public Task<int> CountActiveAsync(string ownerId, CancellationToken ct) => Task.FromResult(Items.Count);

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
    }

    public sealed class PlainCipher : ITokenCipher
    {
        public bool Configured { get; set; } = true;
        public bool IsConfigured => Configured;
        public EncryptedSecret Encrypt(string plaintext) => EncryptedSecret.Create(plaintext, "n", "t", 1);
        public string Decrypt(EncryptedSecret secret) => secret.CipherText;
        public bool CanDecrypt(EncryptedSecret secret) => true;
    }

    private sealed class Links : IAuthLinkBuilder
    {
        public string BuildPasswordResetLink(string rawToken) => throw new NotSupportedException();
        public string BuildEmailVerificationLink(string rawToken) => throw new NotSupportedException();
        public string BuildAppLink(string path = "/dashboard") => $"{WebBase}/{path.TrimStart('/')}";
    }

    public class UsersOf : DispatchProxy
    {
        private User[] _users = Array.Empty<User>();

        public static IUserRepository These(params User[] users)
        {
            var proxy = Create<IUserRepository, UsersOf>();
            ((UsersOf)(object)proxy)._users = users;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IUserRepository.FindByIdAsync))
                throw new NotSupportedException(targetMethod?.Name);
            return Task.FromResult(_users.FirstOrDefault(u => u.Id == (string?)args![0]));
        }
    }
}
