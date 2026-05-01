using Acme.Domain.Common;
using Acme.Domain.Users;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Domain;

public class UserTests
{
    [Fact]
    public void Register_creates_user_with_normalized_email()
    {
        var user = User.Register(
            Email.Create("  Foo@Example.COM  "),
            "Foo",
            PasswordHash.FromHash("hash"));

        user.Email.Value.Should().Be("foo@example.com");
        user.DisplayName.Should().Be("Foo");
        user.Id.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public void Email_rejects_invalid_input(string raw)
    {
        var act = () => Email.Create(raw);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Register_rejects_blank_display_name()
    {
        var act = () => User.Register(
            Email.Create("a@b.com"), "  ", PasswordHash.FromHash("h"));
        act.Should().Throw<DomainException>();
    }
}
