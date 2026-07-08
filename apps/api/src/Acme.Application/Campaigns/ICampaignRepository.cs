using Acme.Domain.Campaigns;

namespace Acme.Application.Campaigns;

public interface ICampaignRepository
{
    Task<IReadOnlyList<Campaign>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task<Campaign?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(Campaign campaign, CancellationToken ct);
    Task UpdateAsync(Campaign campaign, CancellationToken ct);
    Task<int> CountRecipientsThisMonthAsync(string ownerId, CancellationToken ct);
}
