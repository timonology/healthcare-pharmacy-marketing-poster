using Acme.Domain.Users;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Users;

/// <summary>
/// A staff account is not a pharmacy, and must not be asked to become one.
/// </summary>
/// <remarks>
/// <para>
/// Onboarding collects a pharmacy name, address, postcode and contact number.
/// The middleware treats an unfinished profile as a hard redirect on every
/// protected path, so a staff account created with
/// <c>OnboardingCompleted: false</c> cannot reach the admin console at all —
/// it is sent to a form asking for details it will never have.
/// </para>
/// <para>
/// <b>This is a first-run bug, which is why it hid for so long.</b> The
/// super-admin on any environment that had been up for a while had been through
/// the form by hand once, and the flag stayed true forever after. It only
/// reappears when an environment is built from nothing: a database recreated
/// after corruption, a new deployment, a CI run against an empty server. All
/// twenty superadmin browser tests failed the first time that happened, every
/// one of them landing on <c>/onboarding</c>.
/// </para>
/// </remarks>
public class StaffAccountOnboardingTests
{
    private static PasswordHash SomeHash() => PasswordHash.FromHash("$2a$11$abcdefghijklmnopqrstuv");

    [Theory]
    [InlineData(UserRole.SuperAdmin)]
    [InlineData(UserRole.Admin)]
    public void A_staff_account_has_nothing_to_onboard(UserRole role)
    {
        var staff = User.CreateStaff(
            Email.Create("staff@example.test"), "Platform Staff", SomeHash(), role);

        staff.Profile.OnboardingCompleted.Should().BeTrue(
            "a {0} has no pharmacy to describe. Leaving this false sends them to a form "
            + "asking for a pharmacy address, and the middleware keeps sending them back "
            + "until they fill it in — so the admin console is unreachable on a fresh "
            + "environment", role);
    }

    [Fact]
    public void A_staff_profile_carries_no_invented_pharmacy_details()
    {
        // Complete, not populated. Marking it finished must not put a fake
        // pharmacy name or address on the account — those reach patients
        // through campaign email and printed posters.
        var staff = User.CreateStaff(
            Email.Create("staff2@example.test"), "Platform Staff", SomeHash(), UserRole.SuperAdmin);

        staff.Profile.PharmacyName.Should().BeEmpty();
        staff.Profile.Address.Should().BeEmpty();
        staff.Profile.PostCode.Should().BeEmpty();
        staff.Profile.ContactName.Should().BeEmpty();
        staff.Profile.ContactPhone.Should().BeEmpty();
        staff.Profile.SonarFCode.Should().BeNull();
    }

    [Fact]
    public void A_pharmacy_that_registers_still_has_to_onboard()
    {
        // The other half of the rule, and the one worth guarding. Onboarding is
        // how a pharmacy's name and address get onto its posters and into the
        // From line of its campaigns; skipping it for everybody would send
        // patients mail from a blank sender.
        var pharmacy = User.Register(
            Email.Create("pharmacy@example.test"), "Riverside Pharmacy", SomeHash());

        pharmacy.Profile.OnboardingCompleted.Should().BeFalse(
            "a pharmacy has real details to supply, and the app has to ask for them");
    }
}
