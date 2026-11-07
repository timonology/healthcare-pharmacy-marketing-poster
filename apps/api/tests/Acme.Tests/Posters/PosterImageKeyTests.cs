using Acme.Application.Posters;
using Xunit;

namespace Acme.Tests.Posters;

/// <summary>
/// The blob key for a rendered poster.
/// </summary>
/// <remarks>
/// The key carries a hash of what was rendered, and that is what makes the
/// cache <i>correct</i> rather than merely fast. If two different renders could
/// share a key, an edited poster would keep publishing its old artwork — to a
/// pharmacy's followers, with no way to tell from our side that anything was
/// wrong.
/// </remarks>
public class PosterImageKeyTests
{
    private const string Owner = "owner-1";
    private const string Poster = "poster-1";
    private const string Canvas = """{"width":1240,"height":1754,"shapes":[]}""";

    private static string Key(string canvas = Canvas, bool watermark = false,
                              string owner = Owner, string poster = Poster) =>
        PosterImageService.BuildKey(owner, poster, canvas, watermark);

    [Fact]
    public void The_same_canvas_always_produces_the_same_key()
    {
        // Without this the cache never hits and every publish re-renders.
        Assert.Equal(Key(), Key());
    }

    [Fact]
    public void Editing_the_canvas_changes_the_key()
    {
        // The whole point. A stale image must become unreachable the moment the
        // poster changes.
        var edited = """{"width":1240,"height":1754,"shapes":[{"kind":"text"}]}""";

        Assert.NotEqual(Key(), Key(canvas: edited));
    }

    [Fact]
    public void The_watermark_flag_changes_the_key()
    {
        // Same canvas renders differently on Free and on Starter. Leaving the
        // flag out of the hash would leave an upgrading pharmacy serving the
        // watermarked image they just paid to remove.
        Assert.NotEqual(Key(watermark: false), Key(watermark: true));
    }

    [Fact]
    public void Different_posters_never_collide()
    {
        Assert.NotEqual(Key(poster: "poster-1"), Key(poster: "poster-2"));
    }

    [Fact]
    public void Two_pharmacies_with_identical_canvases_get_separate_keys()
    {
        // Both cloned the same template and changed nothing. They must not
        // share a blob — one deleting their poster would take out the other's.
        Assert.NotEqual(Key(owner: "a"), Key(owner: "b"));
    }

    [Fact]
    public void The_key_is_scoped_to_the_owner_and_ends_in_png()
    {
        var key = Key();

        Assert.StartsWith($"posters/{Owner}/", key);
        Assert.EndsWith(".png", key);
        Assert.Contains(Poster, key);
    }

    [Fact]
    public void The_key_contains_no_path_traversal_or_whitespace()
    {
        // The key becomes part of a URL and a blob path. A canvas is
        // user-controlled input, so nothing derived from it may escape the
        // prefix — the hash is hex, which is what guarantees that.
        var hostile = """{"name":"../../etc/passwd","shapes":[]}""";
        var key = Key(canvas: hostile);

        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain(" ", key);
        Assert.Equal(3, key.Split('/').Length);
    }

    [Fact]
    public void The_hash_is_lowercase_hex_of_a_fixed_length()
    {
        var hash = Key().Split('-').Last().Replace(".png", string.Empty);

        Assert.Equal(16, hash.Length);
        Assert.All(hash, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"'{c}' is not lowercase hex"));
    }

    [Fact]
    public void Whitespace_differences_in_the_canvas_are_treated_as_changes()
    {
        // Conservative on purpose. Re-rendering an unchanged poster costs a
        // little CPU; serving a stale one costs credibility.
        var spaced = """{"width":1240, "height":1754, "shapes":[]}""";

        Assert.NotEqual(Key(), Key(canvas: spaced));
    }
}
