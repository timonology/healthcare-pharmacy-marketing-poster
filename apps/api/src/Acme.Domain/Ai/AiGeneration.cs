using Acme.Domain.Common;

namespace Acme.Domain.Ai;

/// <summary>What a pharmacy asked a model to make.</summary>
public enum AiGenerationKind
{
    /// <summary>A poster design produced from a written brief.</summary>
    PosterDesign = 0,
}

/// <summary>
/// One billable call to a generative model, recorded so it can be counted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a record per call.</b> <c>MaxAiGenerationsPerMonth</c> has been on the
/// pricing page since the beginning — 3 on Free, 50 on Starter, unlimited on Pro
/// — and was never enforced, because there was nothing to count. The nearest
/// proxy available was "posters created this month from an AI-generated
/// template", which misses every design a pharmacist generated and discarded:
/// exactly the ones that cost money and produced nothing.
/// </para>
/// <para>
/// So the unit being counted is the call, not the artefact. A design the
/// pharmacist throws away still consumed the model time we paid for.
/// </para>
/// <para>
/// <b>Written before the call, marked when it succeeds.</b> One record per
/// attempt, carrying whether it worked.
/// </para>
/// <para>
/// This started as "written only on success", on the reasoning that a failed
/// call should not be charged to the pharmacist. That half is still true and
/// <see cref="Succeeded"/> is what keeps it true — the allowance counts
/// successes. The other half was wrong, and wrong in the expensive direction:
/// the per-minute rate limit counts these same records, so a run of failures
/// wrote nothing, the limiter read zero, and a loop that failed every time was
/// not rate-limited at all. Every one of those failures still cost a model
/// call. The protection was asserted in this comment and absent from the code.
/// </para>
/// <para>
/// So: the allowance counts what worked, the rate limit counts what was
/// attempted, and a process that dies mid-call leaves an attempt that is
/// rate-limited but never billed — which is the safe direction for both sides.
/// </para>
/// <para>
/// The prompt is kept, shortened. Support cannot answer "why did my allowance
/// go down" from a bare timestamp, and the whole point of this record is to be
/// able to answer that.
/// </para>
/// </remarks>
public sealed class AiGeneration : Entity
{
    public string OwnerId { get; private set; } = default!;
    public AiGenerationKind Kind { get; private set; }

    /// <summary>The brief, shortened for support — never the whole essay.</summary>
    public string Prompt { get; private set; } = string.Empty;

    /// <summary>What the model called the result, for recognising it later.</summary>
    public string ResultName { get; private set; } = string.Empty;

    /// <summary>
    /// True once the model returned a design. Only these count against the
    /// monthly allowance; every attempt counts against the rate limit.
    /// </summary>
    public bool Succeeded { get; private set; }

    /// <summary>How long the prompt was before shortening.</summary>
    public const int MaxStoredPromptLength = 200;

    private AiGeneration() { }

    /// <summary>Records an attempt, before the model is called.</summary>
    public static AiGeneration Attempt(
        string ownerId,
        AiGenerationKind kind,
        string? prompt,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");

        return new AiGeneration
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Kind = kind,
            Prompt = Shorten(prompt),
            ResultName = string.Empty,
            Succeeded = false,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    /// <summary>Marks this attempt as having produced a design.</summary>
    public void Succeed(string? resultName)
    {
        ResultName = (resultName ?? string.Empty).Trim();
        Succeeded = true;
        Touch();
    }

    private static string Shorten(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= MaxStoredPromptLength ? t : t[..MaxStoredPromptLength] + "…";
    }
}
