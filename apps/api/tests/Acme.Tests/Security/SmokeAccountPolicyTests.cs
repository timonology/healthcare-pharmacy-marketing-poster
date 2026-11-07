using Acme.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// The rule that decides whether a smoke-test credential may be created here.
/// </summary>
/// <remarks>
/// <para>
/// The smoke account is a known address with a written-down password, seeded
/// automatically on startup. On a laptop that is the point. On a real
/// deployment it is an unattended door into a live tenant, so the interesting
/// question is not whether it works — it is whether the credential shipped in
/// this repository can ever reach production.
/// </para>
/// <para>
/// Needs no host, no database and no repository fake, which is why the rule was
/// pulled out of the seeder in the first place: a guard nobody can test cheaply
/// is a guard that quietly stops being true.
/// </para>
/// </remarks>
public class SmokeAccountPolicyTests
{
    [Fact]
    public void Development_accepts_the_shared_password()
    {
        SmokeAccountPolicy
            .WhyPasswordRefused(SmokeAccountPolicy.PublishedDevPassword, isDevelopment: true)
            .Should().BeNull();
    }

    [Fact]
    public void Outside_development_the_published_password_is_refused()
    {
        // The whole reason this file exists. It is long enough and mixed enough
        // to satisfy a length-and-complexity rule, so only refusing it *by
        // value* actually stops it.
        SmokeAccountPolicy.PublishedDevPassword.Length
            .Should().BeGreaterThanOrEqualTo(SmokeAccountPolicy.MinimumLength,
                "otherwise this test would pass for the wrong reason");

        SmokeAccountPolicy
            .WhyPasswordRefused(SmokeAccountPolicy.PublishedDevPassword, isDevelopment: false)
            .Should().Contain("published");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_password_is_refused_everywhere(string? password)
    {
        SmokeAccountPolicy.WhyPasswordRefused(password, isDevelopment: true).Should().NotBeNull();
        SmokeAccountPolicy.WhyPasswordRefused(password, isDevelopment: false).Should().NotBeNull();
    }

    [Theory]
    [InlineData("short1!")]
    [InlineData("fifteencharsxy!")]
    public void Outside_development_a_short_password_is_refused(string password)
    {
        password.Length.Should().BeLessThan(SmokeAccountPolicy.MinimumLength);

        SmokeAccountPolicy.WhyPasswordRefused(password, isDevelopment: false)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("correct-changeme-horse-battery")]
    [InlineData("MyPasswordIsGreat123")]
    [InlineData("qwerty-admin-1234567")]
    public void Outside_development_an_obvious_word_is_refused_however_long(string password)
    {
        password.Length.Should().BeGreaterThanOrEqualTo(SmokeAccountPolicy.MinimumLength);

        SmokeAccountPolicy.WhyPasswordRefused(password, isDevelopment: false)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_real_password_is_accepted_outside_development()
    {
        SmokeAccountPolicy
            .WhyPasswordRefused("7Jq!vRt2Lz#wEb9Kd4Nx", isDevelopment: false)
            .Should().BeNull();
    }
}
