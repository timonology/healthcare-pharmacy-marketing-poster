using Acme.Application.Posters;
using Acme.Domain.Posters;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Posters;

/// <summary>
/// The rule that decides whether a poster may leave the pharmacy.
/// </summary>
/// <remarks>
/// Worth a test of its own because for a long time this rule did not exist. The
/// status was stored, it filtered the poster list, and nothing else in the
/// product ever read it — so publishing appeared to do nothing, and a draft
/// somebody was halfway through could be emailed to every patient on the list.
/// </remarks>
public class PosterUsageTests
{
    private static Poster PosterWith(PosterStatus status)
    {
        var poster = Poster.Create("owner-1", "Flu clinic", "{}");
        switch (status)
        {
            case PosterStatus.Published: poster.Publish(); break;
            case PosterStatus.Archived: poster.Archive(); break;
            case PosterStatus.Draft: break;
        }
        return poster;
    }

    [Fact]
    public void A_published_poster_can_be_sent()
    {
        var poster = PosterWith(PosterStatus.Published);

        PosterUsage.CanSend(poster).Should().BeTrue();
        PosterUsage.WhyCannotSend(poster, "sent in a campaign").Should().BeNull(
            "a published poster is exactly the thing the rule exists to allow");
    }

    [Theory]
    [InlineData(PosterStatus.Draft)]
    [InlineData(PosterStatus.Archived)]
    public void Anything_else_cannot(PosterStatus status)
    {
        var poster = PosterWith(status);

        PosterUsage.CanSend(poster).Should().BeFalse();
        PosterUsage.WhyCannotSend(poster, "sent in a campaign").Should().NotBeNull();
    }

    [Fact]
    public void A_new_poster_is_a_draft_and_so_cannot_be_sent()
    {
        // The default matters more than it looks. Posters are created as drafts,
        // so "cannot be sent" is the starting state and publishing is a
        // deliberate act — rather than sending being open until somebody
        // remembers to close it.
        var poster = Poster.Create("owner-1", "Brand new", "{}");

        poster.Status.Should().Be(PosterStatus.Draft);
        PosterUsage.CanSend(poster).Should().BeFalse();
    }

    [Theory]
    [InlineData(PosterStatus.Draft)]
    [InlineData(PosterStatus.Archived)]
    public void The_refusal_names_the_poster_and_says_what_to_do(PosterStatus status)
    {
        var poster = PosterWith(status);

        var why = PosterUsage.WhyCannotSend(poster, "sent in a campaign")!;

        // Which poster. A pharmacy with thirty posters and one refusal needs to
        // know which of them is the problem.
        why.Should().Contain("Flu clinic");

        // What to do about it. "This poster is a draft" states a fact and
        // leaves somebody stuck; the message has to name the way out.
        why.ToLowerInvariant().Should().Contain("publish");

        // And what they were trying to do, so the same rule can explain itself
        // differently for a campaign and for a social post.
        why.Should().Contain("sent in a campaign");
    }

    [Fact]
    public void The_action_is_woven_into_the_sentence()
    {
        var poster = PosterWith(PosterStatus.Draft);

        PosterUsage.WhyCannotSend(poster, "posted to social")
            .Should().Contain("posted to social");
    }

    [Fact]
    public void Restoring_an_archived_poster_does_not_publish_it()
    {
        // Restore returns a poster to Draft, not to Published. Somebody who
        // archived a poster months ago should look at it again before it goes
        // back out to patients.
        var poster = PosterWith(PosterStatus.Archived);
        poster.Restore();

        poster.Status.Should().Be(PosterStatus.Draft);
        PosterUsage.CanSend(poster).Should().BeFalse();
    }
}
