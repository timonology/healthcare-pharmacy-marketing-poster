using Acme.Application.Auth;
using Microsoft.Extensions.Caching.Memory;

namespace Acme.Infrastructure.Security;

/// <summary>The counters of <see cref="ILoginThrottle"/>, in memory.</summary>
/// <remarks>
/// Per instance: with two API instances an attacker gets twice the tries,
/// which still turns millions of guesses into dozens. Known-good addresses are
/// forgotten on restart, which errs towards the owner being treated as a
/// stranger for one window — never towards a stranger being treated as the
/// owner. Emails are normalised so "A@x.com" and " a@x.com" count together.
/// </remarks>
public sealed class MemoryLoginThrottle(IMemoryCache cache) : ILoginThrottle
{
    private sealed class Counter
    {
        public int Failures;
        public readonly DateTime StartedUtc = DateTime.UtcNow;
    }

    private static string Account(string email) => email.Trim().ToLowerInvariant();
    private static string AddressPart(string? address) => string.IsNullOrWhiteSpace(address) ? "-" : address.Trim();

    private static string AddressKey(string email, string? address) => $"login-fail:{Account(email)}|{AddressPart(address)}";
    private static string AccountKey(string email) => $"login-fail-all:{Account(email)}";
    private static string KnownKey(string email, string? address) => $"login-ok:{Account(email)}|{AddressPart(address)}";

    // When a password reset last cleared the account. A per-address counter
    // that started before it no longer counts — without having to know which
    // addresses the counters were keyed on. Kept slightly longer than any
    // counter lives, so every counter it would have to ignore has expired by
    // the time it lapses.
    private static string ClearedKey(string email) => $"login-cleared:{Account(email)}";

    public bool IsLocked(string email, string? address)
    {
        if (Failures(email, AddressKey(email, address)) >= ILoginThrottle.MaxFailuresPerAddress)
            return true;

        return Failures(email, AccountKey(email)) >= ILoginThrottle.MaxFailuresPerAccount
               && !cache.TryGetValue(KnownKey(email, address), out _);
    }

    public void RecordFailure(string email, string? address)
    {
        Increment(email, AddressKey(email, address), ILoginThrottle.AddressWindow);
        Increment(email, AccountKey(email), ILoginThrottle.AccountWindow);
    }

    public void RecordSuccess(string email, string? address)
    {
        cache.Remove(AddressKey(email, address));
        // Only a real address is remembered: "unknown" must never become a
        // pass that every unattributed request shares.
        if (!string.IsNullOrWhiteSpace(address))
            cache.Set(KnownKey(email, address), true,
                new MemoryCacheEntryOptions { SlidingExpiration = ILoginThrottle.KnownAddressFor });
    }

    public void Clear(string email)
    {
        cache.Remove(AccountKey(email));
        cache.Set(ClearedKey(email), DateTime.UtcNow,
            (ILoginThrottle.AddressWindow > ILoginThrottle.AccountWindow
                ? ILoginThrottle.AddressWindow
                : ILoginThrottle.AccountWindow) + TimeSpan.FromMinutes(1));
    }

    private bool IsStale(string email, Counter counter) =>
        cache.TryGetValue(ClearedKey(email), out DateTime cleared) && counter.StartedUtc <= cleared;

    private int Failures(string email, string key) =>
        cache.TryGetValue(key, out Counter? c) && c is not null && !IsStale(email, c)
            ? Volatile.Read(ref c.Failures)
            : 0;

    private void Increment(string email, string key, TimeSpan window)
    {
        if (!cache.TryGetValue(key, out Counter? counter) || counter is null || IsStale(email, counter))
        {
            // Fixed from the first failure: the lock lifts a window after the
            // run began, not after the attacker's latest try.
            counter = new Counter();
            cache.Set(key, counter, window);
        }
        Interlocked.Increment(ref counter.Failures);
    }
}
