namespace Acme.Infrastructure.Social;

/// <summary>Named HttpClients for the platforms, registered without request logging.</summary>
public static class SocialHttpClients
{
    public const string Meta = "meta";
    public const string MetaOAuth = "meta-oauth";
    public const string Google = "google";
    public const string GoogleAds = "google-ads";

    /// <summary>YouTube uploads: a long timeout, because the file itself is sent.</summary>
    public const string YouTube = "youtube";

    /// <summary>LinkedIn sign-in, lookups and post creation: short calls.</summary>
    public const string LinkedIn = "linkedin";

    /// <summary>LinkedIn image and video uploads: a long timeout, because the file itself is sent.</summary>
    public const string LinkedInUpload = "linkedin-upload";

    /// <summary>TikTok sign-in, creator info, post init and status: short calls.</summary>
    public const string TikTok = "tiktok";

    /// <summary>TikTok video upload: a long timeout, because the file itself is sent.</summary>
    public const string TikTokUpload = "tiktok-upload";

    /// <summary>X sign-in, media upload steps and post creation.</summary>
    public const string X = "x";

    /// <summary>Fetching our own file to send to X: a long timeout for a video.</summary>
    public const string XUpload = "x-upload";
}

/// <summary>LinkedIn app credentials (developer.linkedin.com → your app → Auth).</summary>
public sealed class LinkedInOptions
{
    public const string SectionName = "Social:LinkedIn";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Offer the signed-in person's own profile. Needs the app's "Sign In
    /// with LinkedIn using OpenID Connect" and "Share on LinkedIn" products,
    /// which are self-serve. Set false for an app that has only Community
    /// Management (LinkedIn may require that product on an app of its own,
    /// which then cannot ask for <c>openid</c>).
    /// </summary>
    public bool ProfilesEnabled { get; set; } = true;

    /// <summary>
    /// Offer company Pages. Off until LinkedIn approves the app for the
    /// Community Management API: before that, asking for the Page scopes makes
    /// LinkedIn refuse the whole consent with "unauthorized_scope_error".
    /// </summary>
    public bool PagesEnabled { get; set; }

    /// <summary>
    /// LinkedIn-Version header, YYYYMM. Pinned like the Graph version: each
    /// version is served for about a year, and 202610 (October 2026) is the
    /// newest at the time of writing. Bump deliberately and post once to a
    /// test profile afterwards.
    /// </summary>
    public string ApiVersion { get; set; } = "202610";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
        && (ProfilesEnabled || PagesEnabled);
}

/// <summary>Meta (Facebook / Instagram) app credentials.</summary>
public sealed class MetaOptions
{
    public const string SectionName = "Social:Meta";

    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// Graph API version, pinned deliberately.
    /// </summary>
    /// <remarks>
    /// Meta deprecates versions on a schedule and changes behaviour between
    /// them. Pinning means an upgrade is a decision someone makes and tests,
    /// rather than something that happens to production on a Tuesday.
    /// <para>
    /// v26.0 (July 2026). v21.0 stops being served on 21 January 2027, after
    /// which Meta quietly runs calls on the oldest live version instead — an
    /// untested upgrade by default. None of the calls used here (Page
    /// photos/videos, Instagram media and media_publish, the OAuth token
    /// exchange, me/accounts) appear in the deprecations between the two, but
    /// that is a reading of the changelog, not a test: publish once to a test
    /// Page and Instagram account after deploying a version change.
    /// </para>
    /// </remarks>
    public string GraphVersion { get; set; } = "v26.0";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret);
}

/// <summary>Google OAuth client, shared by Business Profile and Ads.</summary>
public sealed class GoogleOptions
{
    public const string SectionName = "Social:Google";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Legacy. Google sunset developer tokens on 10 September 2026: API access
    /// now belongs to the Google Cloud project behind the OAuth client (its
    /// access level is on the project's Google Ads API page), and a token sent
    /// in a call is ignored. Sent only if set, and best left empty — Google has
    /// said a future API version will reject calls that carry one.
    /// </summary>
    public string AdsDeveloperToken { get; set; } = string.Empty;

    /// <summary>Offer Google Ads. Set false to hide it while keeping the OAuth client.</summary>
    public bool AdsEnabled { get; set; } = true;

