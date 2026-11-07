using Acme.Infrastructure.Storage;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Storage;

/// <summary>
/// URL → blob key resolution for template images.
/// </summary>
/// <remarks>
/// These exist because the relative-path case shipped broken and stayed broken
/// through several rounds of debugging. It had been "verified" against a Python
/// model of the logic, which used urlparse — that reports an empty scheme for a
/// leading-slash path, whereas .NET on Unix parses the same string as an
/// absolute file:// URI. The model agreed with the intent and disagreed with
/// the runtime, so the tests passed while every template image silently failed
/// to load.
///
/// The lesson is encoded here: this logic is tested in the language that runs
/// it, on the platform that runs it.
/// </remarks>
public class TemplateAssetUrlsTests
{
    private const string Guid32 = "40b9e46c3ca54493928efa16663b40b8";

    [Fact]
    public void Resolves_the_relative_form_that_canvases_actually_store()
    {
        // The exact value from the failing poster.
        TemplateAssetUrls.TryResolveBlobKey($"/api/assets/templates/{Guid32}.jpg")
            .Should().Be($"templates/assets/{Guid32}.jpg");
    }

    [Fact]
    public void Relative_path_is_not_mistaken_for_a_file_uri()
    {
        // Guards the regression directly: on Unix a leading slash parses as an
        // absolute file:// URI, which previously tripped the non-http guard.
        var input = $"/api/assets/templates/{Guid32}.jpg";

        Uri.TryCreate(input, UriKind.Absolute, out var parsed)
            .Should().BeTrue("a leading-slash path parses as absolute on Unix");
        parsed!.Scheme.Should().Be(Uri.UriSchemeFile);

        TemplateAssetUrls.TryResolveBlobKey(input).Should().NotBeNull(
            "resolution must not depend on how Uri.TryCreate treats a bare path");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    public void Accepts_every_stored_extension(string ext)
    {
        TemplateAssetUrls.TryResolveBlobKey($"/api/assets/templates/{Guid32}.{ext}")
            .Should().Be($"templates/assets/{Guid32}.{ext}");
    }

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("http://localhost:9090")]
    public void Accepts_absolute_urls_from_other_hosts(string origin)
    {
        // A canvas may have been authored against a different origin.
        TemplateAssetUrls.TryResolveBlobKey($"{origin}/api/assets/templates/{Guid32}.jpg")
            .Should().Be($"templates/assets/{Guid32}.jpg");
    }

    [Theory]
    [InlineData("https://images.unsplash.com/photo-1559757175-5700dde675bc")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    public void Ignores_values_it_does_not_own(string input)
    {
        TemplateAssetUrls.TryResolveBlobKey(input).Should().BeNull();
    }

    [Theory]
    [InlineData("../../secrets.json")]
    [InlineData("..%2f..%2fsecrets")]
    [InlineData("sub/dir/" + Guid32 + ".jpg")]
    [InlineData(Guid32 + ".svg")]                    // script-bearing type
    [InlineData(Guid32)]                             // no extension
    [InlineData("0123456789abcdef.jpg")]             // stem too short
    [InlineData(Guid32 + "aa.jpg")]                  // stem too long
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.jpg")] // not hex
    public void Rejects_anything_that_is_not_a_minted_name(string fileName)
    {
        TemplateAssetUrls.TryResolveBlobKey($"/api/assets/templates/{fileName}")
            .Should().BeNull();
    }

    [Fact]
    public void Round_trips_a_minted_url()
    {
        var url = TemplateAssetUrls.UrlFor($"{Guid32}.jpg");
        TemplateAssetUrls.TryResolveBlobKey(url)
            .Should().Be($"{TemplateAssetUrls.BlobPrefix}/{Guid32}.jpg");
    }
}
