using Acme.Application.Campaigns;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Campaigns;

/// <summary>
/// Whether a mail provider could fetch the poster from the address we publish.
/// </summary>
/// <remarks>
/// <para>
/// <b>A mail client does not fetch images from the reader's browser.</b> Gmail
/// fetches them from Google's servers through its own image proxy. So the
/// question is not "can this machine reach that URL" — it is "can the public
/// internet", and the answer decides whether the campaign links the poster or
/// carries it in the message.
/// </para>
/// <para>
/// The failure this prevents is a nasty one because it looks like flakiness.
/// With <c>Public:ApiBaseUrl</c> left at its <c>http://localhost:5050</c>
/// default, Apple Mail on the machine that sent the campaign resolves localhost
/// to itself and shows the poster perfectly, while Gmail open in a browser on
/// the same screen shows an empty space. Same message, same moment, opposite
/// result — and nothing in the logs.
/// </para>
/// <para>
/// Wrong in the cautious direction embeds a poster that could have been linked,
/// which costs bytes on a send that was going to work. Wrong the other way ships
/// an email with no design in it, to patients, and nobody finds out.
/// </para>
/// </remarks>
public class PublicUrlReachabilityTests
{
    [Theory]
    // The default when Public:ApiBaseUrl is unset. This is the case.
    [InlineData("http://localhost:5050")]
    [InlineData("http://127.0.0.1:5050")]
    [InlineData("http://[::1]:5050")]
    // Docker service names: resolvable inside the compose network, nowhere else.
    [InlineData("http://api:8080")]
    [InlineData("http://acme-api:8080")]
    // A LAN address works from the office and fails from a patient's phone,
    // which is the same trap one step further out.
    [InlineData("http://192.168.1.14:5050")]
    [InlineData("http://10.0.0.7:5050")]
    [InlineData("http://172.16.4.9:5050")]
    [InlineData("http://169.254.10.1:5050")]
    [InlineData("http://macbook-pro.local:5050")]
    [InlineData("http://api.internal:8080")]
    // Nothing usable at all.
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("ftp://files.example.com")]
    public void These_addresses_cannot_be_fetched_by_a_mail_provider(string? baseUrl)
    {
        CampaignService.IsReachableFromTheInternet(baseUrl).Should().BeFalse(
            "'{0}' would leave every recipient looking at an empty space where the "
            + "design should be", baseUrl ?? "(null)");
    }

    [Theory]
    [InlineData("https://api.marketing25.co.uk")]
    [InlineData("https://api.marketing25.co.uk/")]
    [InlineData("http://api.marketing25.co.uk")]
    [InlineData("https://marketing25-api.azurewebsites.net")]
    // A tunnel, which is how this is made to work on a development machine.
    [InlineData("https://gentle-otter-1234.trycloudflare.com")]
    // A public IP is unusual but not wrong.
    [InlineData("https://93.184.216.34")]
    public void These_can(string baseUrl)
    {
        CampaignService.IsReachableFromTheInternet(baseUrl).Should().BeTrue(
            "'{0}' is an address the public internet can resolve and fetch", baseUrl);
    }
}
