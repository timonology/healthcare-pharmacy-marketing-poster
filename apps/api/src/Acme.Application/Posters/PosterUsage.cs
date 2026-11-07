using Acme.Domain.Posters;

namespace Acme.Application.Posters;

/// <summary>
/// When a poster may be sent to somebody outside the pharmacy.
/// </summary>
/// <remarks>
/// <para>
/// Draft means "still working on it". Published means "this is finished and may
/// go out". Archived means "retired". Until now those were three labels and
/// nothing more: the status was stored, it filtered the poster list, and no
/// other part of the product ever read it. A pharmacist could publish a poster
/// and watch nothing happen, or send a half-finished draft to five hundred
/// patients with no warning at all.
/// </para>
/// <para>
/// The rule lives here rather than in each caller because there are several
/// ways out of the product — an email or SMS campaign, a social post — and a
/// rule enforced in three places is a rule enforced in two places as soon as
/// somebody adds a fourth.
/// </para>
/// <para>
/// It is deliberately about *sending*, not about viewing. Downloading a PDF or
/// opening a poster is how a draft gets reviewed in the first place, so those
/// stay open.
/// </para>
/// </remarks>
public static class PosterUsage
{
    /// <summary>
    /// Null when the poster may be sent out, or the reason it may not.
    /// </summary>
    /// <param name="poster">The poster, already confirmed to belong to the caller.</param>
    /// <param name="action">
    /// What was being attempted, in a form that completes "before it can be
    /// ..." — e.g. "sent in a campaign". Named rather than generic because
    /// "this poster is a draft" tells somebody what is true, and not what to do
    /// about it.
    /// </param>
    public static string? WhyCannotSend(Poster poster, string action) => poster.Status switch
    {
        PosterStatus.Published => null,

        PosterStatus.Draft =>
            $"'{poster.Name}' is still a draft. Publish it before it can be {action}.",

        // Archived is a decision somebody already made about this poster, so
        // the wording says how to undo it rather than treating it as an error.
        PosterStatus.Archived =>
            $"'{poster.Name}' has been archived. Restore it to a draft and publish it "
            + $"before it can be {action}.",

        // A status that does not exist yet. Refusing is the safe direction: the
        // failure is somebody being told to publish, not a patient receiving
        // something nobody meant to send.
        _ => $"'{poster.Name}' cannot be {action} while it is {poster.Status}.",
    };

    /// <summary>True when this poster may be sent out.</summary>
    public static bool CanSend(Poster poster) => poster.Status == PosterStatus.Published;
}
