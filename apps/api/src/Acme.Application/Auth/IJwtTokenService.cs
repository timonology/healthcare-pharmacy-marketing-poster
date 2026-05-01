using Acme.Domain.Users;

namespace Acme.Application.Auth;

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresAtUtc) Issue(User user);
}
