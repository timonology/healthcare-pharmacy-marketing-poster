using Acme.Domain.Users;

namespace Acme.Application.Auth;

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(Email email, CancellationToken ct);
    Task<User?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task UpdateAsync(User user, CancellationToken ct);
}
