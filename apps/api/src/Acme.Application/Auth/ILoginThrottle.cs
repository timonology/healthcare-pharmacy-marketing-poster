namespace Acme.Application.Auth;

/// <summary>
/// Slows password guessing against an account without letting a stranger
/// lock its owner out (security reviews SEC-05, R2-08).
/// </summary>
/// <remarks>
/// <para>
/// Two counters, because there are two attacks and one counter cannot answer
/// both:
/// </para>
/// <list type="bullet">
///   <item><b>Per account and address</b> — <see cref="MaxFailuresPerAddress"/>
///   in <see cref="AddressWindow"/>. One machine guessing one account is
///   stopped quickly, and stopping it touches nobody else: the owner signing
///   in from their own address is unaffected.</item>
///   <item><b>Per account, any address</b> — <see cref="MaxFailuresPerAccount"/>
///   in <see cref="AccountWindow"/>. A distributed run, a few tries from each
///   of thousands of addresses, passes the first counter, so failures are also
///   totalled per account. When this lock trips it applies only to addresses
///   that have never signed in to the account successfully: the owner's usual
///   addresses keep working, which is what stops the lock itself being the
///   attack.</item>
/// </list>
/// <para>
/// Counted for unknown accounts too, so a lock says nothing about whether an
/// account exists. A completed password reset clears both counters.
/// </para>
/// </remarks>
public interface ILoginThrottle
{
    /// <summary>Whether sign-in for this account from this address is paused.</summary>
    bool IsLocked(string email, string? address);

    void RecordFailure(string email, string? address);

    /// <summary>A successful sign-in: clears this address's counter and remembers it as the owner's.</summary>
    void RecordSuccess(string email, string? address);

    /// <summary>Clears every counter for the account — after a password reset.</summary>
    void Clear(string email);

    public const int MaxFailuresPerAddress = 8;
    public static readonly TimeSpan AddressWindow = TimeSpan.FromMinutes(15);

    public const int MaxFailuresPerAccount = 40;
    public static readonly TimeSpan AccountWindow = TimeSpan.FromHours(1);

    /// <summary>How long an address that signed in successfully stays exempt from the account-wide lock.</summary>
    public static readonly TimeSpan KnownAddressFor = TimeSpan.FromDays(30);
}
