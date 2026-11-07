using System.Security.Cryptography;
using System.Text;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// AES-256-GCM for platform credentials at rest.
/// </summary>
/// <remarks>
/// <para>
/// GCM rather than CBC because these records need <i>authenticated</i>
/// encryption. An attacker who can write to the database but not read the key
/// could, under an unauthenticated mode, flip bits in a stored token and have
/// the application use the result. GCM's tag makes that a decryption failure
/// instead of a silent corruption.
/// </para>
/// <para>
/// <b>The nonce is fresh for every encryption and never reused.</b> This is not
/// a nice-to-have: reusing a nonce under the same key in GCM leaks the XOR of
/// the plaintexts and allows forgery of the authentication tag. It is a total
/// break, not a degradation. <see cref="RandomNumberGenerator"/> is the only
/// source used, and no code path accepts a caller-supplied nonce.
/// </para>
/// <para>
/// Keys are held in a version-keyed dictionary so credentials encrypted under
/// an older key stay readable while new writes use the current one.
/// </para>
/// </remarks>
public sealed class TokenCipher : ITokenCipher
{
    /// <summary>96 bits — the size GCM is specified and optimised for.</summary>
    private const int NonceBytes = 12;

    /// <summary>128 bits. The maximum, and the only size worth using.</summary>
    private const int TagBytes = 16;

    private const int KeyBytes = 32;   // AES-256

    private readonly IReadOnlyDictionary<int, byte[]> _keys;
    private readonly int _activeVersion;

    public TokenCipher(IOptions<TokenCipherOptions> options)
    {
        var value = options.Value;

        // No keys at all is a deployment that has not switched social on yet,
        // not a typo: the cipher reports itself unconfigured, the API refuses
        // to start a connection with a plain reason, and the rest of the app
        // (the social page included) keeps working. Keys that are present but
        // wrong are still refused here, at first use, loudly.
        if (value.Keys.Count == 0)
        {
            _keys = new Dictionary<int, byte[]>();
            _activeVersion = 0;
            return;
        }

        var keys = new Dictionary<int, byte[]>();

        foreach (var entry in value.Keys)
        {
            if (entry.Version < 1)
                throw new InvalidOperationException("Encryption key versions must be 1 or greater.");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(entry.Key);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"Encryption key version {entry.Version} is not valid base64.");
            }

            // Checked at startup rather than at first use. A wrong-length key
            // discovered when a pharmacy connects their Facebook Page is a
            // production incident; discovered at boot it is a config typo.
            if (bytes.Length != KeyBytes)
                throw new InvalidOperationException(
                    $"Encryption key version {entry.Version} is {bytes.Length} bytes; " +
                    $"AES-256 needs exactly {KeyBytes}. Generate one with: openssl rand -base64 32");

            if (!keys.TryAdd(entry.Version, bytes))
                throw new InvalidOperationException(
                    $"Encryption key version {entry.Version} is defined more than once.");
        }

        if (!keys.ContainsKey(value.ActiveVersion))
            throw new InvalidOperationException(
                $"{TokenCipherOptions.SectionName}:ActiveVersion is {value.ActiveVersion}, " +
                $"but no key with that version is configured. Available: {string.Join(", ", keys.Keys.Order())}.");

        _keys = keys;
        _activeVersion = value.ActiveVersion;
    }

    /// <summary>The message for any use while unconfigured.</summary>
    public static readonly string NotConfiguredMessage =
        $"No encryption keys configured. Set {TokenCipherOptions.SectionName}:Keys — " +
        "each entry needs a Version and a base64 32-byte Key (openssl rand -base64 32). " +
        "Social account credentials cannot be stored without one.";

    public bool IsConfigured => _keys.Count > 0;

    public EncryptedSecret Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!IsConfigured) throw new InvalidOperationException(NotConfiguredMessage);

        // An empty credential is refused rather than encrypted.
        //
        // There is no such thing as a valid empty access token, so reaching
        // here with one means something upstream went wrong — a platform
        // returned no token, or a field was read from the wrong place. Storing
        // it would produce a connection that looks healthy on the card and
        // fails at 8am on the morning the pharmacy scheduled a post. Failing
        // here points at the actual mistake.
        //
        // (This also happens to be what AES-GCM does to an empty input: zero
        // bytes of ciphertext, which base64-encodes to an empty string and is
        // then rejected by EncryptedSecret as missing. Better to say why.)
        if (string.IsNullOrWhiteSpace(plaintext))
            throw new ArgumentException(
                "Refusing to encrypt an empty credential — the platform returned no token.",
                nameof(plaintext));

        var key = _keys[_activeVersion];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        var nonce = new byte[NonceBytes];
        RandomNumberGenerator.Fill(nonce);

        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        // The explicit tag size is required: the single-argument AesGcm
        // constructor is obsolete from .NET 8 and this project treats warnings
        // as errors.
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        return EncryptedSecret.Create(
            Convert.ToBase64String(cipherBytes),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            _activeVersion);
    }

    public string Decrypt(EncryptedSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!IsConfigured) throw new InvalidOperationException(NotConfiguredMessage);

        if (!_keys.TryGetValue(secret.KeyVersion, out var key))
            throw new CryptographicException(
                $"No encryption key with version {secret.KeyVersion} is configured. " +
                "A key that credentials were encrypted with has been removed; " +
                "restore it, or every account encrypted under it must be reconnected.");

        var cipherBytes = Convert.FromBase64String(secret.CipherText);
        var nonce = Convert.FromBase64String(secret.Nonce);
        var tag = Convert.FromBase64String(secret.Tag);

        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(key, TagBytes);

        // Throws CryptographicException when the tag does not verify — i.e. the
        // record was altered or the wrong key was used. Deliberately not caught
        // and converted into a null or an empty string: a caller that treats
        // "could not decrypt" as "no token" would go on to make an unauthenticated
        // call and report a confusing platform error instead of a key problem.
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    public bool CanDecrypt(EncryptedSecret secret) =>
        secret is not null && _keys.ContainsKey(secret.KeyVersion);
}
