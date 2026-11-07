using System.Security.Cryptography;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// Tests for the encryption protecting pharmacies' social credentials.
/// </summary>
/// <remarks>
/// Weighted towards the properties that make the scheme sound rather than the
/// round trip. A cipher that encrypts and decrypts correctly but reuses nonces,
/// or accepts a tampered record, is broken in a way no happy-path test notices.
/// </remarks>
public class TokenCipherTests
{
    private static string Key(byte seed) =>
        Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray());

    private static TokenCipher Cipher(int activeVersion = 1, params (int Version, byte Seed)[] keys)
    {
        var list = (keys.Length == 0 ? new[] { (Version: 1, Seed: (byte)7) } : keys)
            .Select(k => new TokenKey { Version = k.Version, Key = Key(k.Seed) })
            .ToList();

        return new TokenCipher(Options.Create(new TokenCipherOptions
        {
            ActiveVersion = activeVersion,
            Keys = list,
        }));
    }

    /* ---------------- round trip ---------------- */

    [Fact]
    public void Round_trips_a_token()
    {
        var cipher = Cipher();
        const string token = "EAAG1ZBxyz...a-realistic-looking-page-token";

        Assert.Equal(token, cipher.Decrypt(cipher.Encrypt(token)));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("token with spaces and — punctuation … 😀")]
    [InlineData("   leading and trailing space   ")]
    public void Round_trips_awkward_input(string token)
    {
        var cipher = Cipher();
        Assert.Equal(token, cipher.Decrypt(cipher.Encrypt(token)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_to_encrypt_an_empty_credential(string token)
    {
        // There is no valid empty access token, so this means something
        // upstream misread a response. Encrypting it would store a connection
        // that looks healthy and fails at publish time, days later and far from
        // the actual mistake.
        var cipher = Cipher();

        Assert.Throws<ArgumentException>(() => cipher.Encrypt(token));
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var cipher = Cipher();
        var secret = cipher.Encrypt("super-secret-token");

        Assert.DoesNotContain("super-secret-token", secret.CipherText);
    }

    /* ---------------- the properties that matter ---------------- */

    [Fact]
    public void Never_reuses_a_nonce()
    {
        // Nonce reuse under one key is a total break of GCM, not a weakening:
        // it leaks the XOR of the plaintexts and permits tag forgery. Two
        // hundred encryptions of identical input must produce two hundred
        // distinct nonces.
        var cipher = Cipher();

        var nonces = Enumerable.Range(0, 200)
            .Select(_ => cipher.Encrypt("the same token every time").Nonce)
            .ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
    }

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var cipher = Cipher();

        var a = cipher.Encrypt("identical");
        var b = cipher.Encrypt("identical");

        // Falls out of the fresh nonce, and is what stops an observer of the
        // database learning that two pharmacies hold the same token.
        Assert.NotEqual(a.CipherText, b.CipherText);
    }

    [Fact]
    public void Rejects_a_tampered_ciphertext()
    {
        var cipher = Cipher();
        var secret = cipher.Encrypt("token");

        var bytes = Convert.FromBase64String(secret.CipherText);
        bytes[0] ^= 0xFF;
        var tampered = secret with { CipherText = Convert.ToBase64String(bytes) };

        // ThrowsAny, not Throws. .NET raises AuthenticationTagMismatchException,
        // a subclass of CryptographicException, and which subclass surfaces is
        // a platform detail — this run went through Interop.AppleCrypto on
        // macOS. What the contract actually promises is "a cryptographic
        // failure, not a silent wrong answer", and pinning the exact type would
        // make the suite fail on a different OS for no reason.
        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(tampered));
    }

    [Fact]
    public void Rejects_a_tampered_tag()
    {
        var cipher = Cipher();
        var secret = cipher.Encrypt("token");

        var tag = Convert.FromBase64String(secret.Tag);
        tag[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => cipher.Decrypt(secret with { Tag = Convert.ToBase64String(tag) }));
    }

    [Fact]
    public void Rejects_a_record_encrypted_under_a_different_key()
    {
        // Same version number, different key material — what happens if two
        // environments both call their key "version 1".
        var a = Cipher(1, (1, 7));
        var b = Cipher(1, (1, 9));

        Assert.ThrowsAny<CryptographicException>(() => b.Decrypt(a.Encrypt("token")));
    }

    /* ---------------- key rotation ---------------- */

    [Fact]
    public void Reads_records_written_under_an_older_key()
    {
        // The point of versioning: introduce v2 for new writes, keep v1 so
        // existing connections survive without a migration.
        var before = Cipher(1, (1, 7));
        var old = before.Encrypt("token-from-last-year");

        var after = Cipher(2, (1, 7), (2, 11));

        Assert.Equal("token-from-last-year", after.Decrypt(old));
        Assert.Equal(2, after.Encrypt("new").KeyVersion);
    }

    [Fact]
    public void Says_which_key_version_is_missing()
    {
        var v2 = Cipher(2, (2, 11));
        var encryptedUnderV1 = Cipher(1, (1, 7)).Encrypt("token");

        var ex = Assert.Throws<CryptographicException>(() => v2.Decrypt(encryptedUnderV1));

        // The message has to name the version. "Decryption failed" sends
        // someone hunting a corrupt database when a key was simply dropped
        // from config. Matching "version 1" rather than "1" so the assertion
        // cannot pass on an incidental digit elsewhere in the message.
        Assert.Contains("version 1", ex.Message);
    }

    [Fact]
    public void CanDecrypt_reports_without_throwing()
    {
        var v2 = Cipher(2, (2, 11));
        var underV1 = Cipher(1, (1, 7)).Encrypt("token");

        Assert.False(v2.CanDecrypt(underV1));
        Assert.True(v2.CanDecrypt(v2.Encrypt("token")));
    }

    /* ---------------- configuration ---------------- */

    [Fact]
    public void With_no_keys_it_reports_unconfigured_and_refuses_to_encrypt()
    {
        // Unconfigured is a state, not a crash: the social page still loads
        // and says why nothing can be connected. Any actual use still fails,
        // with the instruction to fix it.
        var cipher = new TokenCipher(Options.Create(new TokenCipherOptions()));

        Assert.False(cipher.IsConfigured);
        var ex = Assert.Throws<InvalidOperationException>(() => cipher.Encrypt("token"));
        Assert.Contains("openssl", ex.Message);
        Assert.Throws<InvalidOperationException>(() =>
            cipher.Decrypt(EncryptedSecret.Create("c", "n", "t", 1)));
    }

    [Fact]
    public void Refuses_a_key_of_the_wrong_length()
    {
        var options = Options.Create(new TokenCipherOptions
        {
            ActiveVersion = 1,
            Keys = new List<TokenKey>
            {
                new() { Version = 1, Key = Convert.ToBase64String(new byte[16]) },
            },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new TokenCipher(options));
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Refuses_a_key_that_is_not_base64()
    {
        var options = Options.Create(new TokenCipherOptions
        {
            ActiveVersion = 1,
            Keys = new List<TokenKey> { new() { Version = 1, Key = "not base64 !!" } },
        });

        Assert.Throws<InvalidOperationException>(() => new TokenCipher(options));
    }

    [Fact]
    public void Refuses_an_active_version_with_no_matching_key()
    {
        // The misconfiguration that would otherwise surface as a NullReference
        // on the first pharmacy to connect an account.
        var options = Options.Create(new TokenCipherOptions
        {
            ActiveVersion = 3,
            Keys = new List<TokenKey> { new() { Version = 1, Key = Key(7) } },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new TokenCipher(options));
        Assert.Contains("ActiveVersion", ex.Message);
    }

    [Fact]
    public void Refuses_duplicate_key_versions()
    {
        var options = Options.Create(new TokenCipherOptions
        {
            ActiveVersion = 1,
            Keys = new List<TokenKey>
            {
                new() { Version = 1, Key = Key(7) },
                new() { Version = 1, Key = Key(9) },
            },
        });

        Assert.Throws<InvalidOperationException>(() => new TokenCipher(options));
    }

    /* ---------------- leakage ---------------- */

    [Fact]
    public void ToString_does_not_print_the_ciphertext()
    {
        var secret = Cipher().Encrypt("token");

        // Guards against a stray log statement or string interpolation putting
        // credential material into a log file.
        Assert.DoesNotContain(secret.CipherText, secret.ToString());
    }
}
