using Acme.Domain.Social;

namespace Acme.Application.Social;

/* ------------------------------------------------------------------ *
 * Token protection
 * ------------------------------------------------------------------ */

/// <summary>
/// Encrypts and decrypts platform credentials.
/// </summary>
/// <remarks>
/// An interface rather than a static helper so the key material stays in
/// infrastructure and the application layer cannot accidentally acquire the
/// ability to read a token where it shouldn't. Implementations must be
/// authenticated encryption — detecting tampering matters here, because a
/// credential store an attacker can modify undetected is one they can use to
/// redirect a pharmacy's posts.
/// </remarks>
public interface ITokenCipher
{
    /// <summary>
    /// False when no encryption key is configured. Nothing can be connected or
    /// published until one is, so callers check this before sending a pharmacy
    /// off to consent at Meta or Google.
    /// </summary>
    bool IsConfigured { get; }

    EncryptedSecret Encrypt(string plaintext);

    /// <summary>
    /// Recovers the plaintext, or throws if the key version is unknown or the
    /// authentication tag does not verify.
    /// </summary>
    string Decrypt(EncryptedSecret secret);

    /// <summary>True when this record can still be decrypted with a configured key.</summary>
    bool CanDecrypt(EncryptedSecret secret);
}

/* ------------------------------------------------------------------ *
 * Repositories
 * ------------------------------------------------------------------ */

public interface ISocialConnectionRepository
{
    Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct);

    /// <summary>
    /// Finds an existing connection for the same external account, so
    /// re-authenticating updates rather than duplicates.
    /// </summary>
    Task<SocialConnection?> FindByExternalAsync(
        string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct);

    Task AddAsync(SocialConnection connection, CancellationToken ct);
    Task UpdateAsync(SocialConnection connection, CancellationToken ct);
    Task<int> CountActiveAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// Every connection, across all pharmacies, granted by this platform user
    /// on these platforms. Used only by the platform's own signed callbacks.
    /// </summary>
    Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(
        IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct);

    /// <summary>Removes the record outright — for a data-deletion request.</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>
    /// Whether any live connection other than <paramref name="exceptId"/>, in
    /// any pharmacy, still uses the grant with this fingerprint.
    /// </summary>
    Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct);

    /// <summary>
    /// Atomically takes one active connection on these platforms that has not
    /// been health-checked since <paramref name="checkedBeforeUtc"/>, stamping
    /// it as checked now so no other instance takes it too. Null when none.
    /// </summary>
    Task<SocialConnection?> ClaimForHealthCheckAsync(
        IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc,
        CancellationToken ct);

    /// <summary>
    /// Records a health-check result with a targeted update — never a full
    /// replace, so a publish finishing at the same moment is not overwritten —
    /// and only while the connection is still Active: a check must never bring
    /// back a connection the pharmacy or Meta revoked meanwhile.
    /// </summary>
    Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Saves a credential the platform rotated while it was being used
    /// (TikTok may return a new refresh token on every refresh, and the old
    /// one then stops working). A targeted update, never on a revoked
    /// connection, and leaving the status alone.
    /// </summary>
    Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct);
}

/// <param name="AnyOf">Any of these statuses, when set. Combined with <paramref name="Status"/> if both are given.</param>
public sealed record ScheduledPostQuery(
    string OwnerId,
    PublishStatus? Status = null,
    int Skip = 0,
    int Take = 50,
    IReadOnlyCollection<PublishStatus>? AnyOf = null);

/// <summary>The two halves of the pharmacy's post list.</summary>
public static class ScheduledPostViews
{
    /// <summary>Still to go out, or going out now.</summary>
    public static readonly IReadOnlyCollection<PublishStatus> Upcoming = new[]
    {
        PublishStatus.Draft, PublishStatus.Scheduled, PublishStatus.Publishing,
    };

    /// <summary>Finished, one way or another.</summary>
    public static readonly IReadOnlyCollection<PublishStatus> History = new[]
    {
        PublishStatus.Published, PublishStatus.PartiallyPublished,
        PublishStatus.Failed, PublishStatus.Cancelled,
    };

    public static IReadOnlyCollection<PublishStatus>? For(string? view) => view?.ToLowerInvariant() switch
    {
        "upcoming" => Upcoming,
        "history" => History,
        _ => null,
    };
}

