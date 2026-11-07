using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// The daily check: a token Meta no longer accepts shows "Reconnect" the day
/// it breaks; an outage changes nothing.
/// </summary>
public class ConnectionHealthTests
{
    [Fact]
    public async Task A_rejected_token_marks_the_connection_for_reconnecting()
    {
        var h = new Harness();
        var c = h.Due(SocialPlatform.FacebookPage);
        h.Probe.Answer = (ProbeResult.Unauthorized, "Token expired.");

        await h.Service.CheckDueAsync(10, default);

        Assert.Equal((c.Id, false, "Token expired."), h.Repo.Recorded.Single());
    }

    [Fact]
    public async Task A_working_token_is_recorded_as_verified()
    {
        var h = new Harness();
        var c = h.Due(SocialPlatform.FacebookPage);

        await h.Service.CheckDueAsync(10, default);

        Assert.Equal((c.Id, true, (string?)null), h.Repo.Recorded.Single());
    }

    [Fact]
    public async Task An_outage_changes_nothing()
    {
        var h = new Harness();
        h.Due(SocialPlatform.FacebookPage);
        h.Probe.Answer = (ProbeResult.Inconclusive, "503");

        await h.Service.CheckDueAsync(10, default);

        Assert.Empty(h.Repo.Recorded);
    }

    [Fact]
    public async Task A_probe_that_throws_does_not_stop_the_rest()
    {
        var h = new Harness();
        h.Due(SocialPlatform.FacebookPage);
        var second = h.Due(SocialPlatform.FacebookPage);
        h.Probe.ThrowOnce = true;

        var checkedCount = await h.Service.CheckDueAsync(10, default);

        Assert.Equal(2, checkedCount);
        Assert.Equal(second.Id, h.Repo.Recorded.Single().Id);
    }

    [Fact]
    public async Task Nothing_is_checked_without_an_encryption_key()
    {
        var h = new Harness();
        h.Due(SocialPlatform.FacebookPage);
        h.Cipher.Configured = false;

        Assert.Equal(0, await h.Service.CheckDueAsync(10, default));
    }

    [Fact]
    public async Task Only_platforms_with_a_probe_are_claimed()
    {
        var h = new Harness();

        await h.Service.CheckDueAsync(10, default);

        Assert.Equal(new[] { SocialPlatform.FacebookPage }, h.Repo.LastPlatforms);
    }

    [Fact]
    public async Task The_cap_per_pass_is_respected()
    {
        var h = new Harness();
        for (var i = 0; i < 5; i++) h.Due(SocialPlatform.FacebookPage);

        Assert.Equal(3, await h.Service.CheckDueAsync(3, default));
    }

    /* ================================================================ */

    private sealed class Harness
    {
        public Repo Repo { get; } = new();
        public Probe Probe { get; } = new();
        public OAuthFlowTests.PlainCipher Cipher { get; } = new();

        public ConnectionHealthService Service =>
            new(Repo, Cipher, new ISocialConnectionProbe[] { Probe }, NullLogger<ConnectionHealthService>.Instance);

        public SocialConnection Due(SocialPlatform platform)
        {
            var c = SocialConnection.Create("owner-1", platform, Guid.NewGuid().ToString("N"), "Page",
                EncryptedSecret.Create("page-token", "n", "t", 1), null, null);
            Repo.Queue.Enqueue(c);
            return c;
        }
    }

    private sealed class Probe : ISocialConnectionProbe
    {
        public (ProbeResult, string?) Answer { get; set; } = (ProbeResult.Healthy, null);
        public bool ThrowOnce { get; set; }

        public SocialPlatform Platform => SocialPlatform.FacebookPage;
        public bool IsConfigured => true;

        public Task<(ProbeResult Result, string? Detail)> ProbeAsync(
            string externalAccountId, string accessToken, CancellationToken ct)
        {
            if (ThrowOnce) { ThrowOnce = false; throw new HttpRequestException("boom"); }
            Assert.Equal("page-token", accessToken);
            return Task.FromResult(Answer);
        }
    }

    private sealed class Repo : ISocialConnectionRepository
    {
        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct) =>
            Task.CompletedTask;

        public Queue<SocialConnection> Queue { get; } = new();
        public List<(string Id, bool Healthy, string? Error)> Recorded { get; } = new();
        public IReadOnlyCollection<SocialPlatform>? LastPlatforms { get; private set; }

        public Task<SocialConnection?> ClaimForHealthCheckAsync(
            IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc, CancellationToken ct)
        {
            LastPlatforms = platforms;
            Assert.True(checkedBeforeUtc <= nowUtc - ConnectionHealthService.Interval + TimeSpan.FromSeconds(1));
            return Task.FromResult(Queue.Count > 0 ? Queue.Dequeue() : null);
        }

        public Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct)
        {
            Recorded.Add((id, healthy, error));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAsync(SocialConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAsync(SocialConnection connection, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct) => throw new NotSupportedException();
    }
}
