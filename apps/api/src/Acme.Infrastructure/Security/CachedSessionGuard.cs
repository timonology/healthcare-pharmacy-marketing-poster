using Acme.Application.Auth;
using Acme.Domain.Users;
using Microsoft.Extensions.Caching.Memory;

namespace Acme.Infrastructure.Security;

/// <summary>
/// <see cref="ISessionGuard"/> with each account's state cached briefly.
/// </summary>
/// <remarks>
/// Every authenticated request asks, so the answer is cached per account for
/// <see cref="CacheFor"/> — one database read per active account per half
/// minute per instance. The instance that makes a change forgets the account
/// at once (<see cref="Forget"/>); any other instance catches up within that
/// half minute — against the token lifetime it used to be.
/// </remarks>
public sealed class CachedSessionGuard(IUserRepository users, IMemoryCache cache) : ISessionGuard
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private sealed record State(bool Exists, bool IsActive, bool CanSignIn, DateTime? ValidAfterUtc);

    private static string Key(string userId) => "session-state:" + userId;

    public void Forget(string userId) => cache.Remove(Key(userId));

    public async Task<string?> WhyEndedAsync(
        string userId, DateTime issuedAtUtc, SessionParty party, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "no subject";

        var key = Key(userId);
        if (!cache.TryGetValue(key, out State? state) || state is null)
        {
            var user = await users.FindByIdAsync(userId, ct);
            state = user is null
                ? new State(false, false, false, null)
                : new State(true, user.IsActive, user.CanSignIn, user.SessionsValidAfterUtc);
            cache.Set(key, state, CacheFor);
        }

        if (!state.Exists) return "account not found";

        // The pharmacy being worked as need not be able to sign in: an invited
        // branch has no login of its own yet is a real workspace. It must not
        // be deactivated — the switch's own rule.
        if (party == SessionParty.WorkedAs)
        {
            if (!state.IsActive) return "pharmacy deactivated";
        }
        else if (!state.CanSignIn) return "account cannot sign in";

        // For both parties. Deactivation ends sessions and reactivation does
        // not restore them, so without this a token issued before a
        // deactivation would come back to life if the pharmacy were
        // reactivated within its hour.
        if (state.ValidAfterUtc is { } after && issuedAtUtc < after) return "session ended";
        return null;
    }
}
