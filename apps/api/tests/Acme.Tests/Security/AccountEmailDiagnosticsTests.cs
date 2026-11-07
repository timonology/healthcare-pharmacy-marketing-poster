using System.Net;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Acme.WebApi;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// The pieces that make "reset emails are not arriving" answerable.
/// </summary>
/// <remarks>
/// Reported from production. The reset request answers the same thing whatever
/// happens — that is what stops it being an enumeration oracle — so nothing
/// recorded whether the email went, and a rate-limit refusal left no trace.
/// </remarks>
public class AccountEmailDiagnosticsTests
{
    [Theory]
    [InlineData(AuthToken.EmailAccepted)]
    [InlineData(AuthToken.EmailRejected)]
    [InlineData(AuthToken.EmailError)]
    [InlineData(AuthToken.EmailMocked)]
    public void A_token_records_what_the_provider_said(string outcome)
    {
        var (token, _) = AuthToken.Issue("u1", "a@example.test", AuthTokenPurpose.PasswordReset, TimeSpan.FromHours(1));
        token.EmailOutcome.Should().BeNull("nothing has been sent yet");

        token.RecordEmailOutcome(outcome);

        token.EmailOutcome.Should().Be(outcome);
    }

    [Fact]
    public void An_unknown_outcome_is_refused()
    {
        var (token, _) = AuthToken.Issue("u1", "a@example.test", AuthTokenPurpose.PasswordReset, TimeSpan.FromHours(1));

        var act = () => token.RecordEmailOutcome("probably fine");

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.x")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.x")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2:…")]
    public void A_refused_address_is_logged_truncated(string address, string expected) =>
        RateLimits.Truncated(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void No_address_is_logged_as_unknown() =>
        RateLimits.Truncated(null).Should().Be("unknown");
}
