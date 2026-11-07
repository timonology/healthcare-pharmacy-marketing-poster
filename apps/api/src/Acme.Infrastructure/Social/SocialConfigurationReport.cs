using Acme.Application.Social;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// One line at startup saying whether Google Ads can work, and if not, which
/// setting is missing — by name, never by value.
/// </summary>
/// <remarks>
/// The pharmacy-facing message ("Google Ads is not available on this server
/// yet") cannot name a setting, and the usual causes are invisible from the
/// portal: a key under a slightly different name, a value that a later
/// deployment template replaced, or a container that was restarted rather
/// than recreated and so never read the new environment. This puts the answer
/// in the first lines of the log.
/// </remarks>
internal sealed class SocialConfigurationReport(
    IOptions<GoogleOptions> google,
    IOptions<MetaOptions> meta,
    IOptions<LinkedInOptions> linkedIn,
    IOptions<TikTokOptions> tikTok,
    IOptions<XOptions> x,
    ITokenCipher cipher,
    ILogger<SocialConfigurationReport> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var g = google.Value;
        var missing = new List<string>();
        if (!cipher.IsConfigured) missing.Add("Social__Encryption__Keys__0__Key");
        if (string.IsNullOrWhiteSpace(g.ClientId)) missing.Add("Social__Google__ClientId");
        if (string.IsNullOrWhiteSpace(g.ClientSecret)) missing.Add("Social__Google__ClientSecret");

        if (!g.AdsEnabled)
            log.LogInformation("Google Ads is switched off (Social__Google__AdsEnabled=false).");
        else if (missing.Count == 0)
            log.LogInformation(
                "Google Ads is available (API {Version}{ValidateOnly}). Real ad accounts need the Cloud project's Basic access.",
                g.AdsApiVersion, g.AdsValidateOnly ? ", validate-only" : "");
        else
            log.LogWarning(
                "Google Ads is NOT available: missing {Settings}. Pharmacies see \"Google Ads is not available on this server yet\".",
                string.Join(", ", missing));

        if (!string.IsNullOrWhiteSpace(g.AdsDeveloperToken))
            log.LogWarning(
                "Social__Google__AdsDeveloperToken is set. Google sunset developer tokens on 10 September 2026 and ignores them; " +
                "remove the setting before moving to a newer Google Ads API version, which may reject calls that carry one.");

        if (!meta.Value.IsConfigured)
            log.LogInformation("Facebook/Instagram are not configured (Social__Meta__AppId / Social__Meta__AppSecret).");

        // Client ids are public (they travel in every consent URL), so the
        // whole value is logged: comparing it with the Cloud Console's is
        // the fastest answer to Google's "OAuth client was not found".
        if (!string.IsNullOrWhiteSpace(g.ClientId))
            log.LogInformation(
                "Google OAuth client in use: {ClientId}{Shape}", g.ClientId,
                g.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : " — this does not look like a Google client id (it should end .apps.googleusercontent.com)");

        if (tikTok.Value.IsConfigured)
            log.LogInformation("TikTok is available (client key {Key}).", tikTok.Value.ClientKey);
        else
            log.LogInformation("TikTok is not configured (Social__TikTok__ClientKey / Social__TikTok__ClientSecret).");

        if (x.Value.IsConfigured)
            log.LogInformation("X is available (client id {ClientId}). Posts use the developer account's pay-per-use credits.", x.Value.ClientId);
        else
            log.LogInformation("X is not configured (Social__X__ClientId / Social__X__ClientSecret).");

        var li = linkedIn.Value;
        if (!li.IsConfigured)
            log.LogInformation("LinkedIn is not configured (Social__LinkedIn__ClientId / Social__LinkedIn__ClientSecret).");
        else
            log.LogInformation(
                "LinkedIn is available: profiles {Profiles}, Pages {Pages} (API {Version}).",
                li.ProfilesEnabled ? "on" : "off", li.PagesEnabled ? "on" : "off", li.ApiVersion);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
