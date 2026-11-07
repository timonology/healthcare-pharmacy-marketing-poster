using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// Disconnecting: our copy always goes; the grant at Google or LinkedIn goes
/// only when nobody else — in any pharmacy — still uses it.
/// </summary>
public class DisconnectTests
{
    [Fact]
    public async Task The_last_connection_on_a_google_grant_revokes_it_at_google()
    {
        var h = new Harness();
        var ads = h.Google(h.Pharmacy.Id, "refresh-1");

        var result = await h.Service.DisconnectAsync(h.Pharmacy.Id, ads.Id, default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal(new[] { "refresh-1" }, h.Provider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, ads.Status);
    }

    [Fact]
    public async Task A_grant_another_pharmacy_still_uses_is_left_alone()
    {
        // A head office connected a branch with the same Google sign-in.
        // Revoking at Google would silently break the branch.
        var h = new Harness();
        var mine = h.Google(h.Pharmacy.Id, "shared-refresh");
        h.Google("another-pharmacy", "shared-refresh");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, mine.Id, default);

        Assert.Empty(h.Provider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, mine.Status);   // our copy still goes
    }

    [Fact]
    public async Task A_platform_that_cannot_be_reached_does_not_undo_the_disconnect()
    {
        var h = new Harness();
        h.Provider.Fail = true;
        var ads = h.Google(h.Pharmacy.Id, "refresh-1");

        var result = await h.Service.DisconnectAsync(h.Pharmacy.Id, ads.Id, default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal(ConnectionStatus.Revoked, ads.Status);
        Assert.Null(ads.RefreshToken);
    }

    [Fact]
    public async Task Meta_connections_are_not_revoked_at_the_platform()
    {
        // A Page token cannot be revoked on its own; withdrawing the person's
        // whole grant would take their other Pages with it.
        var h = new Harness();
        var page = SocialConnection.Create(h.Pharmacy.Id, SocialPlatform.FacebookPage, "page-1", "Page",
            EncryptedSecret.Create("page-token", "n", "t", 1), null, null);
        h.Connections.Items[page.Id] = page;

        await h.Service.DisconnectAsync(h.Pharmacy.Id, page.Id, default);

        Assert.Empty(h.Provider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, page.Status);
    }

    [Fact]
    public async Task Disconnecting_ads_leaves_the_google_grant_while_a_youtube_channel_is_connected()
    {
        // Google withdraws a person's whole sign-in to the app at once, so
        // revoking for Ads would also cut off the channel.
        var h = new Harness();
        var ads = h.Google(h.Pharmacy.Id, "refresh-ads");
        var channel = SocialConnection.Create(h.Pharmacy.Id, SocialPlatform.YouTubeChannel, "UC1", "Channel",
            EncryptedSecret.Create("refresh-yt", "n", "t", 1), EncryptedSecret.Create("refresh-yt", "n", "t", 1), null);
        h.Connections.Items[channel.Id] = channel;

        await h.Service.DisconnectAsync(h.Pharmacy.Id, ads.Id, default);

        Assert.Empty(h.Provider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, ads.Status);
        Assert.Equal(ConnectionStatus.Active, channel.Status);
    }

    [Fact]
    public async Task Disconnecting_youtube_never_revokes_at_google()
    {
        var h = new Harness();
        var channel = SocialConnection.Create(h.Pharmacy.Id, SocialPlatform.YouTubeChannel, "UC1", "Channel",
            EncryptedSecret.Create("refresh-yt", "n", "t", 1), EncryptedSecret.Create("refresh-yt", "n", "t", 1), null);
        h.Connections.Items[channel.Id] = channel;

        await h.Service.DisconnectAsync(h.Pharmacy.Id, channel.Id, default);

        Assert.Empty(h.Provider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, channel.Status);
    }

    [Fact]
    public async Task The_last_linkedin_connection_on_a_sign_in_revokes_it_at_linkedin()
    {
        var h = new Harness();
        var profile = h.LinkedIn(h.Pharmacy.Id, SocialPlatform.LinkedInProfile, "li-access-1");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, profile.Id, default);

        Assert.Equal(new[] { "li-access-1" }, h.LinkedInProvider.Revoked);
        Assert.Empty(h.Provider.Revoked);   // never Google's
        Assert.Equal(ConnectionStatus.Revoked, profile.Status);
    }

