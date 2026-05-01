using Acme.Domain.Common;

namespace Acme.Domain.BrandKit;

public sealed class BrandKit : Entity
{
    public string OwnerId { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    public string? LogoBlobKey { get; private set; }

    public BrandColors Colors { get; private set; }
    public PharmacyDetails Pharmacy { get; private set; }
    public string RegulatoryFooter { get; private set; } = default!;

    private BrandKit() { }

    public static BrandKit Create(
        string ownerId,
        string name,
        BrandColors colors,
        PharmacyDetails pharmacy,
        string regulatoryFooter)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Brand kit name is required.");
        if (regulatoryFooter is null)
            throw new DomainException("Regulatory footer is required (may be empty string).");
        if (regulatoryFooter.Length > 2000)
            throw new DomainException("Regulatory footer must be 2000 chars or fewer.");

        return new BrandKit
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Name = name.Trim(),
            Colors = colors,
            Pharmacy = pharmacy,
            RegulatoryFooter = regulatoryFooter,
        };
    }

    public void Update(
        string name,
        BrandColors colors,
        PharmacyDetails pharmacy,
        string regulatoryFooter)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Brand kit name is required.");
        if (regulatoryFooter.Length > 2000)
            throw new DomainException("Regulatory footer must be 2000 chars or fewer.");

        Name = name.Trim();
        Colors = colors;
        Pharmacy = pharmacy;
        RegulatoryFooter = regulatoryFooter;
        Touch();
    }

    public void SetLogo(string blobKey)
    {
        if (string.IsNullOrWhiteSpace(blobKey))
            throw new DomainException("Blob key is required.");
        LogoBlobKey = blobKey;
        Touch();
    }

    public void ClearLogo()
    {
        LogoBlobKey = null;
        Touch();
    }
}
