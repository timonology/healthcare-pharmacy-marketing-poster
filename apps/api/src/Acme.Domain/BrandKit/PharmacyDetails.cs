using Acme.Domain.Common;

namespace Acme.Domain.BrandKit;

public readonly record struct PharmacyDetails
{
    public string Name { get; }

    /// <summary>
    /// The pharmacy's registration number, or "" if it has not given one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Optional, and it had to become optional.</b> It was required, which
    /// meant a brand kit could only exist for a pharmacy that had typed one in.
    /// That was tolerable while the kit held only design choices. It stopped
    /// being tolerable when the booking link moved here, because pharmacies that
    /// had set a booking link on their profile and never opened the brand kit
    /// had nowhere for that link to go — and inventing a registration number to
    /// give them one is not something to do to a regulated field.
    /// </para>
    /// <para>
    /// Nothing downstream ever required it: <see cref="PharmacyBrand"/> already
    /// resolves it to "" when absent, and the poster footer omits an empty one.
    /// So the requirement was enforced at the door and nowhere else.
    /// </para>
    /// </remarks>
    public string LicenseNumber { get; }

    public string Phone { get; }
    public string Address { get; }

    /// <summary>
    /// Where a patient books an appointment, normalised, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here, and only here.</b> It lived on the pharmacy profile first, beside
    /// the address and the phone number, on the reasoning that it is the same
    /// kind of thing — a contact detail a patient acts on. That reasoning was
    /// sound and the placement still turned out wrong: the link is what a QR code
    /// on a poster encodes, posters are laid out from the brand kit, and a value
    /// that a designer needs living in the form an owner fills in once during
    /// onboarding is a value nobody knows where to change.
    /// </para>
    /// <para>
    /// The profile still shows it. It does not store it — there is one copy, and
    /// this is it.
    /// </para>
    /// </remarks>
    public string? BookingUrl { get; }

    private PharmacyDetails(
        string name, string licenseNumber, string phone, string address, string? bookingUrl)
    {
        Name = name;
        LicenseNumber = licenseNumber;
        Phone = phone;
        Address = address;
        BookingUrl = bookingUrl;
    }

    public static PharmacyDetails Create(
        string name,
        string? licenseNumber,
        string phone,
        string address,
        string? bookingUrl = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Pharmacy name is required.");
        if (name.Length > 200) throw new DomainException("Name too long.");
        if (licenseNumber?.Length > 80) throw new DomainException("License too long.");

        return new PharmacyDetails(
            name.Trim(),
            licenseNumber?.Trim() ?? string.Empty,
            phone?.Trim() ?? string.Empty,
            address?.Trim() ?? string.Empty,
            BookingLink.Normalize(bookingUrl));
    }
}
