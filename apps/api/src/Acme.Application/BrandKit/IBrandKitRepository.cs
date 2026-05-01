using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Application.BrandKit;

public interface IBrandKitRepository
{
    Task<DomainBrandKit?> FindByOwnerAsync(string ownerId, CancellationToken ct);
    Task<DomainBrandKit?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(DomainBrandKit kit, CancellationToken ct);
    Task UpdateAsync(DomainBrandKit kit, CancellationToken ct);
}
