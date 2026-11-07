using Acme.Domain.Common;

namespace Acme.Domain.Users;

public sealed record PharmacyProfile(
    string PharmacyName,
    string Address,
    string PostCode,
    string Description,
    string ContactName,
    string ContactPhone,
    string? SonarFCode,
    bool OnboardingCompleted)
{
    // The booking link used to be here, as the last positional member, on the
    // reasoning that it is the same kind of thing as the address and the phone
    // number. It now lives on the brand kit — see PharmacyDetails.BookingUrl for
    // why it moved and BookingLinkToBrandKitMigration for how existing ones got
    // there. The profile still displays it; it does not hold it.
    //
    // Nothing here should gain a BookingUrl again. Two records holding one
    // patient-facing URL is how a pharmacy ends up with a printed QR code
    // pointing somewhere its own profile page disagrees with.

    public static PharmacyProfile Empty() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, false);

    /// <summary>
    /// The profile a staff account gets: blank, and finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Onboarding asks for a pharmacy name, address, postcode and contact
    /// number. A platform administrator has none of those, so
    /// <see cref="Empty"/> — which is <c>OnboardingCompleted: false</c> — makes
    /// the app demand something they cannot supply, and the middleware bounces
    /// them out of every protected page until they do. That includes the whole
    /// admin console.
    /// </para>
    /// <para>
    /// It went unnoticed because the super-admin on any long-lived environment
    /// had been through the form by hand once, months earlier. It reappears the
    /// moment an environment is built fresh: seed a super-admin, sign in, and be
    /// held at a pharmacy-setup page with no admin console behind it. Recreating
    /// a corrupted development database is what surfaced it, but a first
    /// production deploy would have hit exactly the same wall.
    /// </para>
    /// <para>
    /// Marked complete rather than exempting staff in the middleware: "a staff
    /// account has no pharmacy to set up" is a fact about the account, and it
    /// belongs where the account is made. A routing exemption would leave the
    /// flag saying false, so the finish-setup prompt would follow them around
    /// the app forever and the admin list would report them as unfinished.
    /// </para>
    /// </remarks>
    public static PharmacyProfile ForStaff() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, true);

    public static PharmacyProfile Create(
        string pharmacyName,
        string address,
        string postCode,
        string description,
        string contactName,
        string contactPhone,
        string? sonarFCode)
    {
        if (string.IsNullOrWhiteSpace(pharmacyName))
            throw new DomainException("Pharmacy name is required.");
        if (pharmacyName.Length > 200)
            throw new DomainException("Pharmacy name must be 200 chars or fewer.");
        if (string.IsNullOrWhiteSpace(contactName))
            throw new DomainException("Contact name is required.");
        if (string.IsNullOrWhiteSpace(contactPhone))
            throw new DomainException("Contact phone is required.");
        if (description?.Length > 2000)
            throw new DomainException("Description must be 2000 chars or fewer.");
        if (postCode?.Length > 16)
            throw new DomainException("Postcode must be 16 chars or fewer.");

        return new PharmacyProfile(
            pharmacyName.Trim(),
            address?.Trim() ?? string.Empty,
            (postCode ?? string.Empty).Trim().ToUpperInvariant(),
            description?.Trim() ?? string.Empty,
            contactName.Trim(),
            contactPhone.Trim(),
            string.IsNullOrWhiteSpace(sonarFCode) ? null : sonarFCode.Trim().ToUpperInvariant(),
            OnboardingCompleted: true);
    }

}
