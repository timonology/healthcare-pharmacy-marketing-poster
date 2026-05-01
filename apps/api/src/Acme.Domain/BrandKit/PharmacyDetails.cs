using Acme.Domain.Common;

namespace Acme.Domain.BrandKit;

public readonly record struct PharmacyDetails
{
    public string Name { get; }
    public string LicenseNumber { get; }
    public string Phone { get; }
    public string Address { get; }

    private PharmacyDetails(string name, string licenseNumber, string phone, string address)
    {
        Name = name;
        LicenseNumber = licenseNumber;
        Phone = phone;
        Address = address;
    }

    public static PharmacyDetails Create(
        string name,
        string licenseNumber,
        string phone,
        string address)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Pharmacy name is required.");
        if (string.IsNullOrWhiteSpace(licenseNumber))
            throw new DomainException("License number is required.");
        if (name.Length > 200) throw new DomainException("Name too long.");
        if (licenseNumber.Length > 80) throw new DomainException("License too long.");

        return new PharmacyDetails(
            name.Trim(),
            licenseNumber.Trim(),
            phone?.Trim() ?? string.Empty,
            address?.Trim() ?? string.Empty);
    }
}
