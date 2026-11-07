namespace Acme.Infrastructure.Social;

/// <summary>
/// Encryption keys for stored platform credentials.
/// </summary>
/// <remarks>
/// <para>
/// A list rather than a single key, because rotation is the whole reason this
/// is versioned. Add a new key with a higher version, point
/// <see cref="ActiveVersion"/> at it, and new writes use it while existing
/// records stay readable through their own version. Once nothing references the
/// old version it can be removed.
/// </para>
/// <para>
/// Deleting a key that records still reference does not fail loudly at startup
/// — it fails at the moment a pharmacy's scheduled post tries to publish. So
/// <c>TokenCipher</c> validates what it can at construction and says plainly
/// which version is missing when it cannot.
/// </para>
/// </remarks>
public sealed class TokenCipherOptions
{
    public const string SectionName = "Social:Encryption";

    /// <summary>Version used for new encryptions. Must exist in <see cref="Keys"/>.</summary>
    public int ActiveVersion { get; set; } = 1;

    public List<TokenKey> Keys { get; set; } = new();
}

/// <param name="Version">Monotonic. Stored on every encrypted record.</param>
/// <param name="Key">
/// Base64 of exactly 32 bytes. Generate with:
/// <c>openssl rand -base64 32</c>
/// </param>
public sealed class TokenKey
{
    public int Version { get; set; }
    public string Key { get; set; } = string.Empty;
}