public interface IScheduledPostRepository
{
    Task<IReadOnlyList<ScheduledPost>> ListAsync(ScheduledPostQuery query, CancellationToken ct);
    Task<long> CountAsync(ScheduledPostQuery query, CancellationToken ct);
    Task<ScheduledPost?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(ScheduledPost post, CancellationToken ct);
    Task UpdateAsync(ScheduledPost post, CancellationToken ct);

    /// <summary>
    /// Saves a post the worker has claimed, only if this worker still holds it.
    /// </summary>
    /// <remarks>
    /// False means the lease was lost — it lapsed and another instance took
    /// over, or the pharmacy changed the post underneath. The caller must stop
    /// at once and send nothing more: the other owner is now responsible, and
    /// carrying on is how one post reaches a feed twice.
    /// </remarks>
    Task<bool> TryUpdateClaimedAsync(ScheduledPost post, string leaseHolder, CancellationToken ct);

    /// <summary>
    /// Saves a pharmacy's change (cancel, retry, reschedule) only if the post
    /// is still in the status it was read in.
    /// </summary>
    /// <remarks>
    /// The worker can claim a post between the pharmacy loading it and saving
    /// their change. An unconditional replace would then overwrite the claim
    /// while the worker is mid-publish. False means "it moved; reload".
    /// </remarks>
    Task<bool> TryUpdateIfStatusAsync(ScheduledPost post, PublishStatus expectedStatus, CancellationToken ct);

    /// <summary>
    /// Atomically claims one due post for this worker, or returns null.
    /// </summary>
    /// <remarks>
    /// <b>This is the concurrency boundary for the whole publishing feature.</b>
    /// It must be a single findAndModify that matches on "due and unleased (or
    /// lease expired)" and sets the lease in the same operation. Reading a
    /// candidate and then writing a claim is not equivalent: two instances read
    /// the same document, both write, and the pharmacy's followers see the post
    /// twice. There is no way to retract that.
    /// <para>
    /// Also reclaims a post left in Publishing whose lease has lapsed — the
    /// worker that held it crashed or was stopped. Without that, a deploy at
    /// the wrong moment strands the post for ever.
    /// </para>
    /// </remarks>
    Task<ScheduledPost?> ClaimNextDueAsync(
        string leaseHolder, TimeSpan leaseDuration, DateTime nowUtc, CancellationToken ct);

    /// <summary>Posts referencing a connection, so disconnecting can warn about them.</summary>
    Task<int> CountPendingForConnectionAsync(string connectionId, CancellationToken ct);
}

public interface IGoogleAdsCampaignRepository
{
    Task<IReadOnlyList<GoogleAdsCampaign>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task<GoogleAdsCampaign?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(GoogleAdsCampaign campaign, CancellationToken ct);
    Task UpdateAsync(GoogleAdsCampaign campaign, CancellationToken ct);

    /// <summary>Removes a record that never became a campaign (a validate-only check).</summary>
    Task DeleteAsync(string id, CancellationToken ct);
}

/* ------------------------------------------------------------------ *
 * OAuth
 * ------------------------------------------------------------------ */

/// <summary>An account the platform offered us after the user consented.</summary>
public sealed record DiscoveredAccount(
    string ExternalAccountId,
    string DisplayName,
    SocialPlatform Platform,
    string AccessToken,
    string? RefreshToken,
    DateTime? ExpiresAtUtc,
    IReadOnlyList<string> Scopes,
    string? ParentAccountId = null,
    string? AvatarUrl = null,
    string? AuthorizedByUserId = null);

/// <summary>
/// The OAuth half of a platform integration.
/// </summary>
/// <remarks>
/// Separate from <see cref="ISocialPublisher"/> because the two have different
/// shapes: one Meta OAuth flow yields both Facebook Page and Instagram
/// connections, while publishing to each is a different call. Google Ads has an
/// OAuth provider and no publisher at all.
/// </remarks>
public interface ISocialOAuthProvider
{
    /// <summary>Which platform group this provider handles.</summary>
    SocialOAuthProviderKind Kind { get; }

    /// <summary>False when the app credentials are absent, so the UI can say so.</summary>
    bool IsConfigured { get; }

