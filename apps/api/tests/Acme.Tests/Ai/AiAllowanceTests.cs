using Acme.Application.Ai;
using Acme.Application.Subscriptions;
using Acme.Domain.Ai;
using Acme.Domain.Common;
using Acme.Domain.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Ai;

/// <summary>
/// The allowance the pricing page has always advertised, now enforced.
/// </summary>
/// <remarks>
/// <para>
/// <c>MaxAiGenerationsPerMonth</c> — 3 on Free, 50 on Starter, unlimited on Pro
/// — was printed on the pricing page from the beginning and checked nowhere.
/// These tests are the check, and they are written against the plan table
/// rather than against literal numbers wherever the number is the plan's to
/// decide, so changing a price does not mean editing assertions in two places.
/// </para>
/// <para>
/// The boundary cases are the ones that matter. Off by one here is either a
/// pharmacy refused something it paid for, or an unbilled generation every
/// month for every account.
/// </para>
/// </remarks>
public class AiAllowanceTests
{
    [Theory]
    [InlineData(SubscriptionTier.Free)]
    [InlineData(SubscriptionTier.Starter)]
    [InlineData(SubscriptionTier.Pro)]
    public void A_pharmacy_that_has_used_nothing_may_generate(SubscriptionTier tier)
    {
        AiAllowance.Refuse(Plans.For(tier), usedThisMonth: 0).Should().BeNull(
            "every advertised plan includes at least some AI designs");
    }

    [Theory]
    [InlineData(SubscriptionTier.Free)]
    [InlineData(SubscriptionTier.Starter)]
    public void The_last_included_design_is_allowed_and_the_next_is_not(SubscriptionTier tier)
    {
        var plan = Plans.For(tier);
        var cap = plan.MaxAiGenerationsPerMonth;

        cap.Should().BeGreaterThan(0, "this test is about finite plans");

        AiAllowance.Refuse(plan, cap - 1).Should().BeNull(
            "a pharmacy on {0} that has used {1} of {2} has one left",
            plan.DisplayName, cap - 1, cap);

        AiAllowance.Refuse(plan, cap).Should().NotBeNull(
            "the {0}th design is the one past the {1} advertised", cap + 1, cap);
    }

    [Fact]
    public void Pro_is_never_refused_however_many_it_has_used()
    {
        var pro = Plans.For(SubscriptionTier.Pro);
        pro.MaxAiGenerationsPerMonth.Should().Be(Plans.Unlimited);

        foreach (var used in new[] { 0, 1, 500, 50_000 })
            AiAllowance.Refuse(pro, used).Should().BeNull();
    }

    [Fact]
    public void A_plan_with_no_allowance_is_told_to_upgrade_not_to_wait()
    {
        // No tier is currently zero, and the branch still has to be right: a
        // future plan with none, or a plan table edit, must produce "upgrade"
        // rather than "you've used them all", which would be a lie.
        var none = Plans.For(SubscriptionTier.Free) with { MaxAiGenerationsPerMonth = 0 };

        var why = AiAllowance.Refuse(none, usedThisMonth: 0);

        why.Should().NotBeNull();
        why.Should().Contain("Upgrade");
        why.Should().NotContain("used all",
            "a pharmacy that was never given any has not used them");
    }

    [Fact]
    public void The_refusal_names_the_plan_and_the_number()
    {
        var starter = Plans.For(SubscriptionTier.Starter);

        var why = AiAllowance.Refuse(starter, starter.MaxAiGenerationsPerMonth)!;

        why.Should().Contain(starter.DisplayName);
        why.Should().Contain(starter.MaxAiGenerationsPerMonth.ToString());
        why.Should().Contain("resets",
            "somebody refused deserves to know it is temporary");
    }

    [Fact]
    public void Remaining_never_goes_negative_after_a_downgrade()
    {
        // Pro → Starter mid-month, with 60 designs already used against a cap
        // of 50. The allowance is gone; the number shown must be 0, not -10.
        var starter = Plans.For(SubscriptionTier.Starter);

        AiAllowance.Remaining(starter, usedThisMonth: 60).Should().Be(0);
        AiAllowance.Remaining(starter, usedThisMonth: 0)
            .Should().Be(starter.MaxAiGenerationsPerMonth);
        AiAllowance.Remaining(Plans.For(SubscriptionTier.Pro), 9_999)
            .Should().BeNull("unlimited has no remainder to report");
    }

    /* ── the rate limit ──────────────────────────────────────────────── */

    [Fact]
    public void A_burst_is_refused_but_a_normal_retry_is_not()
    {
        const int max = PosterAiService.MaxPerWindow;

        AiAllowance.RefuseForRate(0, max).Should().BeNull();
        AiAllowance.RefuseForRate(1, max).Should().BeNull(
            "pressing try-another once is normal behaviour, not abuse");
        AiAllowance.RefuseForRate(max - 1, max).Should().BeNull();
        AiAllowance.RefuseForRate(max, max).Should().NotBeNull();
    }

    [Fact]
    public void The_rate_refusal_does_not_tell_a_paying_customer_to_upgrade()
    {
        // It is mapped to Invalid rather than Forbidden for this reason, and
        // the wording has to agree with that choice: a Pro account on an
        // unlimited plan can hit this, and sending them to the pricing page
        // would be both useless and insulting.
        var why = AiAllowance.RefuseForRate(PosterAiService.MaxPerWindow, PosterAiService.MaxPerWindow)!;

        why.Should().NotContain("Upgrade");
        why.Should().NotContain("plan");
        why.Should().Contain("minute", "the remedy is waiting, and it should say so");
    }

