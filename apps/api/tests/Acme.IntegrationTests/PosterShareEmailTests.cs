using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Emailing a poster to somebody — the other path with the hardcoded sender.
/// </summary>
/// <remarks>
/// Same failure as the invitation email, same absence of any test that looked
/// at the message. Worth its own file rather than a case in the campaign tests,
/// because this endpoint behaves differently in the one way that matters here:
/// it is the only email in the product that <b>still sends a PDF
/// attachment</b>, and it sets a display name — the pharmacy's — while
/// deliberately leaving the envelope address alone.
/// </remarks>
public sealed class PosterShareEmailTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<(HttpClient Client, string PosterId)> PosterAsync(string label)
    {
        var (client, _, _) = await Api.RegisterPharmacyAsync(label);

        using var res = await client.PostAsJsonAsync(
            "/api/posters", new { name = $"Poster for the {label} share" });
        res.IsSuccessStatusCode.Should().BeTrue(
            "the test needs a poster: {0}", await res.Content.ReadAsStringAsync());

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (client, body.GetProperty("id").GetString()!);
    }

    private async Task ShareAsync(HttpClient client, string posterId, string recipient)
    {
        Api.Email.Clear();

        using var res = await client.PostAsJsonAsync(
            $"/api/posters/{posterId}/share-email",
            new { recipients = new[] { recipient }, subject = (string?)null, message = "Have a look." });

        res.IsSuccessStatusCode.Should().BeTrue(
            "sharing must succeed: {0}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sharing_a_poster_emails_the_recipient()
    {
        RequireDatabase();

        var (client, posterId) = await PosterAsync("share");
        const string Recipient = "poster-share@example.test";

        await ShareAsync(client, posterId, Recipient);

        Api.Email.Sent.Should().ContainSingle(
            m => m.To.Contains(Recipient),
            "sharing a poster by email must actually send one");
    }

    [Fact]
    public async Task The_share_email_does_not_name_its_own_sender()
    {
        // The regression. This endpoint hardcoded no-reply@pharmacyposter.app,
        // which overrode the verified sender and was refused at the provider.
        RequireDatabase();

        var (client, posterId) = await PosterAsync("share-sender");
        await ShareAsync(client, posterId, "poster-sender@example.test");

        var message = Api.Email.Last;

        message.FromAddress.Should().BeNullOrEmpty(
            "the envelope address must come from configuration, so the provider " +
            "accepts it");

        message.FromName.Should().NotBeNullOrWhiteSpace(
            "the pharmacy's name should still show in the inbox — only the envelope " +
            "address has to belong to us");
    }

    [Fact]
    public async Task The_share_email_carries_the_poster_as_a_pdf()
    {
        // The one email that is still meant to have an attachment. Campaigns
        // moved their poster into the body; this did not, and a test that
        // assumed otherwise would quietly pass while the attachment vanished.
        RequireDatabase();

        var (client, posterId) = await PosterAsync("share-pdf");
        await ShareAsync(client, posterId, "poster-pdf@example.test");

        var message = Api.Email.Last;

        message.Attachments.Should().ContainSingle("the poster travels as one attachment");

        var pdf = message.Attachments[0];
        pdf.ContentType.Should().Be("application/pdf");
        pdf.FileName.Should().EndWith(".pdf");

        pdf.Content.Should().HaveCountGreaterThan(1000, "a rendered poster is not a few bytes");
        pdf.Content.Take(5).Should().Equal(
            new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D },
            "the bytes must start with the PDF signature %PDF-, not merely be labelled as one");
    }
}
