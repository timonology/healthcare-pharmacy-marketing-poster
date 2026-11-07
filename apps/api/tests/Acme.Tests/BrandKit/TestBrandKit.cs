using Acme.Domain.BrandKit;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Tests.BrandKit;

/// <summary>
/// Brand kits for tests, built the way the application builds them.
/// </summary>
/// <remarks>
/// <para>
/// One builder rather than a literal in each test file. <c>ResolveForRender</c>
/// used to take a bare <c>string? logoBlobKey</c>, so tests passed a string and
/// nothing had to be constructed. It takes the whole kit now — because the
/// booking link moved onto the kit and passing it separately would have been a
/// fifth argument a call site can forget — and a kit built by hand in four test
/// files is four chances to build a subtly different one.
/// </para>
/// <para>
/// Deliberately goes through <see cref="DomainBrandKit.Create"/> and
/// <see cref="PharmacyDetails.Create"/> rather than reflecting values in. A
/// helper that bypasses the validators would let tests pass with kits the
/// application cannot produce — which is how a test suite ends up green about
/// a shape that never reaches production.
/// </para>
/// </remarks>
internal static class TestBrandKit
{
    public const string OwnerId = "owner-1";

    public static DomainBrandKit With(
        string? logoBlobKey = null,
        string? bookingUrl = null,
        string name = "Riverside Pharmacy",
        string licenseNumber = "GPhC-12345",
        string phone = "",
        string address = "",
        string regulatoryFooter = "")
    {
        var kit = DomainBrandKit.Create(
            OwnerId,
            name,
            BrandColors.Create("#0f766e", "#1e293b", "#f59e0b"),
            PharmacyDetails.Create(name, licenseNumber, phone, address, bookingUrl),
            regulatoryFooter);

        if (!string.IsNullOrWhiteSpace(logoBlobKey)) kit.SetLogo(logoBlobKey);
        return kit;
    }

    /// <summary>A kit whose only interesting property is the booking link.</summary>
    public static DomainBrandKit WithBooking(string? bookingUrl) => With(bookingUrl: bookingUrl);

    /// <summary>A kit whose only interesting property is the logo.</summary>
    public static DomainBrandKit WithLogo(string? logoBlobKey) => With(logoBlobKey: logoBlobKey);
}
