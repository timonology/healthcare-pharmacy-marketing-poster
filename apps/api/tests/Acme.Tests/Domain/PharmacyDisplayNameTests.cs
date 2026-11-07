using Acme.Domain.Users;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Domain;

/// <summary>
/// What a pharmacy is called in an email.
/// </summary>
/// <remarks>
/// <para>
/// Written after <c>Profile.PharmacyName ?? DisplayName</c> — the same line, in
/// three files — turned out never to fall through. <c>PharmacyName</c> is a
/// non-nullable string and the repository hydrates a missing one as
/// <c>string.Empty</c>; <c>??</c> tests for null, not for emptiness. Every
/// account that had not finished onboarding therefore sent campaigns with the
/// subject <c>"Flu shots · "</c> and no sender name in the inbox.
/// </para>
/// <para>
/// The empty-string case is the whole point of this file. A test that only
/// covered null would have passed against the broken code.
/// </para>
/// </remarks>
public class PharmacyDisplayNameTests
{
    private static User WithNames(string pharmacyName, string displayName)
    {
        var user = User.Register(
            Email.Create("someone@example.test"),
            displayName,
            PasswordHash.FromHash("hash"));

        user.SetProfile(user.Profile with { PharmacyName = pharmacyName });
        return user;
    }

    [Fact]
    public void The_pharmacy_name_is_used_when_it_is_set()
    {
        WithNames("Riverside Pharmacy", "Aisha Rahman")
            .PharmacyDisplayName.Should().Be("Riverside Pharmacy");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_pharmacy_name_falls_back_to_the_person(string pharmacyName)
    {
        // The regression. An account that has registered but not onboarded has
        // exactly this shape, and it is the majority of accounts on their
        // first day.
        WithNames(pharmacyName, "Aisha Rahman")
            .PharmacyDisplayName.Should().Be(
                "Aisha Rahman",
                "an empty name is not a name — ?? does not catch this, which is why " +
                "campaign subjects read \"Flu shots · \" with nothing after them");
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        WithNames("  Oakfield Chemist  ", "Daniel Achebe")
            .PharmacyDisplayName.Should().Be("Oakfield Chemist");
    }

    [Fact]
    public void A_freshly_registered_account_still_has_a_name()
    {
        // The exact shape of the bug: registered, never onboarded. Register()
        // sets Profile to PharmacyProfile.Empty(), whose PharmacyName is an
        // empty string — so this is the default state of every new account,
        // not an edge case that needed constructing.
        var user = User.Register(
            Email.Create("fresh@example.test"), "Priya Nair", PasswordHash.FromHash("hash"));

        user.Profile.PharmacyName.Should().BeEmpty(
            "this is the premise — if Empty() ever starts returning null, the ?? " +
            "idiom this replaced would have worked and this test proves nothing");

        user.PharmacyDisplayName.Should().Be("Priya Nair");
    }

    // Note: the final "Pharmacy" fallback is not tested here because it cannot
    // be reached through the public API — Register and Rename both reject a
    // blank display name. It guards hydration from the database, where a legacy
    // row could carry one, and testing that would mean asserting on the
    // reflection-based mapper rather than on behaviour.
}