    /// <summary>The URL to send the pharmacy to. <paramref name="state"/> is opaque and signed by the caller.</summary>
    string BuildAuthorizationUrl(string state, string redirectUri);

    /// <summary>
    /// Exchanges the callback code for credentials and lists the accounts the
    /// pharmacy can use.
    /// </summary>
    /// <remarks>
    /// Returns every account rather than picking one. A pharmacy group with
    /// four Pages must choose; guessing would connect the wrong one and post a
    /// prescription-services poster to the café next door.
    /// </remarks>
    Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct);

    /// <summary>
    /// <see cref="ExchangeCodeAsync(string, string, CancellationToken)"/>, for
    /// providers that need the flow's <c>state</c> to redeem the code — X's
    /// PKCE verifier is derived from it, so nothing extra has to be stored
    /// between the consent and the callback.
    /// </summary>
    Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, string state, CancellationToken ct) =>
        ExchangeCodeAsync(code, redirectUri, ct);

    /// <summary>
    /// Withdraws a grant at the platform, so the credential stops working
    /// there and not only here. Best effort: callers log and carry on.
    /// </summary>
    /// <remarks>
    /// Defaults to doing nothing, which is right for Meta: a Page token cannot
    /// be revoked on its own, and withdrawing the user's whole grant would
    /// disconnect every other Page they connected. Deleting our copy is the
    /// revocation there; the person can remove the app in Facebook.
    /// </remarks>
    Task RevokeAsync(string credential, CancellationToken ct) => Task.CompletedTask;
}

public enum SocialOAuthProviderKind
{
    Meta = 0,
    Google = 1,

    /// <summary>
    /// Google sign-in for a YouTube channel. The same Google OAuth client as
    /// Ads, but its own consent with only the YouTube scopes and its own
    /// callback page (<c>/social/callback/youtube</c>).
    /// </summary>
    YouTube = 2,

    /// <summary>
    /// LinkedIn sign-in. One consent yields the person's own profile and —
    /// when the server has Community Management access — the company Pages
    /// they administer. Callback page <c>/social/callback/linkedin</c>.
    /// </summary>
    LinkedIn = 3,

    /// <summary>TikTok Login Kit (web). Callback page <c>/social/callback/tiktok</c>.</summary>
    TikTok = 4,

    /// <summary>X (formerly Twitter), OAuth 2.0 with PKCE. Callback page <c>/social/callback/x</c>.</summary>
    X = 5,
}

/* ------------------------------------------------------------------ *
 * Publishing
 * ------------------------------------------------------------------ */

/// <summary>Everything an adapter needs to post, with credentials already decrypted.</summary>
/// <remarks>
/// The plaintext token appears here and nowhere else in the application layer.
/// It is constructed inside <c>PublishService</c> immediately before the call
/// and is not persisted, logged or returned.
/// </remarks>
public sealed record PublishRequest(
    SocialPlatform Platform,
    string ExternalAccountId,
    string AccessToken,
    string Caption,

    /// <summary>
    /// A publicly reachable URL for the media.
    /// </summary>
    /// <remarks>
    /// Every one of these platforms fetches media by URL rather than accepting
    /// an upload, so the link must be reachable from the public internet for
    /// the lifetime of the call. A localhost URL fails with a message about the
    /// image being invalid, which sends you looking at the image.
    /// </remarks>
    string MediaUrl,

    bool IsVideo,
    string? ParentAccountId = null,
    GbpPostOptions? GbpOptions = null,
    TikTokPostOptions? TikTokOptions = null,

    /// <summary>
    /// Saves a credential the platform rotated, the moment it is rotated.
    /// </summary>
    /// <remarks>
    /// Not left to the end of the publish: a TikTok upload can take minutes,
    /// and another worker posting to the same account meanwhile would refresh
    /// with the old refresh token — which TikTok may already have retired —
    /// and mark a healthy connection for reconnecting.
    /// </remarks>
    Func<RenewedCredential, Task>? SaveRenewal = null)
{
    /// <summary>Never print the token.</summary>
    public override string ToString() => $"PublishRequest {{ Platform = {Platform}, ExternalAccountId = {ExternalAccountId} }}";
}

