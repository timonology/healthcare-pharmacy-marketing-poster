using Acme.Domain.Videos;

namespace Acme.Application.Videos;

/// <summary>What the classifier made of a pharmacist's sentence.</summary>
/// <param name="Stage">
/// Null when the instruction could not be routed — a question, a thank-you,
/// something ambiguous. Deliberately not "fall back to the cheapest stage":
/// that would re-cut identical footage and report success.
/// </param>
/// <param name="Reason">
/// One sentence for the pharmacist. When <paramref name="Stage"/> is null this
/// is the request to rephrase, so it is never empty.
/// </param>
public readonly record struct RefineRouting(RefineStage? Stage, string Reason);

/// <summary>
/// Routes a change request to the earliest pipeline stage that can satisfy it.
/// </summary>
/// <remarks>
/// <para>
/// An interface because the implementation is an HTTP call to the Python
/// ai-service, where the models and the pipeline's own vocabulary already live
/// — and because a test has no business making one.
/// </para>
/// <para>
/// <b>Advisory, not authoritative.</b> Whatever comes back is checked against
/// <see cref="RefineStages"/> on this side before anything is stored or run.
/// The classifier can suggest a stage that costs money; it cannot cause one to
/// be entered.
/// </para>
/// </remarks>
public interface IRefineClassifier
{
    /// <summary>
    /// Never throws. A provider outage returns a null stage with a sentence
    /// saying so, because a refine that cannot be routed is a refine that does
    /// not happen — and the video the pharmacist already has is untouched.
    /// </summary>
    /// <param name="bearerToken">
    /// The caller's own access token, forwarded so ai-service can verify them
    /// independently.
    /// </param>
    /// <remarks>
    /// The token is a parameter rather than something the implementation finds
    /// for itself, matching <c>ITemplateGenerator</c>. ai-service is reachable
    /// inside the cluster and re-checks every caller, so a call without one is
    /// a 401 — and the first version of this had no token at all, which turned
    /// every preview into "we could not work out what to change". A transport
    /// fault wearing the costume of a comprehension failure.
    /// </remarks>
    Task<RefineRouting> ClassifyAsync(
        string instruction, string bearerToken, CancellationToken ct);
}
