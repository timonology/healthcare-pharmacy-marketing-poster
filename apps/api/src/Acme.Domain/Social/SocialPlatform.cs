namespace Acme.Domain.Social;

/// <summary>
/// An external account a pharmacy can connect.
/// </summary>
/// <remarks>
/// Explicit numeric values because these are persisted. Never renumber: a
/// stored connection carries the number, and shifting them would silently
/// repoint every pharmacy's Facebook credentials at a different platform.
/// </remarks>
public enum SocialPlatform
{
    FacebookPage = 0,

    /// <summary>
    /// An Instagram Business or Creator account, which Meta only exposes
    /// through the Facebook Page it is linked to. There is no standalone
    /// Instagram connection — the token is the Page token.
    /// </summary>
    InstagramBusiness = 1,

    GoogleBusinessProfile = 2,

    /// <summary>
    /// Not a publishing target. Kept in the same enum because it is the same
    /// OAuth-and-tokens machinery, and a pharmacy thinks of it as one more
    /// account they connected.
    /// </summary>
    GoogleAds = 3,

    /// <summary>
    /// A YouTube channel. Video only: YouTube has no still-image post, so a
    /// poster is never offered here. Our 9:16 short videos go out as Shorts.
    /// </summary>
    /// <remarks>
    /// Connected through Google sign-in, but as its own connection with only
    /// the YouTube scopes — a pharmacy that runs Google Ads is not asked to
    /// give us their channel, and the reverse.
    /// </remarks>
    YouTubeChannel = 4,

    /// <summary>
    /// A person's own LinkedIn profile ("Share on LinkedIn", self-serve).
    /// Usable as soon as the app is created — no LinkedIn review.
    /// </summary>
    LinkedInProfile = 5,

    /// <summary>
    /// A LinkedIn company Page the signed-in person administers. Needs
    /// LinkedIn's Community Management API approval, so it is only offered
    /// when the server is configured for it (Social:LinkedIn:PagesEnabled).
    /// </summary>
    LinkedInPage = 6,

    /// <summary>
    /// A TikTok account, through the Content Posting API (Direct Post). Video
    /// only. Every post carries the privacy level and interaction settings
    /// the pharmacy chose for it (<see cref="TikTokPostOptions"/>), as
    /// TikTok's sharing guidelines require — which is also why TikTok is not
    /// offered for recurring posts.
    /// </summary>
    TikTokAccount = 7,

    /// <summary>
    /// An X (formerly Twitter) account. Posters and videos. A post's text is
    /// held to X's standard 280-character limit (<see cref="XText"/>),
    /// counted the way X counts it.
    /// </summary>
    XAccount = 8,
}

/// <summary>
/// Why a connection cannot currently be used.
/// </summary>
public enum ConnectionStatus
{
    Active = 0,

    /// <summary>
    /// The platform rejected our credentials. Recoverable by the pharmacy
    /// re-running the OAuth flow; nothing is deleted in the meantime, so
    /// scheduled posts survive a re-authentication.
    /// </summary>
    NeedsReauth = 1,

    /// <summary>Disconnected by the pharmacy. Kept for history, never used.</summary>
    Revoked = 2,
}

public static class SocialPlatformExtensions
{
    /// <summary>True for platforms a poster or video can actually be posted to.</summary>
    public static bool IsPublishTarget(this SocialPlatform platform) =>
        platform is SocialPlatform.FacebookPage
                 or SocialPlatform.InstagramBusiness
                 or SocialPlatform.GoogleBusinessProfile
                 or SocialPlatform.YouTubeChannel
                 or SocialPlatform.LinkedInProfile
                 or SocialPlatform.LinkedInPage
                 or SocialPlatform.TikTokAccount
                 or SocialPlatform.XAccount;

    /// <summary>True for either kind of LinkedIn connection.</summary>
    public static bool IsLinkedIn(this SocialPlatform platform) =>
        platform is SocialPlatform.LinkedInProfile or SocialPlatform.LinkedInPage;

    /// <summary>
    /// True when the platform can post a video rather than only a still.
    /// </summary>
    /// <remarks>
    /// Google Business Profile accepts photos on a post but not video uploads
    /// through the API, so offering "publish this video to GBP" would produce
    /// a rejection the pharmacy cannot act on. Better to grey it out.
    /// </remarks>
    public static bool SupportsVideo(this SocialPlatform platform) =>
        platform is SocialPlatform.FacebookPage
                 or SocialPlatform.InstagramBusiness
                 or SocialPlatform.YouTubeChannel
                 or SocialPlatform.LinkedInProfile
                 or SocialPlatform.LinkedInPage
                 or SocialPlatform.TikTokAccount
                 or SocialPlatform.XAccount;

    /// <summary>
    /// True when the platform can post a still image (a poster).
    /// </summary>
    /// <remarks>
    /// YouTube and TikTok cannot here: video uploads only (TikTok's photo
    /// posts need media served from a domain verified with TikTok). Refused when composing,
    /// for the same reason GBP-with-a-video is: a rejection at 8am on a
    /// Saturday is worse than being told now.
    /// </remarks>
    public static bool SupportsImages(this SocialPlatform platform) =>
        platform is not (SocialPlatform.YouTubeChannel or SocialPlatform.TikTokAccount);

    public static string DisplayName(this SocialPlatform platform) => platform switch
    {
        SocialPlatform.FacebookPage => "Facebook Page",
        SocialPlatform.InstagramBusiness => "Instagram",
        SocialPlatform.GoogleBusinessProfile => "Google Business Profile",
        SocialPlatform.GoogleAds => "Google Ads",
        SocialPlatform.YouTubeChannel => "YouTube",
        SocialPlatform.LinkedInProfile => "LinkedIn profile",
        SocialPlatform.LinkedInPage => "LinkedIn Page",
        SocialPlatform.TikTokAccount => "TikTok",
        SocialPlatform.XAccount => "X",
        _ => platform.ToString(),
    };
}
