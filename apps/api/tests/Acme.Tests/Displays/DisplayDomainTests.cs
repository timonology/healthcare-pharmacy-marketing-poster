using Acme.Application.Displays;
using Acme.Domain.Common;
using Acme.Domain.Displays;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Displays;

/// <summary>
/// The rules a screen and a playlist enforce on their own.
/// </summary>
/// <remarks>
/// No database and no HTTP: these are the invariants that must hold before any
/// of it is reachable. The integration suite covers who may call what; this
/// covers what is true regardless of who called.
/// </remarks>
public class PlaylistTests
{
    private const string Owner = "owner-1";

    private static PlaylistItem Poster(string id, int seconds = 10) =>
        new(PlaylistItemKind.Poster, id, seconds);

    [Fact]
    public void A_new_playlist_starts_empty_at_version_one()
    {
        var playlist = Playlist.Create(Owner, "  Winter  ");

        playlist.Items.Should().BeEmpty();
        playlist.Version.Should().Be(1);
        playlist.Name.Should().Be("Winter", "names are trimmed on the way in");
        playlist.OwnerId.Should().Be(Owner);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_playlist_needs_a_name(string name)
    {
        var act = () => Playlist.Create(Owner, name);
        act.Should().Throw<DomainException>();
    }

    /* ── the version, which is what makes screens update ──────────────── */

    [Fact]
    public void Changing_the_running_order_moves_the_version()
    {
        var playlist = Playlist.Create(Owner, "Loop");
        playlist.SetItems(new[] { Poster("a"), Poster("b") });

        playlist.Version.Should().Be(2);
    }

    [Fact]
    public void Renaming_does_not_move_the_version()
    {
        // The kiosk builds its ETag from the version. If a rename moved it,
        // every screen showing this playlist would drop its cached images and
        // re-download all of them because somebody fixed a spelling mistake.
        var playlist = Playlist.Create(Owner, "Wnter");
        playlist.SetItems(new[] { Poster("a") });
        var afterItems = playlist.Version;

        playlist.Rename("Winter");

        playlist.Version.Should().Be(afterItems);
        playlist.Name.Should().Be("Winter", "the rename still happened");
    }

    [Fact]
    public void Setting_the_same_items_again_does_not_move_the_version()
    {
        // The editor sends the whole list back on every save, including saves
        // where only the name changed. Bumping the version for an identical
        // list would push a no-op refresh to every screen.
        var playlist = Playlist.Create(Owner, "Loop");
        playlist.SetItems(new[] { Poster("a"), Poster("b") });
        var version = playlist.Version;

        playlist.SetItems(new[] { Poster("a"), Poster("b") });

        playlist.Version.Should().Be(version);
    }

    [Fact]
    public void Changing_only_a_duration_moves_the_version()
    {
        // Same posters, different dwell time. A screen has to notice: the
        // durations are what it runs its timer from.
        var playlist = Playlist.Create(Owner, "Loop");
        playlist.SetItems(new[] { Poster("a", 10) });
        var version = playlist.Version;

        playlist.SetItems(new[] { Poster("a", 25) });

        playlist.Version.Should().BeGreaterThan(version);
    }

    /* ── what a playlist refuses ──────────────────────────────────────── */

    [Theory]
    [InlineData(Playlist.MinDurationSeconds - 1)]
    [InlineData(Playlist.MaxDurationSeconds + 1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_slot_outside_the_bounds_is_refused(int seconds)
    {
        var playlist = Playlist.Create(Owner, "Loop");

        var act = () => playlist.SetItems(new[] { Poster("a", seconds) });

        act.Should().Throw<DomainException>()
            .WithMessage("*seconds*", "the message has to say what the bound is");
    }

    [Fact]
    public void The_same_poster_twice_in_a_row_is_refused()
    {
        // On a wall this reads as the screen having frozen, which is a support
        // call rather than a design choice.
        var playlist = Playlist.Create(Owner, "Loop");

        var act = () => playlist.SetItems(new[] { Poster("a"), Poster("a") });

        act.Should().Throw<DomainException>().WithMessage("*frozen*");
    }

    [Fact]
    public void The_same_poster_at_both_ends_of_a_loop_is_allowed()
    {
        // Deliberately not symmetrical with the case above. A long loop that
        // opens and closes on the same notice is a real thing somebody would
        // build, and only adjacency looks broken.
        var playlist = Playlist.Create(Owner, "Loop");

        var act = () => playlist.SetItems(new[] { Poster("a"), Poster("b"), Poster("a") });

        act.Should().NotThrow();
        playlist.Items.Should().HaveCount(3);
    }

    [Fact]
    public void A_playlist_longer_than_the_limit_is_refused()
    {
        var playlist = Playlist.Create(Owner, "Loop");

        // Alternating ids, or the adjacency rule would reject it first and this
        // would pass for the wrong reason.
        var tooMany = Enumerable.Range(0, Playlist.MaxItems + 1)
            .Select(i => Poster($"poster-{i}"))
            .ToArray();

        var act = () => playlist.SetItems(tooMany);

        act.Should().Throw<DomainException>().WithMessage($"*{Playlist.MaxItems}*");
    }

    [Fact]
    public void Exactly_the_limit_is_allowed()
    {
        // The boundary itself, because an off-by-one here refuses a playlist
        // somebody is allowed to build.
        var playlist = Playlist.Create(Owner, "Loop");

        var atLimit = Enumerable.Range(0, Playlist.MaxItems)
            .Select(i => Poster($"poster-{i}"))
            .ToArray();

        playlist.SetItems(atLimit);

        playlist.Items.Should().HaveCount(Playlist.MaxItems);
    }
}

/// <summary>A screen, its pairing code, and the credentials that reach it.</summary>
public class DisplayScreenTests
{
    private const string Owner = "owner-1";

    private static DisplayScreen NewScreen() =>
        DisplayScreen.Register(Owner, "Waiting room", "By the door", "1 High Street");

    [Fact]
    public void A_new_screen_is_unpaired_and_carries_a_live_code()
    {
        var screen = NewScreen();

        screen.IsPaired.Should().BeFalse();
        screen.PairingCodeIsLive.Should().BeTrue();
        screen.PairingCode.Should().HaveLength(DisplayScreen.PairingCodeLength);
        screen.Credentials.Should().BeEmpty();
    }

    [Fact]
    public void The_pairing_code_avoids_characters_people_misread()
    {
        // An installer reads this off a laptop and types it into a television
        // with a remote control. O/0, I/1/L, S/5, Z/2, B/8 and U/V are where
        // that goes wrong, so the alphabet leaves them out — and a regression
        // here would surface only as support calls, never as a failure.
        const string forbidden = "01258BILOSUVZ";

        // Many draws, because one code says almost nothing about an alphabet.
        for (var i = 0; i < 200; i++)
        {
            var code = DisplayScreen.Register(Owner, "S", null, null).PairingCode!;

            code.Should().MatchRegex("^[A-Z0-9]+$");

            foreach (var bad in forbidden)
            {
                code.Should().NotContain(bad.ToString(),
                    $"'{bad}' is one an installer will mistype");
            }
        }
    }

    [Fact]
    public void Codes_are_not_all_the_same()
    {
        // Guards against a constant, which every other assertion here would
        // happily accept.
        var codes = Enumerable.Range(0, 50)
            .Select(_ => DisplayScreen.Register(Owner, "S", null, null).PairingCode!)
            .ToHashSet();

        codes.Should().HaveCountGreaterThan(40, "codes must be random, not fixed");
    }

    [Fact]
    public void Clearing_the_code_leaves_nothing_to_type()
    {
        var screen = NewScreen();
        screen.ClearPairingCode();

        screen.PairingCode.Should().BeNull();
        screen.PairingCodeIsLive.Should().BeFalse();
    }

    /* ── credentials ──────────────────────────────────────────────────── */

    [Fact]
    public void A_credential_makes_the_screen_paired_and_matchable()
    {
        var screen = NewScreen();
        var credential = screen.AddCredential(DisplayCredentialKind.PairedDevice, "hash-a");

        screen.IsPaired.Should().BeTrue();
        screen.MatchCredential("hash-a").Should().NotBeNull();
        screen.MatchCredential("hash-a")!.Id.Should().Be(credential.Id);
    }

    [Fact]
    public void Pairing_a_replacement_device_retires_the_old_one()
    {
        // A screen is one panel. Leaving the previous credential live would
        // mean a television the pharmacy has thrown away still pulls content.
        var screen = NewScreen();
        screen.AddCredential(DisplayCredentialKind.PairedDevice, "old");
        screen.AddCredential(DisplayCredentialKind.PairedDevice, "new");

        screen.MatchCredential("old").Should().BeNull("the old device must stop working");
        screen.MatchCredential("new").Should().NotBeNull();
        screen.Credentials.Count(c => c.IsActive).Should().Be(1);
    }

    [Fact]
    public void A_direct_link_does_not_disturb_a_paired_device()
    {
        // The two kinds are independent on purpose. Copying a link must not
        // silently unpair the screen already hanging on the wall.
        var screen = NewScreen();
        screen.AddCredential(DisplayCredentialKind.PairedDevice, "device");
        screen.AddCredential(DisplayCredentialKind.DirectLink, "link");

        screen.MatchCredential("device").Should().NotBeNull();
        screen.MatchCredential("link").Should().NotBeNull();
        screen.Credentials.Count(c => c.IsActive).Should().Be(2);
    }

    [Fact]
    public void A_revoked_credential_stops_matching()
    {
        var screen = NewScreen();
        var credential = screen.AddCredential(DisplayCredentialKind.PairedDevice, "hash-a");

        screen.Revoke(credential.Id).Should().BeTrue();

        screen.MatchCredential("hash-a").Should().BeNull();
        screen.IsPaired.Should().BeFalse();
        screen.Credentials.Should().HaveCount(1, "the record is kept, only deactivated");
    }

    [Fact]
    public void Revoking_twice_reports_that_it_was_already_gone()
    {
        var screen = NewScreen();
        var credential = screen.AddCredential(DisplayCredentialKind.DirectLink, "hash-a");

        screen.Revoke(credential.Id).Should().BeTrue();
        screen.Revoke(credential.Id).Should().BeFalse(
            "the caller turns this into 'that access has already been removed'");
    }

    /* ── last seen ────────────────────────────────────────────────────── */

    [Fact]
    public void Noting_a_poll_does_not_look_like_an_edit()
    {
        // A kiosk polls every thirty seconds for years. If last-seen moved
        // UpdatedAtUtc, every screen would rewrite its own document on every
        // poll and the field would be useless for spotting real changes.
        var screen = NewScreen();
        var credential = screen.AddCredential(DisplayCredentialKind.PairedDevice, "hash-a");
        var updatedAt = screen.UpdatedAtUtc;

        screen.NoteSeen(credential.Id);

        screen.UpdatedAtUtc.Should().Be(updatedAt, "a poll is not an edit");
        screen.LastSeenAtUtc.Should().NotBeNull();
        screen.Credentials.Single().LastSeenAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_screen_needs_a_name(string name)
    {
        var act = () => DisplayScreen.Register(Owner, name, null, null);
        act.Should().Throw<DomainException>();
    }
}

/// <summary>The token a screen presents, and what is stored about it.</summary>
public class DisplayTokenTests
{
    [Fact]
    public void The_raw_token_is_never_what_gets_stored()
    {
        var (raw, hash) = DisplayToken.Issue();

        hash.Should().NotBe(raw, "a database dump must not yield a working credential");
        hash.Should().MatchRegex("^[0-9a-f]{64}$", "SHA-256, lower-case hex");
    }

    [Fact]
    public void Hashing_the_same_token_gives_the_same_answer()
    {
        // Lookup is by hash, so this is the whole mechanism.
        var (raw, hash) = DisplayToken.Issue();

        DisplayToken.HashOf(raw).Should().Be(hash);
    }

    [Fact]
    public void Two_tokens_are_never_the_same()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => DisplayToken.Issue().Raw).ToHashSet();

        tokens.Should().HaveCount(500);
    }

    [Fact]
    public void An_issued_token_is_accepted_by_the_shape_check()
    {
        // The cheap guard in front of the database has to accept what the
        // issuer produces, or every screen is refused before it is looked up.
        for (var i = 0; i < 100; i++)
            DisplayToken.LooksWellFormed(DisplayToken.Issue().Raw).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("has spaces in it aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("../../etc/passwd/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Junk_is_turned_away_before_it_reaches_the_database(string? token)
        => DisplayToken.LooksWellFormed(token).Should().BeFalse();
}
