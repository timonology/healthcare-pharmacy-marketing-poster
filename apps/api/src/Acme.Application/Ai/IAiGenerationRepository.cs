using Acme.Domain.Ai;

namespace Acme.Application.Ai;

public interface IAiGenerationRepository
{
    /// <summary>Records an attempt, before the model is called.</summary>
    Task AddAsync(AiGeneration generation, CancellationToken ct);

    /// <summary>Marks an attempt as having produced a design.</summary>
    Task MarkSucceededAsync(AiGeneration generation, CancellationToken ct);

    /// <summary>
    /// Generations this pharmacy has made since the billing month began.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every kind, one pool. <c>MaxAiGenerationsPerMonth</c> is advertised as a
    /// single number — "50 AI generations / month" — so counting per kind would
    /// give a pharmacy more than the page promises the moment a second kind
    /// exists. <see cref="AiGeneration.Kind"/> is recorded anyway, so the pool
    /// can be split later without losing the history needed to do it.
    /// </para>
    /// <para>
    /// <b>Successful ones only.</b> A pharmacist is not charged for our outage.
    /// </para>
    /// </remarks>
    Task<int> CountSucceededThisMonthAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// Attempts since <paramref name="sinceUtc"/>, for rate limiting.
    /// </summary>
    /// <remarks>
    /// <b>Every attempt, successful or not.</b> This counted successes once, and
    /// the effect was that a loop which failed every time wrote no records, read
    /// a count of zero, and was never limited — while paying for a model call
    /// each time round. The allowance asks "what did they get"; this asks "how
    /// hard are they hitting us", and those are different questions.
    /// </remarks>
    Task<int> CountAttemptsSinceAsync(string ownerId, DateTime sinceUtc, CancellationToken ct);
}