    /* ── the record that makes counting possible ─────────────────────── */

    [Fact]
    public void An_attempt_keeps_a_shortened_prompt_for_support()
    {
        var essay = new string('a', AiGeneration.MaxStoredPromptLength + 500);

        var record = AiGeneration.Attempt(
            "owner-1", AiGenerationKind.PosterDesign, essay, DateTime.UtcNow);

        record.Prompt.Length.Should().BeLessThan(essay.Length);
        record.Prompt.Should().StartWith("aaa");
        record.OwnerId.Should().Be("owner-1");
    }

    [Fact]
    public void An_attempt_is_not_charged_until_it_succeeds()
    {
        // The distinction the rate limit depends on. An attempt exists from the
        // moment the call starts — so a loop of failures is limited — and only
        // becomes billable when a design comes back.
        var attempt = AiGeneration.Attempt(
            "owner-1", AiGenerationKind.PosterDesign, "a poster about flu jabs",
            DateTime.UtcNow);

        attempt.Succeeded.Should().BeFalse("nothing has come back yet");
        attempt.ResultName.Should().BeEmpty();

        attempt.Succeed("Flu clinic");

        attempt.Succeeded.Should().BeTrue();
        attempt.ResultName.Should().Be("Flu clinic");
    }

    [Fact]
    public void An_attempt_without_an_owner_is_refused()
    {
        // The owner is the only reason this record exists — an unattributed
        // generation cannot be counted against anybody's allowance, so it is
        // worse than no record at all: it is cost with no home.
        var act = () => AiGeneration.Attempt(
            "", AiGenerationKind.PosterDesign, "a poster about flu", DateTime.UtcNow);

        act.Should().Throw<DomainException>();
    }

    /* ── the assertion that makes the allowance unavoidable ──────────── */

    [Fact]
    public void A_quota_assertion_is_bound_to_the_user_the_purpose_and_the_minute()
    {
        // Without this binding the assertion would be a bearer token for
        // spending: one pharmacy's could authorise another's generation, or a
        // poster-design assertion could be replayed against a costlier purpose.
        AiQuotaAssertion.Configure("a-test-secret-at-least-32-characters-long");

        var now = DateTimeOffset.UtcNow;
        var mine = AiQuotaAssertion.Sign("owner-1", AiQuotaAssertion.PosterDesignPurpose, now);

        mine.Should().NotBeNullOrWhiteSpace();
        mine.Should().Be(
            AiQuotaAssertion.Sign("owner-1", AiQuotaAssertion.PosterDesignPurpose, now),
            "the same inputs must sign identically, or ai-service cannot verify it");

        mine.Should().NotBe(
            AiQuotaAssertion.Sign("owner-2", AiQuotaAssertion.PosterDesignPurpose, now),
            "another pharmacy's assertion must not authorise this one's spend");

        mine.Should().NotBe(
            AiQuotaAssertion.Sign("owner-1", "video-refine", now),
            "an assertion for one kind of work must not authorise another");

        mine.Should().NotBe(
            AiQuotaAssertion.Sign("owner-1", AiQuotaAssertion.PosterDesignPurpose,
                now.AddMinutes(2)),
            "an assertion must go stale, or a captured one is a standing licence");
    }

    [Fact]
    public void An_assertion_is_url_safe_because_it_travels_in_a_header()
    {
        AiQuotaAssertion.Configure("a-test-secret-at-least-32-characters-long");

        var signed = AiQuotaAssertion.Sign(
            "owner-1", AiQuotaAssertion.PosterDesignPurpose, DateTimeOffset.UtcNow)!;

        signed.Should().NotContain("+").And.NotContain("/").And.NotContain("=",
            "base64url, so no header encoding rules apply and ai-service's " +
            "urlsafe_b64decode agrees with it");
    }

    /* ── the billing month every allowance shares ────────────────────── */

    [Fact]
    public void The_billing_month_starts_at_midnight_utc_on_the_first()
    {
        var mid = new DateTime(2026, 3, 17, 14, 32, 9, DateTimeKind.Utc);

        BillingMonth.StartUtcFor(mid).Should().Be(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_local_moment_is_converted_rather_than_truncated()
    {
        // The hazard this guards: 00:30 on 1 March in a UTC+2 zone is still
        // February in UTC. Truncating the local value would hand that pharmacy
        // a fresh allowance a few hours early, every month, on one server only.
        var justAfterLocalMidnight = new DateTimeOffset(
            2026, 3, 1, 0, 30, 0, TimeSpan.FromHours(2)).DateTime;

        var asLocal = DateTime.SpecifyKind(justAfterLocalMidnight, DateTimeKind.Local);

        BillingMonth.StartUtcFor(asLocal).Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Every_month_boundary_lands_on_the_first()
    {
        for (var month = 1; month <= 12; month++)
        {
            var any = new DateTime(2026, month, 28, 23, 59, 59, DateTimeKind.Utc);
            var start = BillingMonth.StartUtcFor(any);

            start.Day.Should().Be(1);
            start.Month.Should().Be(month);
            start.TimeOfDay.Should().Be(TimeSpan.Zero);
        }
    }
}
