using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Screens in a pharmacy: who may set one up, and what a paired device can see.
/// </summary>
/// <remarks>
/// <para>
/// The kiosk endpoints are the only anonymous surface in the product that
/// returns a pharmacy's own content. Everything else public is either a
/// marketing page or a one-shot emailed link. So the questions worth asking
/// here are not "does it work" but "what does a token get you, and what happens
/// when it is taken away".
/// </para>
/// <para>
/// Every pharmacy is registered fresh. Two tests sharing an account is how an
/// isolation test comes to pass because both sides were the same pharmacy.
/// </para>
/// </remarks>
public sealed class DisplayScreenTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string TokenHeader = "X-Display-Token";

    /* ── fixtures ─────────────────────────────────────────────────────── */

    /// <summary>A Pro pharmacy with one screen, and the screen's pairing code.</summary>
    /// <remarks>
    /// The email comes back rather than the id, because the only test that
    /// wants either is checking the kiosk payload does not leak it — and the
    /// email is the piece of account data that would actually matter on a
    /// screen a stranger can photograph.
    /// </remarks>
    private async Task<(HttpClient Client, string Email, string ScreenId, string Code)>
        PharmacyWithScreenAsync(string label)
    {
        var (client, email, _, screenId, code) = await PharmacyWithScreenAndOwnerAsync(label);
        return (client, email, screenId, code);
    }

    /// <summary>The same, plus the pharmacy's id — for seeding rows it owns.</summary>
    private async Task<(HttpClient Client, string Email, string OwnerId, string ScreenId, string Code)>
        PharmacyWithScreenAndOwnerAsync(string label)
    {
        var (client, email, userId) = await Api.RegisterPharmacyAsync(label);
        await Api.UpgradeToProAsync(userId);

        using var res = await client.PostAsJsonAsync("/api/displays/screens", new
        {
            name = $"{label} screen",
            location = "Waiting room",
            addressLine = "1 High Street",
        });

        res.IsSuccessStatusCode.Should().BeTrue(
            "the screen must register: {0}", await res.Content.ReadAsStringAsync());

        var screen = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (
            client,
            email,
            userId,
            screen.GetProperty("id").GetString()!,
            screen.GetProperty("pairingCode").GetString()!);
    }

    /// <summary>Claims a code the way a television does: anonymously.</summary>
    private async Task<string> PairAsync(string code)
    {
        var device = Api.NewClient();

        using var res = await device.PostAsJsonAsync("/api/display/claim", new { code });
        res.IsSuccessStatusCode.Should().BeTrue(
            "pairing must succeed: {0}", await res.Content.ReadAsStringAsync());

        return (await res.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("token").GetString()!;
    }

    /// <summary>Two screens, paired one after the other. This used to 500.</summary>
    /// <remarks>
    /// <para>
    /// <b>Pairing clears the code, and clearing wrote a null into a unique
    /// index.</b> <c>uniq_pairing_code</c> was declared <c>Sparse</c>, which
    /// only skips documents where the field is <i>absent</i>;
    /// <c>DisplayScreenDocument.PairingCode</c> has no
    /// <c>[BsonIgnoreIfNull]</c>, so a cleared code is written as an explicit
    /// <c>pairingCode: null</c>. The first screen to be paired put a null in
    /// the index and the second collided with it.
    /// </para>
    /// <para>
    /// The blast radius was the whole installed base, not one pharmacy: the
    /// index is collection-wide, so the second screen <i>ever</i> paired would
    /// have failed — from the single endpoint an installer touches, with a 500.
    /// One screen worked perfectly, which is exactly why a demo would not have
    /// found it.
    /// </para>
    /// <para>
    /// The other tests in this file do catch it, but only by accident of
    /// running after one another against a shared database. That is not a
    /// property to rely on — it disappears the moment tests get their own
    /// database or the order changes. This one states the requirement.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_second_screen_can_still_be_paired_after_the_first()
    {
        RequireDatabase();

        var first = await PharmacyWithScreenAsync("pair-one");
        var second = await PharmacyWithScreenAsync("pair-two");

        await PairAsync(first.Code);

        // The assertion is that this does not throw. Deliberately a separate
        // pharmacy: the index is global, so same-account is the easier case and
        // would not prove the interesting one.
        await PairAsync(second.Code);
    }

    /// <summary>A client that presents a screen token and nothing else.</summary>
    private HttpClient DeviceWith(string token)
    {
        var device = Api.NewClient();
        device.DefaultRequestHeaders.Add(TokenHeader, token);
        return device;
    }

    private async Task<string> PublishedPosterAsync(HttpClient client, string name)
    {
        using var created = await client.PostAsJsonAsync("/api/posters", new { name });
        created.IsSuccessStatusCode.Should().BeTrue();

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        using var published = await client.PatchAsJsonAsync(
            $"/api/posters/{id}/status", new { status = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue();

        return id;
    }

    /// <summary>Creates a playlist holding one poster and puts it on a screen.</summary>
    private async Task<string> PlaylistOnScreenAsync(
        HttpClient client, string screenId, string posterId)
    {
        using var created = await client.PostAsJsonAsync("/api/displays/playlists", new
        {
            name = "Loop",
            items = new[] { new { kind = "Poster", refId = posterId, durationSeconds = 10 } },
        });

        created.IsSuccessStatusCode.Should().BeTrue(
            "the playlist must save: {0}", await created.Content.ReadAsStringAsync());

        var playlistId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        using var assigned = await client.PutAsJsonAsync(
            $"/api/displays/screens/{screenId}/playlist", new { playlistId });
        assigned.IsSuccessStatusCode.Should().BeTrue();

        return playlistId;
    }

    /* ── who may have a screen at all ─────────────────────────────────── */

    [Fact]
    public async Task A_free_pharmacy_is_told_screens_are_a_paid_feature()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("free-screen");

        using var res = await client.PostAsJsonAsync(
            "/api/displays/screens", new { name = "Window" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Free", "the refusal names the plan they are on");
        body.ToLowerInvariant().Should().Contain("upgrade", "and the way out of it");
    }

    [Fact]
    public async Task Starter_covers_one_screen_and_says_so_on_the_second()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("starter-screen");
        await Api.SetTierAsync(userId, "Starter");

        using (var first = await client.PostAsJsonAsync(
            "/api/displays/screens", new { name = "Counter" }))
        {
            first.IsSuccessStatusCode.Should().BeTrue(
                "one screen is inside the Starter plan: {0}",
                await first.Content.ReadAsStringAsync());
        }

        using var second = await client.PostAsJsonAsync(
            "/api/displays/screens", new { name = "Window" });

        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await second.Content.ReadAsStringAsync()).Should().Contain("Starter");
    }

    /* ── pairing ──────────────────────────────────────────────────────── */

    [Fact]
    public async Task A_pairing_code_works_once_and_then_never_again()
    {
        RequireDatabase();

        var (_, _, _, code) = await PharmacyWithScreenAsync("pair-once");

        await PairAsync(code);

        // A second television typing the same six characters a minute later
        // gets nothing. Without this, a code read aloud across a shop floor
        // would pair every device in earshot.
        var second = Api.NewClient();
        using var again = await second.PostAsJsonAsync("/api/display/claim", new { code });

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a claimed code is spent");
    }

    [Fact]
    public async Task A_code_nobody_issued_is_refused_without_saying_why()
    {
        RequireDatabase();

        var device = Api.NewClient();

        using var res = await device.PostAsJsonAsync(
            "/api/display/claim", new { code = "AAAAAA" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The code space is small and this endpoint is anonymous, so a response
        // that distinguished "no such code" from "expired" would let somebody
        // enumerate live codes across every pharmacy on the platform.
        var body = (await res.Content.ReadAsStringAsync()).ToLowerInvariant();
        body.Should().NotContain("expired");
        body.Should().NotContain("not found");
    }

    [Fact]
    public async Task Pairing_a_second_device_retires_the_first()
    {
        RequireDatabase();

        var (client, _, screenId, code) = await PharmacyWithScreenAsync("replace");
        var firstToken = await PairAsync(code);

        using var refreshed = await client.PostAsync(
            $"/api/displays/screens/{screenId}/pairing-code", null);
        var newCode = (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("pairingCode").GetString()!;

        var secondToken = await PairAsync(newCode);
        secondToken.Should().NotBe(firstToken);

        // The television that was swapped out must stop pulling content, or a
        // device sold on eBay keeps showing a pharmacy's posters.
        using var oldDevice = DeviceWith(firstToken);
        using var stale = await oldDevice.GetAsync("/api/display/content");
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var newDevice = DeviceWith(secondToken);
        using var live = await newDevice.GetAsync("/api/display/content");
        live.IsSuccessStatusCode.Should().BeTrue();
    }

    /* ── what a token gets you, and what it does not ──────────────────── */

    [Fact]
    public async Task A_request_with_no_token_is_incomplete_rather_than_rejected()
    {
        RequireDatabase();

        var device = Api.NewClient();
        using var res = await device.GetAsync("/api/display/content");

        // 400, not 401, and the distinction is load-bearing: a screen treats
        // 401 as "your credential is finished" and erases it. A proxy that
        // stripped the header must not cost a working display its pairing.
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_revoked_credential_stops_the_screen_immediately()
    {
        RequireDatabase();

        var (client, _, screenId, code) = await PharmacyWithScreenAsync("revoke");
        var token = await PairAsync(code);

        using var device = DeviceWith(token);

        using (var before = await device.GetAsync("/api/display/content"))
        {
            before.IsSuccessStatusCode.Should().BeTrue();
        }

        using var screens = await client.GetAsync("/api/displays/screens");
        var credentialId = (await screens.Content.ReadFromJsonAsync<JsonElement>(Json))
            .EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == screenId)
            .GetProperty("credentials")[0]
            .GetProperty("id").GetString()!;

        using var revoked = await client.DeleteAsync(
            $"/api/displays/screens/{screenId}/credentials/{credentialId}");
        revoked.IsSuccessStatusCode.Should().BeTrue();

        // The whole point of a revoke button. A screen that keeps working after
        // being switched off in the dashboard is worse than no button at all.
        using var after = await device.GetAsync("/api/display/content");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_screen_token_reaches_only_its_own_pharmacy()
    {
        RequireDatabase();

        var (mine, _, myScreen, myCode) = await PharmacyWithScreenAsync("mine");
        var (theirs, _, theirScreen, theirCode) = await PharmacyWithScreenAsync("theirs");

        var myPoster = await PublishedPosterAsync(mine, "My poster");
        await PlaylistOnScreenAsync(mine, myScreen, myPoster);

        var theirPoster = await PublishedPosterAsync(theirs, "Their poster");
        await PlaylistOnScreenAsync(theirs, theirScreen, theirPoster);

        var myToken = await PairAsync(myCode);
        await PairAsync(theirCode);

        using var myDevice = DeviceWith(myToken);

        // My screen sees my poster.
        using var content = await myDevice.GetAsync("/api/display/content");
        content.IsSuccessStatusCode.Should().BeTrue();
        (await content.Content.ReadAsStringAsync()).Should().Contain("My poster");

        // And cannot fetch theirs, even naming it directly. A token is a key to
        // one screen's playlist, not to a pharmacy's whole poster library.
        using var stolen = await myDevice.GetAsync($"/api/display/slides/{theirPoster}");
        stolen.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_poster_not_on_the_playlist_is_not_served_to_the_screen()
    {
        RequireDatabase();

        var (client, _, screenId, code) = await PharmacyWithScreenAsync("scope");

        var onScreen = await PublishedPosterAsync(client, "On the loop");
        var elsewhere = await PublishedPosterAsync(client, "Not on the loop");
        await PlaylistOnScreenAsync(client, screenId, onScreen);

        using var device = DeviceWith(await PairAsync(code));

        using var allowed = await device.GetAsync($"/api/display/slides/{onScreen}");
        allowed.IsSuccessStatusCode.Should().BeTrue(
            "the poster on the playlist must render: {0}",
            await allowed.Content.ReadAsStringAsync());

        // Same pharmacy, same token, a poster that is simply not on this
        // screen. Without this a screen in a public waiting room would be a
        // key to every published poster the pharmacy owns.
        using var refused = await device.GetAsync($"/api/display/slides/{elsewhere}");
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /* ── the playlist rules the screen depends on ─────────────────────── */

    [Fact]
    public async Task A_draft_poster_cannot_be_put_on_a_screen()
    {
        RequireDatabase();

        var (client, _, _, _) = await PharmacyWithScreenAsync("draft");

        using var created = await client.PostAsJsonAsync(
            "/api/posters", new { name = "Half finished" });
        var draftId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        using var res = await client.PostAsJsonAsync("/api/displays/playlists", new
        {
            name = "Loop",
            items = new[] { new { kind = "Poster", refId = draftId, durationSeconds = 10 } },
        });

        // A screen in a shop window is publication. The same rule campaigns use.
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).ToLowerInvariant()
            .Should().Contain("publish");
    }

    [Fact]
    public async Task Another_pharmacys_playlist_cannot_be_put_on_my_screen()
    {
        RequireDatabase();

        var (mine, _, myScreen, _) = await PharmacyWithScreenAsync("borrower");
        var (theirs, _, theirScreen, _) = await PharmacyWithScreenAsync("lender");

        var theirPoster = await PublishedPosterAsync(theirs, "Their poster");
        var theirPlaylist = await PlaylistOnScreenAsync(theirs, theirScreen, theirPoster);

        using var res = await mine.PutAsJsonAsync(
            $"/api/displays/screens/{myScreen}/playlist",
            new { playlistId = theirPlaylist });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "not-found rather than forbidden — a distinguishable refusal would "
            + "confirm the playlist exists");
    }

    [Fact]
    public async Task Unpublishing_a_poster_takes_it_off_the_screen()
    {
        RequireDatabase();

        var (client, _, screenId, code) = await PharmacyWithScreenAsync("unpublish");
        var posterId = await PublishedPosterAsync(client, "Seasonal notice");
        await PlaylistOnScreenAsync(client, screenId, posterId);

        using var device = DeviceWith(await PairAsync(code));

        using (var before = await device.GetAsync("/api/display/content"))
        {
            (await before.Content.ReadAsStringAsync()).Should().Contain("Seasonal notice");
        }

        using var draft = await client.PatchAsJsonAsync(
            $"/api/posters/{posterId}/status", new { status = "Draft" });
        draft.IsSuccessStatusCode.Should().BeTrue();

        // Withdrawing a poster has to reach the wall, not just the website.
        // The playlist still references it; the kiosk is what must not show it.
        using var after = await device.GetAsync("/api/display/content");
        after.IsSuccessStatusCode.Should().BeTrue();

        var body = await after.Content.ReadAsStringAsync();
        body.Should().NotContain("Seasonal notice",
            "an unpublished poster must leave every screen showing it");
    }

    /* ── the poll ─────────────────────────────────────────────────────── */

    [Fact]
    public async Task An_unchanged_playlist_answers_304()
    {
        RequireDatabase();

        var (client, _, screenId, code) = await PharmacyWithScreenAsync("etag");
        var posterId = await PublishedPosterAsync(client, "Notice");
        await PlaylistOnScreenAsync(client, screenId, posterId);

        using var device = DeviceWith(await PairAsync(code));

        using var first = await device.GetAsync("/api/display/content");
        first.IsSuccessStatusCode.Should().BeTrue();

        var etag = first.Headers.ETag?.Tag;
        etag.Should().NotBeNullOrEmpty("the poll is only affordable because of this");

        using var repeat = new HttpRequestMessage(HttpMethod.Get, "/api/display/content");
        repeat.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var second = await device.SendAsync(repeat);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified,
            "a screen polls this every thirty seconds for years");
    }

    [Fact]
    public async Task Editing_the_playlist_changes_the_etag()
    {
        RequireDatabase();

        var (client, _, screenId, code) = await PharmacyWithScreenAsync("etag-moves");
        var first = await PublishedPosterAsync(client, "First notice");
        var playlistId = await PlaylistOnScreenAsync(client, screenId, first);

        using var device = DeviceWith(await PairAsync(code));

        using var before = await device.GetAsync("/api/display/content");
        var original = before.Headers.ETag?.Tag;

        var second = await PublishedPosterAsync(client, "Second notice");
        using var edited = await client.PutAsJsonAsync(
            $"/api/displays/playlists/{playlistId}",
            new
            {
                name = "Loop",
                items = new[]
                {
                    new { kind = "Poster", refId = first, durationSeconds = 10 },
                    new { kind = "Poster", refId = second, durationSeconds = 10 },
                },
            });
        edited.IsSuccessStatusCode.Should().BeTrue(
            "the edit must save: {0}", await edited.Content.ReadAsStringAsync());

        // The other half of the ETag bargain. If it never moved, a screen would
        // keep showing last month's loop and nobody could explain why.
        using var after = await device.GetAsync("/api/display/content");
        after.Headers.ETag?.Tag.Should().NotBe(original);
        (await after.Content.ReadAsStringAsync()).Should().Contain("Second notice");
    }

    [Fact]
    public async Task The_payload_carries_nothing_about_the_pharmacy_account()
    {
        RequireDatabase();

        var (client, email, screenId, code) = await PharmacyWithScreenAsync("payload");
        var posterId = await PublishedPosterAsync(client, "Notice");
        await PlaylistOnScreenAsync(client, screenId, posterId);

        using var device = DeviceWith(await PairAsync(code));
        using var res = await device.GetAsync("/api/display/content");

        var body = await res.Content.ReadAsStringAsync();

        // A kiosk hangs in a room strangers walk through and can be
        // photographed, so the payload is limited to what a passer-by could
        // read off the glass anyway.
        body.Should().NotContain(email, "the account's email is not for the wall");
        body.Should().NotContain("ownerId");
        body.Should().NotContain(screenId,
            "the screen's own id is not needed by the device and is a handle to a record");

        // The poster id is the one identifier that does appear, and only as the
        // path of the image the screen is about to draw.
        //
        // This assertion used to be a flat NotContain(posterId) and it failed on
        // correct output. Worth being explicit about why it was narrowed rather
        // than deleted, because "a security test was in the way" is how real
        // checks get quietly weakened:
        //
        //   - the id is an opaque GUID for an image already on public display,
        //     so it reveals strictly less than the picture beside it;
        //   - no anonymous endpoint anywhere accepts a poster id — the only
        //     route that does is /api/display/slides, which requires this
        //     screen's token AND refuses any poster not on this screen's
        //     playlist. Checked against the reviewed anonymous list in
        //     AuthorizationMatrixTests before relaxing this;
        //   - the alternative, an opaque per-slide slug, would have to stay
        //     stable across playlist reorders or every screen re-downloads
        //     every image whenever somebody drags a row.
        //
        // So the requirement is that it appears *only* there. That is a tighter
        // statement than the original: a poster id leaking into any other field
        // still fails.
        // mediaUrl is the same image path for a poster (and a video's signed
        // path for a video) — the same "only as the path of what to draw" rule.
        var withoutImageUrls = Regex.Replace(
            body, @"""(imageUrl|mediaUrl)"":""[^""]*""", @"""$1"":""""");
        withoutImageUrls.Should().NotContain(posterId,
            "outside the image path there is no reason for the device to know a record id");
    }

    /* ── videos on the screen ─────────────────────────────────────────── */

    private const string CapabilitiesHeader = "X-Display-Capabilities";

    /// <summary>A device that says it can play video, as the current kiosk does.</summary>
    private HttpClient VideoDeviceWith(string token)
    {
        var device = DeviceWith(token);
        device.DefaultRequestHeaders.Add(CapabilitiesHeader, "video");
        return device;
    }

    private async Task<(HttpResponseMessage Response, string Body)> SavePlaylistAsync(
        HttpClient client, params object[] items)
    {
        var res = await client.PostAsJsonAsync("/api/displays/playlists", new { name = "Loop", items });
        return (res, await res.Content.ReadAsStringAsync());
    }

    private async Task<string> AssignedPlaylistAsync(HttpClient client, string screenId, params object[] items)
    {
        var (res, body) = await SavePlaylistAsync(client, items);
        using (res) res.IsSuccessStatusCode.Should().BeTrue("the playlist must save: {0}", body);

        var playlistId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        using var assigned = await client.PutAsJsonAsync(
            $"/api/displays/screens/{screenId}/playlist", new { playlistId });
        assigned.IsSuccessStatusCode.Should().BeTrue();
        return playlistId;
    }

    private static object PosterItem(string id) => new { kind = "Poster", refId = id, durationSeconds = 10 };
    private static object VideoItem(string id, int seconds = 10) => new { kind = "Video", refId = id, durationSeconds = seconds };

    private static JsonElement[] Slides(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("slides").EnumerateArray().ToArray();

    [Fact]
    public async Task A_ready_video_plays_on_the_screen_from_a_signed_link()
    {
        RequireDatabase();

        var (client, _, userId, screenId, code) = await PharmacyWithScreenAndOwnerAsync("video-ready");
        var posterId = await PublishedPosterAsync(client, "Flu clinic");
        var videoId = await Api.SeedVideoAsync(userId, "Flu jab explainer",
            Acme.Domain.Videos.VideoStatus.Ready, durationSeconds: 24);

        // 99 is asked for; a video plays for its own length, so 24 is kept.
        await AssignedPlaylistAsync(client, screenId, PosterItem(posterId), VideoItem(videoId, 99));

        using var device = VideoDeviceWith(await PairAsync(code));
        using var res = await device.GetAsync("/api/display/content");
        res.IsSuccessStatusCode.Should().BeTrue();
        var body = await res.Content.ReadAsStringAsync();

        var slides = Slides(body);
        slides.Select(s => s.GetProperty("kind").GetString())
            .Should().Equal(new[] { "Poster", "Video" }, "the playlist order is the screen's order");

        var video = slides[1];
        video.GetProperty("durationSeconds").GetInt32().Should().Be(24);
        video.GetProperty("imageUrl").GetString().Should().BeEmpty();

        var mediaUrl = video.GetProperty("mediaUrl").GetString()!;
        mediaUrl.Should().StartWith($"/api/display/video/{videoId}?sig=",
            "a same-origin path the <video> element can play without the screen token");
        body.Should().NotContain("videos/" + videoId + ".mp4", "no blob key reaches the browser");
        body.Should().NotContain(device.DefaultRequestHeaders.GetValues("X-Display-Token").Single(),
            "the pairing token must never travel inside a URL");

        // The signature is one the API's own signed download accepts — the
        // path the web app's /api/display/video route forwards to — and it
        // answers ranges, without which Safari will not play a video at all.
        // The link also carries the video's version (&v=), so a re-cut is a
        // new URL and a browser cannot keep playing the old cut from cache.
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + mediaUrl).Query);
        var sig = query["sig"]!;
        query["v"].Should().NotBeNullOrEmpty();
        using var anonymous = Api.NewClient();
        using var ranged = new HttpRequestMessage(HttpMethod.Get,
            $"/api/videos/{videoId}/public-download?token={Uri.EscapeDataString(sig)}");
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var download = await anonymous.SendAsync(ranged);

        download.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        var range = download.Content.Headers.ContentRange;
        range.Should().NotBeNull();
        range!.From.Should().Be(10);
        range.Length.Should().Be(ApiFactory.SeededVideoBytes.Length);

        // A strong validator, or the screen's browser cannot cache ranged video
        // and every loop downloads every MP4 again.
        download.Headers.ETag.Should().NotBeNull();
        download.Headers.ETag!.IsWeak.Should().BeFalse();
        (await download.Content.ReadAsByteArrayAsync())
            .Should().Equal(ApiFactory.SeededVideoBytes.Skip(10).Take(10));

        // …and it is bound to that one video.
        var otherId = await Api.SeedVideoAsync(userId, "Another", Acme.Domain.Videos.VideoStatus.Ready);
        using var misused = await anonymous.GetAsync(
            $"/api/videos/{otherId}/public-download?token={Uri.EscapeDataString(sig)}");
        misused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a video's link must not open a different video");
    }

    [Fact]
    public async Task A_kiosk_that_cannot_play_video_is_sent_posters_only()
    {
        RequireDatabase();

        var (client, _, userId, screenId, code) = await PharmacyWithScreenAndOwnerAsync("video-legacy");
        var posterId = await PublishedPosterAsync(client, "Opening hours");
        var videoId = await Api.SeedVideoAsync(userId, "Explainer", Acme.Domain.Videos.VideoStatus.Ready);
        await AssignedPlaylistAsync(client, screenId, PosterItem(posterId), VideoItem(videoId));

        // A kiosk page left open through the deploy runs the old code, which
        // draws every slide as an image. Handed a video it would show a broken
        // picture for the video's whole turn.
        using var device = DeviceWith(await PairAsync(code));
        using var res = await device.GetAsync("/api/display/content");
        var slides = Slides(await res.Content.ReadAsStringAsync());

        slides.Should().ContainSingle();
        slides[0].GetProperty("kind").GetString().Should().Be("Poster");
    }

    [Theory]
    [InlineData(Acme.Domain.Videos.VideoStatus.Pending, true)]
    [InlineData(Acme.Domain.Videos.VideoStatus.Rendering, true)]
    [InlineData(Acme.Domain.Videos.VideoStatus.Failed, true)]
    // Ready without a file is the one "Ready" that is still not finished.
    [InlineData(Acme.Domain.Videos.VideoStatus.Ready, false)]
    public async Task Only_a_finished_video_can_be_added(
        Acme.Domain.Videos.VideoStatus status, bool withFile)
    {
        RequireDatabase();

        var (client, _, userId, _, _) = await PharmacyWithScreenAndOwnerAsync($"video-{status}");
        var videoId = await Api.SeedVideoAsync(userId, "Not finished", status, withFile: withFile);

        var (res, body) = await SavePlaylistAsync(client, VideoItem(videoId));
        using (res)
        {
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().Contain("not ready yet");
        }
    }

    [Fact]
    public async Task Another_pharmacys_video_cannot_be_added()
    {
        RequireDatabase();

        var (mine, _, _, _) = await PharmacyWithScreenAsync("video-mine");
        var (_, _, theirId, _, _) = await PharmacyWithScreenAndOwnerAsync("video-theirs");
        var theirVideo = await Api.SeedVideoAsync(theirId, "Theirs", Acme.Domain.Videos.VideoStatus.Ready);

        var (res, body) = await SavePlaylistAsync(mine, VideoItem(theirVideo));
        using (res)
        {
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            body.Should().Contain("could not be found",
                "the same answer as a video that does not exist — no hint it is someone else's");
            body.Should().NotContain("Theirs");
        }
    }

    [Fact]
    public async Task A_video_that_stops_being_ready_leaves_the_screen_on_the_next_poll()
    {
        RequireDatabase();

        var (client, _, userId, screenId, code) = await PharmacyWithScreenAndOwnerAsync("video-drops");
        var posterId = await PublishedPosterAsync(client, "Notice");
        var videoId = await Api.SeedVideoAsync(userId, "Being re-cut", Acme.Domain.Videos.VideoStatus.Ready);
        await AssignedPlaylistAsync(client, screenId, PosterItem(posterId), VideoItem(videoId));

        using var device = VideoDeviceWith(await PairAsync(code));
        using var before = await device.GetAsync("/api/display/content");
        var beforeSlides = Slides(await before.Content.ReadAsStringAsync());
        beforeSlides.Should().HaveCount(2);
        var etag = before.Headers.ETag?.Tag;
        var firstCut = beforeSlides[1].GetProperty("mediaUrl").GetString();

        foreach (var status in new[]
                 {
                     Acme.Domain.Videos.VideoStatus.Rendering,
                     Acme.Domain.Videos.VideoStatus.Failed,
                 })
        {
            await Api.SetVideoStatusAsync(videoId, status);

            using var poll = new HttpRequestMessage(HttpMethod.Get, "/api/display/content");
            poll.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var after = await device.SendAsync(poll);

            after.StatusCode.Should().Be(HttpStatusCode.OK,
                "the screen's content changed, so a 304 would keep the video on the wall");
            var slides = Slides(await after.Content.ReadAsStringAsync());
            slides.Should().ContainSingle().Which.GetProperty("kind").GetString().Should().Be("Poster");
        }

        // Ready again, back on the screen — the playlist kept it.
        await Api.SetVideoStatusAsync(videoId, Acme.Domain.Videos.VideoStatus.Ready);
        using var back = await device.GetAsync("/api/display/content");
        var backSlides = Slides(await back.Content.ReadAsStringAsync());
        backSlides.Should().HaveCount(2);

        // A re-cut writes the new MP4 under the same key. The link must change
        // with it, or a screen's browser keeps playing the old cut from cache.
        backSlides[1].GetProperty("mediaUrl").GetString().Should().NotBe(firstCut);
    }

    [Fact]
    public async Task A_screen_is_sent_only_the_videos_on_its_own_playlist()
    {
        RequireDatabase();

        var (client, _, userId, screenId, code) = await PharmacyWithScreenAndOwnerAsync("video-scope");
        var onScreen = await Api.SeedVideoAsync(userId, "On this screen", Acme.Domain.Videos.VideoStatus.Ready);
        var elsewhere = await Api.SeedVideoAsync(userId, "On another playlist", Acme.Domain.Videos.VideoStatus.Ready);

        await AssignedPlaylistAsync(client, screenId, VideoItem(onScreen));
        var (other, otherBody) = await SavePlaylistAsync(client, VideoItem(elsewhere));
        using (other) other.IsSuccessStatusCode.Should().BeTrue(otherBody);

        using var device = VideoDeviceWith(await PairAsync(code));
        using var res = await device.GetAsync("/api/display/content");
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain(onScreen);
        body.Should().NotContain(elsewhere,
            "the token is for one screen's playlist, not every video the pharmacy owns");
        body.Should().NotContain("On another playlist");
    }

    [Fact]
    public async Task A_playlist_holds_at_most_ten_videos()
    {
        RequireDatabase();

        var (client, _, userId, _, _) = await PharmacyWithScreenAndOwnerAsync("video-cap");

        var ids = new List<string>();
        for (var i = 0; i < 11; i++)
            ids.Add(await Api.SeedVideoAsync(userId, $"Clip {i}", Acme.Domain.Videos.VideoStatus.Ready));

        var (ten, tenBody) = await SavePlaylistAsync(client, ids.Take(10).Select(id => VideoItem(id)).ToArray());
        using (ten) ten.IsSuccessStatusCode.Should().BeTrue(tenBody);

        var (eleven, elevenBody) = await SavePlaylistAsync(client, ids.Select(id => VideoItem(id)).ToArray());
        using (eleven)
        {
            eleven.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            elevenBody.Should().Contain("at most 10 videos");
        }
    }
}
