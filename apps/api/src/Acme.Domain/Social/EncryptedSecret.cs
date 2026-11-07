using Acme.Domain.Common;

namespace Acme.Domain.Social;

/// <summary>
/// A credential held as ciphertext. The domain never sees the plaintext.
/// </summary>
/// <remarks>
/// <para>
/// Modelled as a value object rather than a bare string so that the type
/// system carries the distinction. A method taking <c>string accessToken</c>
/// can be handed a plaintext token by accident and will happily persist it;
/// one taking <see cref="EncryptedSecret"/> cannot.
/// </para>
/// <para>
/// <b>KeyVersion is the part that matters later.</b> Every record records which
/// key encrypted it. Without that, rotating the encryption key means either
/// decrypting and rewriting every token in one migration — with an outage if it
/// half-finishes — or never rotating at all. With it, a new key can be
/// introduced for new writes while old records stay readable, and the old key
/// retires once nothing references it.
/// </para>
/// <para>
/// The nonce is stored alongside the ciphertext because AES-GCM requires a
/// unique one per encryption under the same key, and reusing one is a total
/// break of the mode rather than a gradual weakening. It is not secret.
/// </para>
/// </remarks>
public sealed record EncryptedSecret(
    string CipherText,
    string Nonce,
    string Tag,
    int KeyVersion)
{
    public static EncryptedSecret Create(string cipherText, string nonce, string tag, int keyVersion)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
            throw new DomainException("Cipher text is required.");
        if (string.IsNullOrWhiteSpace(nonce))
            throw new DomainException("Nonce is required.");
        if (string.IsNullOrWhiteSpace(tag))
            throw new DomainException("Authentication tag is required.");
        if (keyVersion < 1)
            throw new DomainException("Key version must be 1 or greater.");

        return new EncryptedSecret(cipherText, nonce, tag, keyVersion);
    }

    /// <summary>
    /// Deliberately overridden so a stray interpolation or log statement cannot
    /// print the ciphertext. It is not plaintext, but it is still the material
    /// an attacker with the key would need, and there is no reason for it to
    /// appear in a log file.
    /// </summary>
    public override string ToString() => $"EncryptedSecret(v{KeyVersion}, {CipherText.Length} chars)";
}