    /// <summary>
    /// Not used. The manager (MCC) an account is reached through is discovered
    /// per account when it is connected, and stored on the connection — one
    /// server-wide value would be wrong for every pharmacy not under it. Kept
    /// so existing configuration still binds.
    /// </summary>
    public string? AdsLoginCustomerId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// The OAuth client is all this server needs. Whether Google lets the
    /// calls reach real ad accounts depends on the Cloud project's access
    /// level, which only Google's answer can tell (CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION).
    /// </summary>
    public bool IsAdsConfigured => IsConfigured && AdsEnabled;

    /// <summary>
    /// Offer YouTube. On by default whenever the Google OAuth client is set,
    /// since it needs nothing else from us — but the Google Cloud project must
    /// have the YouTube Data API v3 enabled and the YouTube callback URL
    /// registered (SOCIAL_SETUP.md). Set false to hide it.
    /// </summary>
    public bool YouTubeEnabled { get; set; } = true;

    /// <summary>
    /// public, unlisted or private. Projects Google has not audited for the
    /// YouTube API have every upload forced to private regardless.
    /// </summary>
    public string YouTubePrivacy { get; set; } = "public";

    /// <summary>YouTube category. 22 is People &amp; Blogs; 26 Howto &amp; Style.</summary>
    public string YouTubeCategoryId { get; set; } = "22";

    public bool IsYouTubeConfigured => IsConfigured && YouTubeEnabled;

    /// <summary>
    /// Google Ads API version, pinned like the Graph version. v25 (July 2026)
    /// is supported until August 2027; Google releases a major version about
    /// every quarter and each lives about a year. Bump deliberately, after
    /// running the test-account checklist in SOCIAL_SETUP.md.
    /// </summary>
    public string AdsApiVersion { get; set; } = "v25";

    /// <summary>
    /// Send every campaign creation with <c>validateOnly</c>: Google checks the
    /// whole request and creates no campaign. For the first runs against a new
    /// account, and for a staging server that must never spend. (Performance
    /// Max still creates its headline and description text assets for real —
    /// Google needs them to exist before it can check the rest — but unlinked
    /// text shows nowhere and costs nothing.)
    /// </summary>
    public bool AdsValidateOnly { get; set; }
}

/// <summary>Publish worker tuning.</summary>
public sealed class PublishWorkerOptions
{
    public const string SectionName = "Social:Worker";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between polls when the queue was empty.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds is a deliberate compromise. Scheduled posts are chosen
    /// to the minute, so this is well inside the precision anyone expects, and
    /// it keeps the indexed findAndModify to four queries a minute per
    /// instance. "Publish now" does not wait for it — it is still a poll away,
    /// which is why the UI says "queued" rather than pretending it is instant.
    /// </remarks>
    public int PollSeconds { get; set; } = 15;

    /// <summary>
    /// How long a claim is held before another worker may take the post.
    /// </summary>
    /// <remarks>
    /// Must comfortably exceed the slowest realistic publish. Instagram is a
    /// three-call sequence — create container, poll until ready, publish — and
    /// a large video can keep that going for a minute or more. Too short and a
    /// second worker starts publishing a post the first is still working on,
    /// which is the exact duplicate this whole mechanism exists to prevent.
    /// </remarks>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>
    /// Daily check that each connected account still works, so a revoked
    /// token shows as "Reconnect" the day it happens rather than at the next
    /// publish. Follows <see cref="Enabled"/>: off when the worker is off.
    /// </summary>
    public bool HealthChecks { get; set; } = true;
}

/// <summary>Tidies a credential pasted into configuration.</summary>
public static class SocialCredentials
{
    /// <summary>Trims whitespace (including newlines) and one pair of surrounding quotes.</summary>
    public static string Clean(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            v = v[1..^1].Trim();
        return v;
    }

    /// <summary>
    /// <see cref="Clean"/>, plus an <c>http://</c> or <c>https://</c> in front
    /// of a Google client id — an easy slip, since the id looks like a host
    /// name. Google answers it with "The OAuth client was not found".
    /// </summary>
    public static string CleanGoogleClientId(string? value)
    {
        var v = Clean(value);
        if (!v.TrimEnd('/').EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            return v;   // not a client id at all: left for the startup report to name
        v = v.TrimEnd('/');
        foreach (var scheme in new[] { "https://", "http://" })
            if (v.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return v[scheme.Length..];
        return v;
    }
}
