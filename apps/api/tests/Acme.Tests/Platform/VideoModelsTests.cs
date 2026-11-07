using Acme.Domain.Common;
using Acme.Domain.Platform;
using Xunit;

namespace Acme.Tests.Platform;

/// <summary>
/// The allow-list that stands between a typo and every video failing.
/// </summary>
/// <remarks>
/// This value is passed straight to the Anthropic API by the Python worker. A
/// bad one does not fail here — it fails later, on every video, for every
/// pharmacy, with an error nobody connects back to a settings change made days
/// earlier. So the guard gets tested properly.
/// </remarks>
public class VideoModelsTests
{
    [Fact]
    public void The_configured_default_is_itself_allowed()
    {
        // If this ever fails, resetting to the built-in default would produce a
        // value the service then refuses — a state with no way out through the UI.
        Assert.True(VideoModels.IsAllowed(VideoModels.Fallback));
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-opus-5")]
    public void Known_models_are_allowed(string model)
    {
        Assert.True(VideoModels.IsAllowed(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gpt-4o")]
    [InlineData("claude-haiku")]                    // real family, wrong id
    [InlineData("claude-haiku-4-5")]                // missing the date suffix
    [InlineData("claude-sonnet-5; DROP TABLE")]     // anything unexpected
    public void Anything_else_is_refused(string? model)
    {
        Assert.False(VideoModels.IsAllowed(model));
    }

    [Fact]
    public void Whitespace_around_a_valid_id_is_tolerated()
    {
        // A value pasted from a docs page usually arrives with a trailing space.
        Assert.True(VideoModels.IsAllowed("  claude-sonnet-5  "));
    }

    [Fact]
    public void Matching_is_case_sensitive()
    {
        // Anthropic model ids are lowercase and the API does not normalise.
        // Accepting "Claude-Sonnet-5" here would store a value that fails later.
        Assert.False(VideoModels.IsAllowed("Claude-Sonnet-5"));
    }

    [Fact]
    public void Every_option_is_distinct_and_described()
    {
        var ids = VideoModels.Allowed.Select(m => m.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(VideoModels.Allowed, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(m.Description));
            Assert.True(m.RelativeCost > 0);
        });
    }

    [Fact]
    public void The_cheapest_option_is_the_default()
    {
        // A default that silently costs 5x would be a poor choice for a fresh
        // install, and nobody would notice until the bill.
        var cheapest = VideoModels.Allowed.OrderBy(m => m.RelativeCost).First();
        Assert.Equal(VideoModels.Fallback, cheapest.Id);
    }

    [Fact]
    public void Find_returns_null_rather_than_throwing_on_an_unknown_id()
    {
        Assert.Null(VideoModels.Find("nope"));
        Assert.NotNull(VideoModels.Find("claude-sonnet-5"));
    }
}

public class PlatformSettingTests
{
    [Fact]
    public void The_key_is_also_the_id()
    {
        // The Mongo document uses the key as _id, which is what makes duplicate
        // rows for one setting impossible.
        var setting = PlatformSetting.Create("video.anthropicModel", "claude-sonnet-5", "boss@x.test");

        Assert.Equal("video.anthropicModel", setting.Id);
        Assert.Equal(setting.Key, setting.Id);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var setting = PlatformSetting.Create("k", "  claude-sonnet-5  ", "boss@x.test");
        Assert.Equal("claude-sonnet-5", setting.Value);
    }

    [Fact]
    public void Updating_records_who_did_it()
    {
        var setting = PlatformSetting.Create("k", "a", "first@x.test");
        var before = setting.UpdatedAtUtc;

        setting.Update("b", "second@x.test");

        Assert.Equal("b", setting.Value);
        Assert.Equal("second@x.test", setting.UpdatedByEmail);
        Assert.True(setting.UpdatedAtUtc >= before);
    }

    [Fact]
    public void A_key_is_required()
    {
        Assert.Throws<DomainException>(() => PlatformSetting.Create("", "v", "e@x.test"));
    }
}
