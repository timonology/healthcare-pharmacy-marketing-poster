using Acme.Application.Subscriptions;

namespace Acme.Application.Ai;

/// <summary>
/// Whether a pharmacy may spend on a model right now, and what to tell them.
/// </summary>
/// <remarks>
/// <para>
/// Pure functions over numbers, deliberately. This is the part that decides
/// money, and it was originally written inline in <see cref="PosterAiService"/>
/// — which takes five dependencies, one of them a concrete service, so testing
/// "what happens on the 50th design of the month" meant constructing a
/// repository, a generator, a brand kit store and a poster service to ask a
/// question about two integers.
/// </para>
/// <para>
/// Separated so the boundaries can be tested exhaustively and read at a glance.
/// The service keeps the I/O; this keeps the rules.
/// </para>
/// </remarks>
public static class AiAllowance
{
    /// <summary>
    /// Why this pharmacy may not generate, or null if it may.
    /// </summary>
    /// <param name="plan">Their plan. <c>Unlimited</c> short-circuits.</param>
    /// <param name="usedThisMonth">Generations already recorded this month.</param>
    /// <remarks>
    /// Three outcomes, three different sentences. "Not on your plan" and "used
    /// them all" are different problems with different fixes, and collapsing
    /// them into one message sends half the people who read it to the wrong
    /// place.
    /// </remarks>
    public static string? Refuse(PlanDefinition plan, int usedThisMonth)
    {
        if (plan.MaxAiGenerationsPerMonth == Plans.Unlimited) return null;

        if (plan.MaxAiGenerationsPerMonth <= 0)
        {
            return $"Designing posters with AI isn't included in the {plan.DisplayName} plan. " +
                   "Upgrade to Starter or Pro to use it.";
        }

        if (usedThisMonth < plan.MaxAiGenerationsPerMonth) return null;

        return $"You've used all {plan.MaxAiGenerationsPerMonth} AI designs on your " +
               $"{plan.DisplayName} plan this month. Your allowance resets on the 1st, " +
               "or upgrade for more.";
    }

    /// <summary>
    /// How many designs are left, or null when the plan is unlimited.
    /// </summary>
    /// <remarks>
    /// Never negative. A plan downgrade can leave a pharmacy above its new cap,
    /// and "-7 designs left" is a number no interface should have to render.
    /// </remarks>
    public static int? Remaining(PlanDefinition plan, int usedThisMonth) =>
        plan.MaxAiGenerationsPerMonth == Plans.Unlimited
            ? null
            : Math.Max(0, plan.MaxAiGenerationsPerMonth - usedThisMonth);

    /// <summary>
    /// Why this pharmacy should wait, or null if it may proceed.
    /// </summary>
    /// <remarks>
    /// Separate from the allowance because it is a different refusal with a
    /// different remedy — waiting, not upgrading — and the caller maps it to a
    /// different status code for exactly that reason.
    /// </remarks>
    public static string? RefuseForRate(int recentCount, int maxPerWindow) =>
        recentCount < maxPerWindow
            ? null
            : "That's a lot of designs at once — give it a minute and try again.";
}
