using System.Reflection;
using Acme.Application.Auth;
using Acme.Application.Posters;
using Acme.Application.Social;
using Acme.Domain.Posters;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// What the worker does with a claimed post: which failures it retries, which
/// it stops on, and — above all — that it never sends twice.
/// </summary>
/// <remarks>
/// Exercises <see cref="PublishService.ExecuteAsync"/> directly against
/// in-memory fakes. The claim itself is Mongo's findAndModify and is covered
/// by the integration environment; everything after the claim is here.
/// </remarks>
public class PublishExecutionTests
{
    private const string Worker = "worker-a";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /* ---------------- automatic retry ---------------- */

    [Fact]
    public async Task A_transient_failure_is_retried_after_a_backoff()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Next(new PublishOutcome(false, null, null, "Rate limited.", IsRetryable: true));
        var post = h.Claimed(fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        var target = post.Targets.Single();
        Assert.Equal(PublishStatus.Scheduled, post.Status);
        Assert.Equal(PublishTargetStatus.Pending, target.Status);
        Assert.Equal(1, target.AttemptCount);
        Assert.NotNull(post.NextAttemptAtUtc);
        Assert.True(post.NextAttemptAtUtc > DateTime.UtcNow.AddMinutes(1));
        Assert.Null(post.LeaseHolder);
        Assert.Contains("Rate limited", target.Error);
    }

    [Fact]
    public async Task Automatic_retries_stop_at_the_ceiling()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var post = h.Claimed(fb);

        for (var i = 0; i < PublishService.MaxAutomaticAttempts; i++)
        {
            h.Facebook.Next(new PublishOutcome(false, null, null, "Down.", IsRetryable: true));
            if (i > 0) post.MarkClaimed(Worker, DateTime.UtcNow.Add(Lease));
            await h.Service.ExecuteAsync(post, Lease, default);
        }