/// <summary>What the platform said.</summary>
public sealed record PublishOutcome(
    bool Success,
    string? ExternalPostId,
    string? Permalink,
    string? Error,

    /// <summary>
    /// True when the failure is our credentials rather than our content.
    /// </summary>
    /// <remarks>
    /// Drives whether the connection is flagged for re-authentication. Retrying
    /// a token failure on a backoff achieves nothing but rate-limit pressure —
    /// only a human re-consenting fixes it.
    /// </remarks>
    bool IsAuthFailure = false,

    /// <summary>True for transient failures worth another attempt.</summary>
    /// <remarks>
    /// Only for failures where the platform certainly did <i>not</i> publish:
    /// a rate limit, a refused connection, a container that never finished.
    /// </remarks>
    bool IsRetryable = false,

    /// <summary>
    /// True when the platform may have published despite the failure.
    /// </summary>
    /// <remarks>
    /// A timeout or dropped connection on the call that actually publishes, a
    /// 5xx from it, or a success with no id. Never retried automatically: a
    /// second copy on a pharmacy's feed cannot be taken back. Takes precedence
    /// over <see cref="IsRetryable"/>.
    /// </remarks>
    bool OutcomeUnknown = false,

    /// <summary>
    /// A replacement for the stored credential, when the platform rotated it
    /// during this call. Saved whatever the outcome — the old one may no
    /// longer work. Null for platforms that never rotate.
    /// </summary>
    RenewedCredential? RenewedCredential = null);

/// <summary>A credential the platform replaced, and when the new one lapses.</summary>
public sealed record RenewedCredential(string Credential, DateTime? ExpiresAtUtc)
{
    /// <summary>Never print the credential.</summary>
    public override string ToString() => $"RenewedCredential {{ ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

/// <summary>What a connection health check found.</summary>
public enum ProbeResult
{
    /// <summary>The platform accepted the credential and the account is reachable.</summary>
    Healthy,

    /// <summary>The credential is no longer accepted. Only reconnecting fixes it.</summary>
    Unauthorized,

    /// <summary>Could not tell — the platform was down, or rate limited us. Try again tomorrow.</summary>
    Inconclusive,
}

/// <summary>
/// Checks that a stored connection still works, without publishing anything.
/// </summary>
public interface ISocialConnectionProbe
{
    SocialPlatform Platform { get; }
    bool IsConfigured { get; }

    Task<(ProbeResult Result, string? Detail)> ProbeAsync(
        string externalAccountId, string accessToken, CancellationToken ct);
}

/// <summary>
/// A health check that may have to rotate the credential to run — TikTok's
/// access token lasts a day, so the daily check always refreshes it.
/// </summary>
public interface IRenewingConnectionProbe : ISocialConnectionProbe
{
    Task<(ProbeResult Result, string? Detail, RenewedCredential? Renewed)> ProbeAndRenewAsync(
        string externalAccountId, string credential, CancellationToken ct);
}

/// <summary>One platform's publishing implementation.</summary>
public interface ISocialPublisher
{
    SocialPlatform Platform { get; }

    /// <summary>False when the platform's app credentials are not configured.</summary>
    bool IsConfigured { get; }

    Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct);
}

/* ------------------------------------------------------------------ *
 * Google Ads
 * ------------------------------------------------------------------ */

/// <param name="LoginCustomerId">
/// The manager account this one is reached through, when the signed-in Google
/// user has access via a manager (MCC) rather than directly. Every later call
/// for this account sends it as <c>login-customer-id</c>. Null for direct access.
/// </param>
public sealed record AdsAccountSummary(
    string CustomerId, string DescriptiveName, string CurrencyCode, string? LoginCustomerId = null);

/// <param name="ValidatedOnly">
/// The server is in validate-only mode: Google checked the request and
/// created nothing. Not a failure, and not a campaign.
/// </param>
public sealed record AdsCreateResult(
    bool Success,
    string? ExternalCampaignId,
    string? ExternalBudgetId,
    string? ExternalAssetId,
    string? Error,
    bool ValidatedOnly = false);

/// <summary>
/// The images a Performance Max campaign is built from, already in the shapes
/// Google requires: a 1.91:1 landscape and a 1:1 square of the poster, and a
/// 1:1 logo. JPEG bytes.
/// </summary>
public sealed record AdsCreative(byte[] LandscapeImage, byte[] SquareImage, byte[] Logo);

