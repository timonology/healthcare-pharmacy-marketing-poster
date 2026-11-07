using System.Text.Json;
using Acme.WebApi.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// "Google Ads is not available on this server yet" must be diagnosable: the
/// config report names the missing setting and any value under a near-miss
/// name — and never prints a value.
/// </summary>
public class SocialDiagnosticsTests
{
    private static JsonElement Social(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var controller = new DiagnosticsController(config, new DevEnvironment());
        var ok = controller.GetConfig().Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(ok.Value).GetProperty("social");
    }

    [Fact]
    public void Google_ads_needs_no_developer_token_any_more()
    {
        // Google sunset developer tokens on 10 September 2026; access comes
        // from the OAuth client's Cloud project.
        var social = Social(new()
        {
            ["Social:Encryption:Keys:0:Key"] = "k",
            ["Social:Google:ClientId"] = "id.apps.googleusercontent.com",
            ["Social:Google:ClientSecret"] = "GOCSPX-secret",
        });

        social.GetProperty("googleAdsAvailable").GetBoolean().Should().BeTrue();
        social.GetProperty("googleAdsMissing").EnumerateArray().Should().BeEmpty();
        social.GetProperty("googleAdsDeveloperToken").GetString().Should().Be("not needed");
    }

    [Fact]
    public void A_missing_oauth_client_is_still_named()
    {
        var social = Social(new() { ["Social:Encryption:Keys:0:Key"] = "k" });

        social.GetProperty("googleAdsAvailable").GetBoolean().Should().BeFalse();
        social.GetProperty("googleAdsMissing").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Social__Google__ClientId", "Social__Google__ClientSecret");
    }

    [Fact]
    public void A_token_under_a_near_miss_name_is_pointed_out()
    {
        var social = Social(new()
        {
            ["Social:Encryption:Keys:0:Key"] = "k",
            ["Social:Google:ClientId"] = "id",
            ["Social:Google:ClientSecret"] = "secret",
            ["GoogleAds:DeveloperToken"] = "dev-token-value",
        });

        social.GetProperty("settingsUnderWrongName").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("GoogleAds__DeveloperToken");
        social.GetRawText().Should().NotContain("dev-token-value").And.NotContain("secret\"");
    }

    [Fact]
    public void All_three_and_the_key_make_google_ads_available()
    {
        var social = Social(new()
        {
            ["Social:Encryption:Keys:0:Key"] = "k",
            ["Social:Google:ClientId"] = "id",
            ["Social:Google:ClientSecret"] = "secret",
            ["Social:Google:AdsDeveloperToken"] = "token",
        });

        social.GetProperty("googleAdsAvailable").GetBoolean().Should().BeTrue();
        social.GetProperty("googleAdsDeveloperToken").GetString().Should().StartWith("set (legacy");
    }

    private sealed class DevEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Acme.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
