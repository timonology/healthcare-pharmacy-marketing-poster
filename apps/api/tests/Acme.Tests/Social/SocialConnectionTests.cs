using Acme.Domain.Social;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// Credential lifecycle on a connected account.
/// </summary>
/// <remarks>
/// The refresh-token test below guards a failure mode that is invisible until
/// it matters: Google issues a refresh token only on first consent, so
/// overwriting a stored one with null turns a working connection into one that
/// dies at the next token expiry — days later, with no obvious cause.
/// </remarks>
public class SocialConnectionTests
{
    private static EncryptedSecret Secret(string tag = "a") => new("cipher", "nonce", tag, 1);

    private static SocialConnection Connection(DateTime? expiresAt = null) =>
        SocialConnection.Create(
            "owner-1",
            SocialPlatform.GoogleAds,
            "123-456-7890",
            "Test Pharmacy Ads",
            Secret("access"),
            Secret("refresh"),
            expiresAt);

    [Fact]
    public void Reauthentication_keeps_an_existing_refresh_token_when_none_is_reissued()
    {
        var connection = Connection();

        connection.UpdateCredentials(
            Secret("new-access"),
            refreshToken: null,          // Google omitted it on the second grant
            expiresAtUtc: DateTime.UtcNow.AddHours(1));

        Assert.NotNull(connection.RefreshToken);
        Assert.Equal("refresh", connection.RefreshToken!.Tag);
        Assert.Equal("new-access", connection.AccessToken!.Tag);
    }

    [Fact]
    public void Reauthentication_replaces_the_refresh_token_when_one_is_reissued()
    {
        var connection = Connection();

        connection.UpdateCredentials(
            Secret("new-access"), Secret("new-refresh"), DateTime.UtcNow.AddHours(1));

        Assert.Equal("new-refresh", connection.RefreshToken!.Tag);
    }

    [Fact]
    public void Reauthentication_clears_a_previous_failure()
    {
        var connection = Connection();
        connection.MarkNeedsReauth("Token expired");

        connection.UpdateCredentials(Secret("new"), null, null);

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Null(connection.LastError);
    }

    [Fact]
    public void Revoking_destroys_the_credentials_but_keeps_the_record()
    {
        // Published history references this connection. Deleting the row would
        // leave a dangling id behind every post it ever sent.
        var connection = Connection();

        connection.Revoke();

        Assert.Equal(ConnectionStatus.Revoked, connection.Status);
        Assert.Null(connection.AccessToken);
        Assert.Null(connection.RefreshToken);
        Assert.False(connection.IsUsable(DateTime.UtcNow));
    }

    [Fact]
    public void A_token_expiring_within_five_minutes_is_not_usable()
    {
        // The skew is not politeness. A token that expires mid-call fails
        // halfway through Instagram's create-then-publish sequence, leaving an
        // orphaned container on Meta's side that we cannot clean up.
        var connection = Connection(DateTime.UtcNow.AddMinutes(2));

        Assert.False(connection.IsUsable(DateTime.UtcNow));
    }

    [Fact]
    public void A_token_with_plenty_of_life_is_usable()
    {
        Assert.True(Connection(DateTime.UtcNow.AddHours(2)).IsUsable(DateTime.UtcNow));
    }

    [Fact]
    public void A_token_with_no_stated_expiry_is_usable()
    {
        // Meta Page tokens derived from a long-lived user token carry no
        // expiry. Treating null as "expired" would break Facebook publishing
        // entirely.
        Assert.True(Connection(null).IsUsable(DateTime.UtcNow));
    }

    [Fact]
    public void Needing_reauth_makes_a_connection_unusable()
    {
        var connection = Connection();
        connection.MarkNeedsReauth("Session expired");

        Assert.False(connection.IsUsable(DateTime.UtcNow));
        Assert.Equal("Session expired", connection.LastError);
    }

    [Theory]
    [InlineData(SocialPlatform.FacebookPage, true, true)]
    [InlineData(SocialPlatform.InstagramBusiness, true, true)]
    [InlineData(SocialPlatform.GoogleBusinessProfile, true, false)]
    [InlineData(SocialPlatform.GoogleAds, false, false)]
    [InlineData(SocialPlatform.YouTubeChannel, true, true)]
    [InlineData(SocialPlatform.LinkedInProfile, true, true)]
    [InlineData(SocialPlatform.LinkedInPage, true, true)]
    [InlineData(SocialPlatform.TikTokAccount, true, true)]
    [InlineData(SocialPlatform.XAccount, true, true)]
    public void Platform_capabilities_are_stated_once(
        SocialPlatform platform, bool isPublishTarget, bool supportsVideo)
    {
        // These two predicates drive what the publish dialog offers. Getting
        // SupportsVideo wrong means offering Google Business Profile a video it
        // will refuse, at publish time, on a Saturday morning.
        Assert.Equal(isPublishTarget, platform.IsPublishTarget());
        Assert.Equal(supportsVideo, platform.SupportsVideo());
    }

    [Fact]
    public void Scopes_are_deduplicated_case_insensitively()
    {
        var connection = SocialConnection.Create(
            "owner-1", SocialPlatform.FacebookPage, "page-1", "Page",
            Secret(), null, null,
            new[] { "pages_manage_posts", "PAGES_MANAGE_POSTS", " pages_show_list " });

        Assert.Equal(2, connection.Scopes.Count);
        Assert.Contains("pages_show_list", connection.Scopes);
    }
}
