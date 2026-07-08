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
    public static PharmacyProfile Empty() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, false);

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