/// <summary>Turns a poster and the pharmacy's brand kit into <see cref="AdsCreative"/>.</summary>
public interface IAdsCreativeBuilder
{
    /// <returns>The images, or an Invalid result naming what is missing (usually the logo).</returns>
    Task<Acme.Application.Common.Result<AdsCreative>> BuildAsync(
        string ownerId, string posterId, CancellationToken ct);
}

/// <summary>How to reach one Google Ads account on behalf of a pharmacy.</summary>
/// <param name="RefreshToken">The pharmacy's grant, decrypted for this call only.</param>
/// <param name="CustomerId">The Ads account, digits only.</param>
/// <param name="LoginCustomerId">The manager it is reached through, if any.</param>
public sealed record AdsAccess(string RefreshToken, string CustomerId, string? LoginCustomerId)
{
    /// <summary>Never print the token.</summary>
    public override string ToString() => $"AdsAccess {{ CustomerId = {CustomerId}, LoginCustomerId = {LoginCustomerId} }}";
}

/// <summary>The Google Ads API, narrowed to what this product does.</summary>
public interface IGoogleAdsClient
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<AdsAccountSummary>> ListAccessibleCustomersAsync(
        string refreshToken, CancellationToken ct);

    /// <summary>
    /// Creates the campaign at Google, paused, in one all-or-nothing request.
    /// </summary>
    /// <param name="creative">Required for Performance Max; unused by Search.</param>
    Task<AdsCreateResult> CreateCampaignAsync(
        GoogleAdsCampaign campaign, AdsAccess access, AdsCreative? creative, CancellationToken ct);

    /// <summary>The account's own currency, which every budget amount is in.</summary>
    Task<string?> GetAccountCurrencyAsync(AdsAccess access, CancellationToken ct);

    Task<bool> SetCampaignStatusAsync(
        string campaignId, bool enabled, AdsAccess access, CancellationToken ct);

    /// <summary>Lifetime metrics, or null when Google returned nothing yet.</summary>
    Task<AdsMetrics?> FetchMetricsAsync(string campaignId, AdsAccess access, CancellationToken ct);
}

/* ------------------------------------------------------------------ *
 * Media resolution
 * ------------------------------------------------------------------ */

public sealed record ResolvedMedia(
    string Url,
    bool IsVideo,
    string Name,
    /// <summary>
    /// A poster's Instagram-ready JPEG, when one was asked for and could be
    /// made. Null for videos, which go to Instagram as they are.
    /// </summary>
    string? InstagramUrl = null)
{
    /// <summary>
    /// The URL to hand a given platform, or null when this media has no form
    /// that platform will accept.
    /// </summary>
    /// <remarks>
    /// Null rather than falling back to the PNG: Instagram is certain to
    /// refuse it, with an error about the image that sends someone looking at
    /// the poster instead of at us.
    /// <para>
    /// X gets the same JPEG when there is one — X refuses images over 5 MB,
    /// which a full-size poster PNG can be — and the PNG otherwise (a small
    /// one goes through; a large one is refused by the X publisher with a
    /// clear message).
    /// </para>
    /// </remarks>
    public string? UrlFor(SocialPlatform platform) =>
        IsVideo ? Url :
        platform == SocialPlatform.InstagramBusiness ? InstagramUrl :
        platform == SocialPlatform.XAccount ? InstagramUrl ?? Url :
        Url;
}

/// <summary>
/// Turns a poster or video id into a URL the platforms can fetch.
/// </summary>
/// <remarks>
/// Its own abstraction because the two sources resolve differently — a poster
/// has to be rendered to a raster image, while a video already has a blob — and
/// because the publisher should not care which it was given.
/// </remarks>
public interface IPublishMediaResolver
{
    /// <param name="forInstagram">
    /// Also prepare a poster's Instagram variant. Off unless an Instagram
    /// target is waiting, so a Facebook-only publish does not pay for it.
    /// </param>
    Task<ResolvedMedia?> ResolveAsync(
        PublishSourceKind kind, string sourceId, string ownerId, bool forInstagram, CancellationToken ct);
}
