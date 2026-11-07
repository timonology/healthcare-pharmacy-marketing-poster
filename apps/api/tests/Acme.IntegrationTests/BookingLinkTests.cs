using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// One booking link, stored in one place, readable from two.
/// </summary>
/// <remarks>
/// <para>
/// <b>The link lives on the brand kit and is shown on the profile.</b> It was
/// the other way round: stored on the profile, beside the address and the phone,
/// because it is the same kind of thing — a contact detail a patient acts on.
/// That reasoning was sound and the placement was still wrong. It is what the
/// booking QR encodes and what <c>{{booking_url}}</c> resolves to, both of which
/// are laid out from the brand kit, so the value a designer needs was sitting in
/// the form an owner fills in once during onboarding.
/// </para>
/// <para>
/// <b>What these tests are really protecting.</b> The predecessor of this file
/// existed because two endpoints wrote the field and disagreed about silence:
/// onboarding treated "bookingUrl absent from the JSON" as "clear it", and
/// onboarding is reachable long after a profile exists. Skip setup, set a link,
/// finish setup, and the link was gone with nothing said. The next poster
/// printed a gap where it had been.
/// </para>
/// <para>
/// Only one endpoint writes it now, which removes that class of fault rather
/// than guarding against it. But the rule it turned on — absent means leave it,
/// empty means remove it — still applies to the endpoint that remains, and the
/// brand kit PUT is a whole-record save, so a client that omits the field must
/// not be read as asking to delete it. That is the first test below. The last
/// one is the new invariant: the profile shows the link and cannot write it.
/// </para>
/// </remarks>
public sealed class BookingLinkTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A complete brand kit, with a booking link in the payload.</summary>
    private static object KitWithBooking(string? bookingUrl) => new
    {
        name = "Riverside brand",
        colors = new { primary = "#0f766e", secondary = "#1e293b", accent = "#f59e0b" },
        pharmacy = new
        {
            name = "Riverside Pharmacy",
            licenseNumber = "FP-1234",
            phone = "0161 496 0000",
            address = "12 Riverside Walk, M1 4AB",
            bookingUrl,
        },
        regulatoryFooter = "Registered pharmacy.",
    };

    /// <summary>
    /// The same kit with the field absent from the JSON entirely.
    /// </summary>
    /// <remarks>
    /// A separate method rather than a flag: these are two different anonymous
    /// types and a ternary between them has no common type — and because
    /// "absent" and "null" are the distinction under test, building them apart
    /// makes each payload say which one it is.
    /// </remarks>
    private static object KitWithoutBooking() => new
    {
        name = "Riverside brand",
        colors = new { primary = "#0f766e", secondary = "#1e293b", accent = "#f59e0b" },
        pharmacy = new
        {
            name = "Riverside Pharmacy",
            licenseNumber = "FP-1234",
            phone = "0161 496 0000",
            address = "12 Riverside Walk, M1 4AB",
        },
        regulatoryFooter = "Registered pharmacy.",
    };

    /// <summary>
    /// Reads the link back from the profile, which is where it is displayed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/api/me/full</c>, not <c>/api/me</c>. There are two and they return
    /// different shapes: <c>/api/me</c> answers with a flat <c>UserProfile</c> —
    /// id, email, tier, role — and carries no pharmacy profile at all, while
    /// <c>/api/me/full</c> answers with <c>MeDto</c>, which nests
    /// <c>PharmacyProfileDto</c>. An earlier version of this helper asked the
    /// first for a property only the second has and got a
    /// <c>KeyNotFoundException</c> naming the missing key without saying the
    /// endpoint was wrong. "me" and "me/full" are one character apart and the
    /// failure does not point at the difference.
    /// </para>
    /// <para>
    /// Reading it from here rather than from the brand-kit response is
    /// deliberate: it checks the display path at the same time, and the display
    /// path is the one that has to keep agreeing with the store.
    /// </para>
    /// </remarks>
    private static async Task<string?> BookingUrlOf(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me/full", Json);

        if (!me.TryGetProperty("profile", out var profile))
            throw new InvalidOperationException(
                "/api/me/full did not return a 'profile' object. If MeDto has "
                + "been reshaped, this helper needs to follow it.");

        if (!profile.TryGetProperty("bookingUrl", out var value))
            throw new InvalidOperationException(
                "PharmacyProfileDto no longer exposes 'bookingUrl'. The profile "
                + "does not store it, but it does show it — removing it from "
                + "here leaves a pharmacist with no way to see what their QR "
                + "codes point at without opening the brand kit.");

        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static async Task SaveKit(HttpClient client, object payload)
    {
        using var res = await client.PutAsJsonAsync("/api/brand-kit", payload);
        res.IsSuccessStatusCode.Should().BeTrue("{0}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_the_kit_without_the_field_does_not_wipe_the_link()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-survives");

        await SaveKit(client, KitWithBooking("https://booking.riverside.co.uk/flu"));
        (await BookingUrlOf(client)).Should().Be("https://booking.riverside.co.uk/flu");

        // A client that predates the field — or any caller editing colours from a
        // stale tab — sends the rest of the kit and nothing about the link.
        await SaveKit(client, KitWithoutBooking());

        (await BookingUrlOf(client)).Should().Be(
            "https://booking.riverside.co.uk/flu",
            "saving your colours is not a request to delete your booking link");
    }

    [Fact]
    public async Task An_empty_string_is_how_a_link_is_removed()
    {
        // The other half of the rule. If absent means "leave it", there has to be
        // something that means "take it away", or a link can be set and never
        // unset.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-clears");

        await SaveKit(client, KitWithBooking("booking.riverside.co.uk"));

        (await BookingUrlOf(client)).Should().Be("https://booking.riverside.co.uk/",
            "a bare domain is what a pharmacist types, and https is added for them");

        await SaveKit(client, KitWithBooking(""));

        (await BookingUrlOf(client)).Should().BeNull();
    }

    [Fact]
    public async Task A_link_that_is_not_a_web_address_is_refused()
    {
        // This value becomes an href on a page and the payload of a QR code in a
        // waiting room. The refusal has to happen at the boundary, not in the
        // browser, because the browser is not the only thing that calls this.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-refused");

        using var res = await client.PutAsJsonAsync(
            "/api/brand-kit", KitWithBooking("javascript:alert(1)"));

        res.IsSuccessStatusCode.Should().BeFalse(
            "a javascript: URL on a poster is not a broken link, it is something "
            + "that runs on a patient's phone when they scan the code");

        (await BookingUrlOf(client)).Should().BeNull("nothing was stored");
    }

    [Fact]
    public async Task The_profile_endpoint_cannot_write_the_link()
    {
        // The invariant the move exists for. A second write path would not be
        // wrong immediately — it would write the same field — it would be wrong
        // the first time the two disagreed about anything, which is how this
        // field lost data once already.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-one-writer");

        await SaveKit(client, KitWithBooking("https://booking.riverside.co.uk/flu"));

        using var res = await client.PutAsJsonAsync("/api/me/profile", new
        {
            pharmacyName = "Riverside Pharmacy",
            address = "12 Riverside Walk",
            postCode = "M1 4AB",
            description = "",
            contactName = "A Pharmacist",
            contactPhone = "0161 496 0000",
            sonarFCode = "FA123",
            // Sent deliberately. The endpoint does not model it, so it is
            // ignored — the point is that it is ignored rather than honoured.
            bookingUrl = "https://somewhere-else.example/",
        });

        res.IsSuccessStatusCode.Should().BeTrue(
            "{0}", await res.Content.ReadAsStringAsync());

        (await BookingUrlOf(client)).Should().Be(
            "https://booking.riverside.co.uk/flu",
            "the profile shows the booking link; the brand kit owns it");
    }

    [Fact]
    public async Task Finishing_onboarding_does_not_wipe_the_booking_link()
    {
        // <b>The fault this whole move descends from.</b> When the link lived on
        // the profile, onboarding treated "bookingUrl absent from the JSON" as
        // "clear it" — and onboarding is reachable long after a profile exists,
        // because skipping is allowed and the finish-setup prompt follows the
        // pharmacy around until they go back. Skip setup, set a link, finish
        // setup, and it was gone with nothing said. The next poster printed a
        // gap; a printed QR pointed at nothing.
        //
        // It is now structurally impossible: onboarding writes the user document
        // and never touches brand_kits, and OnboardRequest does not model a
        // booking link at all. This test exists because "structurally
        // impossible" is a property of today's code, and the cost of it
        // quietly becoming possible again is a pharmacy's printed QR codes.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-onboard");

        await SaveKit(client, KitWithBooking("https://booking.riverside.co.uk/flu"));
        (await BookingUrlOf(client)).Should().Be("https://booking.riverside.co.uk/flu");

        using (var onboarded = await client.PostAsJsonAsync("/api/me/onboard", new
        {
            pharmacyName = "Riverside Pharmacy",
            address = "12 Riverside Walk",
            postCode = "M1 4AB",
            description = "",
            contactName = "A Pharmacist",
            contactPhone = "0161 496 0000",
            sonarFCode = "FA123",
            // The onboarding form carries no booking field, so this is what the
            // real client sends: nothing about the link at all.
        }))
        {
            onboarded.IsSuccessStatusCode.Should().BeTrue(
                "{0}", await onboarded.Content.ReadAsStringAsync());
        }

        (await BookingUrlOf(client)).Should().Be(
            "https://booking.riverside.co.uk/flu",
            "completing setup is not a request to delete a link set on the brand kit");
    }

    [Fact]
    public async Task Onboarding_cannot_set_the_link_even_if_a_client_sends_one()
    {
        // The other direction, and the reason the test above is not enough on
        // its own. A payload that carries a booking link must not be honoured
        // here either — if it were, onboarding would be a second writer, and two
        // writers is how the value drifted in the first place.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-onboard-write");

        await SaveKit(client, KitWithBooking("https://booking.riverside.co.uk/flu"));

        using (var onboarded = await client.PostAsJsonAsync("/api/me/onboard", new
        {
            pharmacyName = "Riverside Pharmacy",
            address = "12 Riverside Walk",
            postCode = "M1 4AB",
            description = "",
            contactName = "A Pharmacist",
            contactPhone = "0161 496 0000",
            sonarFCode = "FA123",
            bookingUrl = "https://somewhere-else.example/",
        }))
        {
            onboarded.IsSuccessStatusCode.Should().BeTrue(
                "{0}", await onboarded.Content.ReadAsStringAsync());
        }

        (await BookingUrlOf(client)).Should().Be(
            "https://booking.riverside.co.uk/flu",
            "the brand kit owns this value; onboarding does not get a say");
    }

    // Skipping onboarding is deliberately not covered here, and the reason is
    // worth recording so nobody adds it later and finds a 404. `skip-onboarding`
    // is a Next.js route, not an API one: it sets a session cookie, sends
    // nothing upstream, and leaves `onboardingCompleted` false. There is no
    // write for it to wipe anything with.

    [Fact]
    public async Task The_qr_route_answers_from_the_kit_and_404s_without_one()
    {
        // The QR is the reason the value is worth this much care, and it reads
        // the kit directly rather than going through the profile. 404 rather
        // than a blank image is deliberate at both ends: a patient cannot tell a
        // dead code from a working one until they have pointed a phone at it, so
        // no image is the honest answer, and the editor's control relies on this
        // status to know whether to offer the button at all.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("booking-qr-route");

        using (var before = await client.GetAsync("/api/brand-kit/booking-qr"))
        {
            before.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "no brand kit means no link, which means no code");
        }

        await SaveKit(client, KitWithBooking("https://booking.riverside.co.uk/flu"));

        using var after = await client.GetAsync("/api/brand-kit/booking-qr");
        after.IsSuccessStatusCode.Should().BeTrue(
            "{0}", await after.Content.ReadAsStringAsync());
        after.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }
}