    [Fact]
    public async Task A_linkedin_page_on_the_same_sign_in_keeps_it_until_it_goes_too()
    {
        // One LinkedIn consent backs the profile and the Pages chosen with it.
        var h = new Harness();
        var profile = h.LinkedIn(h.Pharmacy.Id, SocialPlatform.LinkedInProfile, "li-shared");
        var page = h.LinkedIn(h.Pharmacy.Id, SocialPlatform.LinkedInPage, "li-shared");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, profile.Id, default);
        Assert.Empty(h.LinkedInProvider.Revoked);
        Assert.Equal(ConnectionStatus.Active, page.Status);

        await h.Service.DisconnectAsync(h.Pharmacy.Id, page.Id, default);
        Assert.Equal(new[] { "li-shared" }, h.LinkedInProvider.Revoked);
    }

    [Fact]
    public async Task An_old_linkedin_token_is_not_revoked_while_the_same_person_has_a_newer_one_connected()
    {
        // The profile was reconnected (new token); the Page was not.
        var h = new Harness();
        h.LinkedIn(h.Pharmacy.Id, SocialPlatform.LinkedInProfile, "li-new");
        var page = h.LinkedIn(h.Pharmacy.Id, SocialPlatform.LinkedInPage, "li-old");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, page.Id, default);

        Assert.Empty(h.LinkedInProvider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, page.Status);
    }

    [Fact]
    public async Task Disconnecting_tiktok_revokes_the_sign_in_at_tiktok()
    {
        var h = new Harness();
        var account = h.TikTok(h.Pharmacy.Id, "open-1", "tiktok-credential-1");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, account.Id, default);

        Assert.Equal(new[] { "tiktok-credential-1" }, h.TikTokProvider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, account.Status);
    }

    [Fact]
    public async Task A_tiktok_account_another_pharmacy_still_uses_is_not_revoked()
    {
        // TikTok's revoke ends the person's whole authorisation of the app,
        // which would cut off the other pharmacy too.
        var h = new Harness();
        var mine = h.TikTok(h.Pharmacy.Id, "open-1", "credential-a");
        h.TikTok("another-pharmacy", "open-1", "credential-b");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, mine.Id, default);

        Assert.Empty(h.TikTokProvider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, mine.Status);
    }

    [Fact]
    public async Task Disconnecting_x_revokes_the_grant_at_x()
    {
        var h = new Harness();
        var account = h.X(h.Pharmacy.Id, "x-user-1", "x-credential-1");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, account.Id, default);

        Assert.Equal(new[] { "x-credential-1" }, h.XProvider.Revoked);
        Assert.Empty(h.TikTokProvider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, account.Status);
    }

    [Fact]
    public async Task An_x_account_another_pharmacy_still_uses_is_not_revoked()
    {
        var h = new Harness();
        var mine = h.X(h.Pharmacy.Id, "x-user-1", "credential-a");
        h.X("another-pharmacy", "x-user-1", "credential-b");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, mine.Id, default);

        Assert.Empty(h.XProvider.Revoked);
        Assert.Equal(ConnectionStatus.Revoked, mine.Status);
    }

    [Fact]
    public async Task A_tiktok_connection_by_the_same_id_does_not_stop_an_x_revoke()
    {
        // Same external id on another platform is someone else entirely.
        var h = new Harness();
        var mine = h.X(h.Pharmacy.Id, "shared-id", "x-credential");
        h.TikTok(h.Pharmacy.Id, "shared-id", "tiktok-credential");

        await h.Service.DisconnectAsync(h.Pharmacy.Id, mine.Id, default);

        Assert.Equal(new[] { "x-credential" }, h.XProvider.Revoked);
    }

    /* ================================================================ */

    private sealed class Harness
    {
        public User Pharmacy { get; } = Paid();
        public Connections Connections { get; } = new();
        public FakeProvider Provider { get; } = new();
        public FakeProvider LinkedInProvider { get; } = new(SocialOAuthProviderKind.LinkedIn);
        public FakeProvider TikTokProvider { get; } = new(SocialOAuthProviderKind.TikTok);
        public FakeProvider XProvider { get; } = new(SocialOAuthProviderKind.X);

        public SocialConnectionService Service => new(
            Connections,
            GoogleAdsSafeguardTests.Answer<IScheduledPostRepository>.With(new()
            {
                ["CountPendingForConnectionAsync"] = _ => Task.FromResult(0),
            }),
            OAuthFlowTests.UsersOf.These(Pharmacy),
            new OAuthFlowTests.PlainCipher(),
            GoogleAdsSafeguardTests.Answer<IOAuthSessionStore>.With(new()),
            new ISocialOAuthProvider[] { Provider, LinkedInProvider, TikTokProvider, XProvider },
            GoogleAdsSafeguardTests.Answer<IAuthLinkBuilder>.With(new()),
            NullLogger<SocialConnectionService>.Instance);

        public SocialConnection Google(string ownerId, string refreshToken)
        {
            var c = SocialConnection.Create(ownerId, SocialPlatform.GoogleAds, Guid.NewGuid().ToString("N")[..10], "Ads",
                EncryptedSecret.Create("access", "n", "t", 1),
                EncryptedSecret.Create(refreshToken, "n", "t", 1), null);
            c.UpdateGrantFingerprint(SocialConnectionService.Fingerprint(refreshToken));
            Connections.Items[c.Id] = c;
            return c;
        }

        /// <summary>A LinkedIn connection as the provider makes it: the credential in both slots.</summary>
        public SocialConnection LinkedIn(string ownerId, SocialPlatform platform, string credential, string member = "member-1")
        {
            var c = SocialConnection.Create(ownerId, platform, Guid.NewGuid().ToString("N")[..10], "LinkedIn",
                EncryptedSecret.Create(credential, "n", "t", 1),
                EncryptedSecret.Create(credential, "n", "t", 1), DateTime.UtcNow.AddDays(60));
            c.UpdateGrantFingerprint(SocialConnectionService.Fingerprint(credential));
            c.UpdateAuthorizer(member);
            Connections.Items[c.Id] = c;
            return c;
        }

        public SocialConnection TikTok(string ownerId, string openId, string credential)
        {
            var c = SocialConnection.Create(ownerId, SocialPlatform.TikTokAccount, openId, "TikTok",
                EncryptedSecret.Create(credential, "n", "t", 1), null, DateTime.UtcNow.AddDays(365));
            c.UpdateAuthorizer(openId);
            Connections.Items[c.Id] = c;
            return c;
        }

        public SocialConnection X(string ownerId, string userId, string credential)
        {
            var c = SocialConnection.Create(ownerId, SocialPlatform.XAccount, userId, "@pharmacy",
                EncryptedSecret.Create(credential, "n", "t", 1), null, null);
            c.UpdateAuthorizer(userId);
            Connections.Items[c.Id] = c;
            return c;
        }

        private static User Paid()
        {
            var u = User.Register(Email.Create($"d-{Guid.NewGuid():N}@example.test"), "Pharmacy", PasswordHash.FromHash("x"));
            u.ChangeTier(SubscriptionTier.Starter);
            return u;
        }
    }

    private sealed class FakeProvider(SocialOAuthProviderKind kind = SocialOAuthProviderKind.Google) : ISocialOAuthProvider
    {
        public List<string> Revoked { get; } = new();
        public bool Fail { get; set; }

        public SocialOAuthProviderKind Kind => kind;
        public bool IsConfigured => true;
        public string BuildAuthorizationUrl(string state, string redirectUri) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RevokeAsync(string credential, CancellationToken ct)
        {
            if (Fail) throw new HttpRequestException("unreachable");
            Revoked.Add(credential);
            return Task.CompletedTask;
        }
    }

    private sealed class Connections : ISocialConnectionRepository
    {
        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct) =>
            Task.CompletedTask;

        public Dictionary<string, SocialConnection> Items { get; } = new();

        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(Items.GetValueOrDefault(id));
        public Task UpdateAsync(SocialConnection connection, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct) =>
            Task.FromResult(Items.Values.Any(c =>
                c.GrantFingerprint == fingerprint && c.Id != exceptId && c.Status != ConnectionStatus.Revoked));

        public Task<SocialConnection?> ClaimForHealthCheckAsync(
            IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Items.Values.Where(c => c.OwnerId == ownerId).ToList());
        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAsync(SocialConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Items.Values
                .Where(c => platforms.Contains(c.Platform) && c.AuthorizedByUserId == userId).ToList());
        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