        Assert.Equal(PublishTargetStatus.Failed, post.Targets.Single().Status);
        Assert.Equal(PublishStatus.Failed, post.Status);
        Assert.Equal(PublishService.MaxAutomaticAttempts, h.Facebook.Calls.Count);
    }

    [Fact]
    public async Task A_refusal_is_final_and_an_auth_failure_flags_the_connection()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Next(new PublishOutcome(false, null, null, "Token expired.", IsAuthFailure: true));
        var post = h.Claimed(fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Equal(PublishTargetStatus.Failed, post.Targets.Single().Status);
        Assert.Equal(PublishStatus.Failed, post.Status);
        Assert.Equal(ConnectionStatus.NeedsReauth, fb.Status);
    }

    [Fact]
    public async Task A_sign_in_refused_because_another_worker_rotated_it_is_retried_not_a_reconnect()
    {
        // TikTok and X retire a refresh token the moment it is used. A daily
        // check or another instance renewing first leaves this publish holding
        // a dead one; that says nothing about the account.
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.OnCall = () => fb.RenewCredential(EncryptedSecret.Create("rotated-elsewhere", "n", "t", 1), null);
        h.Facebook.Next(new PublishOutcome(false, null, null, "The connection is no longer valid.", IsAuthFailure: true));
        var post = h.Claimed(fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Equal(ConnectionStatus.Active, fb.Status);
        Assert.Equal(PublishTargetStatus.Pending, post.Targets.Single().Status);   // waiting for its automatic retry
        Assert.Contains("renewed by another task", post.Targets.Single().Error);
    }

    /* ---------------- never twice ---------------- */

    [Fact]
    public async Task An_unknown_outcome_is_never_retried_automatically()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Next(new PublishOutcome(false, null, null, "Timed out.", OutcomeUnknown: true));
        var post = h.Claimed(fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Equal(PublishTargetStatus.Uncertain, post.Targets.Single().Status);
        Assert.Equal(PublishStatus.Failed, post.Status);
        Assert.Null(post.NextAttemptAtUtc);
        Assert.Single(h.Facebook.Calls);
    }

    [Fact]
    public async Task Success_without_a_post_id_is_uncertain_not_retryable()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Next(new PublishOutcome(true, null, null, null));
        var post = h.Claimed(fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Equal(PublishTargetStatus.Uncertain, post.Targets.Single().Status);
    }

    [Fact]
    public async Task A_publisher_that_throws_leaves_the_target_uncertain()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Throw(new TaskCanceledException("timeout"));
        var post = h.Claimed(fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Equal(PublishTargetStatus.Uncertain, post.Targets.Single().Status);
    }

    [Fact]
    public async Task A_reclaimed_post_does_not_resend_the_account_that_was_mid_call()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var ig = h.Connect(SocialPlatform.InstagramBusiness);
        h.Instagram.Next(new PublishOutcome(true, "ig-1", null, null));

        var post = h.Claimed(fb, ig);
        // The previous worker got as far as sending to Facebook, then died.
        post.TargetFor(fb.Id)!.MarkPublishing();

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Empty(h.Facebook.Calls);
        Assert.Equal(PublishTargetStatus.Uncertain, post.TargetFor(fb.Id)!.Status);
        Assert.Equal(PublishTargetStatus.Published, post.TargetFor(ig.Id)!.Status);
        Assert.Equal(PublishStatus.PartiallyPublished, post.Status);
    }

    [Fact]
    public async Task Losing_the_lease_stops_before_anything_more_is_sent()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var ig = h.Connect(SocialPlatform.InstagramBusiness);
        h.Facebook.Next(new PublishOutcome(true, "fb-1", null, null));
        h.Instagram.Next(new PublishOutcome(true, "ig-1", null, null));
        var post = h.Claimed(fb, ig);

        // Facebook's checkpoint and result land; then another instance takes over.
        h.Posts.LoseLeaseAfterSaves = 2;

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Single(h.Facebook.Calls);
        Assert.Empty(h.Instagram.Calls);
    }

    [Fact]
    public async Task The_lease_is_renewed_before_each_account()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Next(new PublishOutcome(true, "fb-1", null, null));
        var post = h.Claimed(fb);
        post.MarkClaimed(Worker, DateTime.UtcNow.AddSeconds(5));   // nearly lapsed

        DateTime? leaseWhenSent = null;
        h.Facebook.OnCall = () => leaseWhenSent = post.LeaseExpiresAtUtc;

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.NotNull(leaseWhenSent);
        Assert.True(leaseWhenSent > DateTime.UtcNow.AddMinutes(4));
    }

    /* ---------------- Instagram media ---------------- */

    [Fact]
    public async Task Instagram_gets_its_own_version_of_a_poster()
    {
        var h = new Harness { InstagramUrl = "https://blob/poster-ig.jpg" };
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var ig = h.Connect(SocialPlatform.InstagramBusiness);
        h.Facebook.Next(new PublishOutcome(true, "fb-1", null, null));
        h.Instagram.Next(new PublishOutcome(true, "ig-1", null, null));

        await h.Service.ExecuteAsync(h.Claimed(fb, ig), Lease, default);

        Assert.Equal("https://blob/poster.png", h.Facebook.Calls.Single().MediaUrl);
        Assert.Equal("https://blob/poster-ig.jpg", h.Instagram.Calls.Single().MediaUrl);
        Assert.True(h.Media.AskedForInstagram);
    }

    [Fact]
    public async Task Instagram_is_not_sent_the_png_when_its_version_could_not_be_made()
    {
        var h = new Harness { InstagramUrl = null };
        var ig = h.Connect(SocialPlatform.InstagramBusiness);
        var post = h.Claimed(ig);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Empty(h.Instagram.Calls);
        Assert.Equal(PublishTargetStatus.Pending, post.Targets.Single().Status);
        Assert.Equal(PublishStatus.Scheduled, post.Status);   // retried later
    }

    [Fact]
    public async Task A_facebook_only_publish_does_not_build_the_instagram_version()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        h.Facebook.Next(new PublishOutcome(true, "fb-1", null, null));

        await h.Service.ExecuteAsync(h.Claimed(fb), Lease, default);

        Assert.False(h.Media.AskedForInstagram);
    }

    /* ---------------- checked again when it is due ---------------- */

    [Fact]
    public async Task A_deactivated_pharmacys_scheduled_post_does_not_go_out()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var post = h.Claimed(fb);
        h.Pharmacy.Deactivate();

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Empty(h.Facebook.Calls);
        Assert.Equal(PublishStatus.Failed, post.Status);
    }

    [Fact]
    public async Task A_pharmacy_that_dropped_to_free_does_not_publish()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var post = h.Claimed(fb);
        h.Pharmacy.ChangeTier(SubscriptionTier.Free);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Empty(h.Facebook.Calls);
        Assert.Contains("Starter", post.Targets.Single().Error);
    }

    [Fact]
    public async Task A_poster_taken_back_to_draft_is_not_published()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var poster = Poster.Create(h.Pharmacy.Id, "Flu clinic", "{}");   // a draft
        h.Posters.Add(poster);
        var post = h.Claimed(poster.Id, fb);

        await h.Service.ExecuteAsync(post, Lease, default);

        Assert.Empty(h.Facebook.Calls);
        Assert.Contains("draft", post.Targets.Single().Error);
    }

    [Fact]
    public async Task A_published_poster_goes_out()
    {
        var h = new Harness();
        var fb = h.Connect(SocialPlatform.FacebookPage);
        var poster = Poster.Create(h.Pharmacy.Id, "Flu clinic", "{}");
        poster.Publish();
        h.Posters.Add(poster);
        h.Facebook.Next(new PublishOutcome(true, "fb-1", null, null));

        await h.Service.ExecuteAsync(h.Claimed(poster.Id, fb), Lease, default);

        Assert.Single(h.Facebook.Calls);
    }

    /* ================================================================ *
     * Fakes
     * ================================================================ */

    private sealed class Harness
    {
        public FakePosts Posts { get; } = new();
        public FakeConnections Connections { get; } = new();
        public FakePublisher Facebook { get; } = new(SocialPlatform.FacebookPage);
        public FakePublisher Instagram { get; } = new(SocialPlatform.InstagramBusiness);
        public FakeMedia Media { get; } = new();

        public string? InstagramUrl { get => Media.InstagramUrl; init => Media.InstagramUrl = value; }

        public User Pharmacy { get; } = PaidPharmacy();
        public List<Poster> Posters { get; } = new();

        public PublishService Service => new(
            Posts,
            Connections,
            OAuthFlowTests.UsersOf.These(Pharmacy),
            new PlainCipher(),
            Media,
            PostersOf.These(Posters),
            new ISocialPublisher[] { Facebook, Instagram },
            NullLogger<PublishService>.Instance);

        public SocialConnection Connect(SocialPlatform platform)
        {
            var c = SocialConnection.Create(
                Pharmacy.Id, platform, $"ext-{Guid.NewGuid():N}", platform.ToString(),
                EncryptedSecret.Create("token", "n", "t", 1), null, null);
            Connections.Items[c.Id] = c;
            return c;
        }

        public ScheduledPost Claimed(params SocialConnection[] targets) => Claimed("poster-1", targets);

        public ScheduledPost Claimed(string posterId, params SocialConnection[] targets)
        {
            var post = ScheduledPost.Create(
                Pharmacy.Id, PublishSourceKind.Poster, posterId, "Flu clinic", "Book now", null,
                targets.Select(t => (t.Id, t.Platform)));
            post.MarkClaimed(Worker, DateTime.UtcNow.Add(Lease));
            Posts.Items[post.Id] = post;
            return post;
        }
    }

    private sealed class FakePosts : IScheduledPostRepository
    {
        public Dictionary<string, ScheduledPost> Items { get; } = new();

        /// <summary>Conditional saves succeed this many times, then report the lease lost.</summary>
        public int LoseLeaseAfterSaves { get; set; } = int.MaxValue;
        private int _saves;

        public Task<bool> TryUpdateClaimedAsync(ScheduledPost post, string leaseHolder, CancellationToken ct) =>
            Task.FromResult(++_saves <= LoseLeaseAfterSaves);

        public Task<bool> TryUpdateIfStatusAsync(ScheduledPost post, PublishStatus expectedStatus, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<ScheduledPost?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(Items.GetValueOrDefault(id));

        public Task AddAsync(ScheduledPost post, CancellationToken ct) { Items[post.Id] = post; return Task.CompletedTask; }
        public Task UpdateAsync(ScheduledPost post, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ScheduledPost>> ListAsync(ScheduledPostQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountAsync(ScheduledPostQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScheduledPost?> ClaimNextDueAsync(string leaseHolder, TimeSpan leaseDuration, DateTime nowUtc, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountPendingForConnectionAsync(string connectionId, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeConnections : ISocialConnectionRepository
    {
        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct) =>
            Task.CompletedTask;

        public Dictionary<string, SocialConnection> Items { get; } = new();

        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(Items.GetValueOrDefault(id));

        public Task UpdateAsync(SocialConnection connection, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Items.Values.Where(c => c.OwnerId == ownerId).ToList());

        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) =>
            Task.FromResult(Items.Values.FirstOrDefault(c =>
                c.OwnerId == ownerId && c.Platform == platform && c.ExternalAccountId == externalAccountId));

        public Task AddAsync(SocialConnection connection, CancellationToken ct) { Items[connection.Id] = connection; return Task.CompletedTask; }
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

    private sealed class FakePublisher(SocialPlatform platform) : ISocialPublisher
    {
        private readonly Queue<Func<PublishOutcome>> _outcomes = new();

        public SocialPlatform Platform => platform;
        public bool IsConfigured => true;
        public List<PublishRequest> Calls { get; } = new();
        public Action? OnCall { get; set; }

        public void Next(PublishOutcome outcome) => _outcomes.Enqueue(() => outcome);
        public void Throw(Exception ex) => _outcomes.Enqueue(() => throw ex);

        public Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            OnCall?.Invoke();
            return Task.FromResult(_outcomes.Count > 0
                ? _outcomes.Dequeue()()
                : new PublishOutcome(false, null, null, "No outcome queued."));
        }
    }

    private sealed class FakeMedia : IPublishMediaResolver
    {
        public string? InstagramUrl { get; set; } = "https://blob/poster-ig.jpg";
        public bool AskedForInstagram { get; private set; }

        public Task<ResolvedMedia?> ResolveAsync(
            PublishSourceKind kind, string sourceId, string ownerId, bool forInstagram, CancellationToken ct)
        {
            AskedForInstagram |= forInstagram;
            return Task.FromResult<ResolvedMedia?>(new ResolvedMedia(
                "https://blob/poster.png", IsVideo: false, "Flu clinic",
                forInstagram ? InstagramUrl : null));
        }
    }

    private sealed class PlainCipher : ITokenCipher
    {
        public bool IsConfigured => true;
        public EncryptedSecret Encrypt(string plaintext) => EncryptedSecret.Create(plaintext, "n", "t", 1);
        public string Decrypt(EncryptedSecret secret) => secret.CipherText;
        public bool CanDecrypt(EncryptedSecret secret) => true;
    }

    private static User PaidPharmacy()
    {
        var u = User.Register(Email.Create($"p-{Guid.NewGuid():N}@example.test"), "Pharmacy", PasswordHash.FromHash("x"));
        u.ChangeTier(SubscriptionTier.Starter);
        return u;
    }

    /// <summary>Posters by id; anything not listed is "not found".</summary>
    public class PostersOf : DispatchProxy
    {
        private List<Poster> _posters = new();

        public static IPosterRepository These(List<Poster> posters)
        {
            var proxy = Create<IPosterRepository, PostersOf>();
            ((PostersOf)(object)proxy)._posters = posters;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPosterRepository.FindByIdAsync))
                throw new NotSupportedException(targetMethod?.Name);
            return Task.FromResult(_posters.FirstOrDefault(p => p.Id == (string?)args![0]));
        }
    }

    /// <summary>A dependency the code under test must not touch.</summary>
    public class Unused<T> : DispatchProxy where T : class
    {
        public static readonly T Instance = Create<T, Unused<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{typeof(T).Name}.{targetMethod?.Name} was not expected to be called.");
    }
}
