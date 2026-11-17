using System.Security.Cryptography;
using chatxp.core;
using Xunit;

namespace chatxp.Core.Tests;

public class E2eeCryptoTests
{
    private static (E2eeCrypto key, string peerPublicKey) CreateKeyPair()
    {
        var crypto = E2eeCrypto.Create();
        return (crypto, crypto.Base64PublicKey);
    }

    [Fact]
    public void Base64PublicKey_Is_Base64()
    {
        var (crypto, _) = CreateKeyPair();
        var b64 = crypto.Base64PublicKey;
        var raw = Convert.FromBase64String(b64);
        // CryptoKit rawRepresentation: raw coordinate pair X || Y, 64 bytes for P-256
        // (no SEC1 0x04 prefix — verified against CryptoKit, see CryptoVectorTests).
        Assert.Equal(64, raw.Length);
    }

    [Fact]
    public void AliceEncrypts_BobDecrypts()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();

        var encrypted = alice.Encrypt("Hello Bob", bobPub)!;
        var decrypted = bob.Decrypt(encrypted, alicePub);

        Assert.Equal("Hello Bob", decrypted);
    }

    [Fact]
    public void BobEncrypts_AliceDecrypts()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();

        var encrypted = bob.Encrypt("Hello Alice", alicePub)!;
        var decrypted = alice.Decrypt(encrypted, bobPub);

        Assert.Equal("Hello Alice", decrypted);
    }

    [Fact]
    public void RoundTrip_SameKey()
    {
        var (crypto, self) = CreateKeyPair();
        var encrypted = crypto.Encrypt("self message", self)!;
        var decrypted = crypto.Decrypt(encrypted, self);
        Assert.Equal("self message", decrypted);
    }

    [Fact]
    public void WrongKey_FailsToDecrypt()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();
        var (mallory, malloryPub) = CreateKeyPair();

        var encrypted = alice.Encrypt("secret", bobPub)!;
        // Mallory cannot decrypt Alice->Bob message.
        Assert.Null(mallory.Decrypt(encrypted, alicePub));
    }

    [Fact]
    public void TamperedMessage_FailsToDecrypt()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();

        var encrypted = alice.Encrypt("important", bobPub)!;
        var raw = Convert.FromBase64String(encrypted);
        raw[^1] ^= 0xFF; // corrupt the tag
        var tampered = Convert.ToBase64String(raw);

        Assert.Null(bob.Decrypt(tampered, alicePub));
    }

    [Fact]
    public void Decrypt_ReturnsNull_OnBadBase64()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();
        Assert.Null(bob.Decrypt("not-base64!!!", alicePub));
    }

    [Fact]
    public void EmptyString_Plaintext()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();
        var encrypted = alice.Encrypt("", bobPub)!;
        Assert.Equal("", bob.Decrypt(encrypted, alicePub));
    }

    [Fact]
    public void Unicode_Plaintext()
    {
        var (alice, alicePub) = CreateKeyPair();
        var (bob, bobPub) = CreateKeyPair();
        const string msg = "👋 café résumé 你好 — 世界";
        var encrypted = alice.Encrypt(msg, bobPub)!;
        Assert.Equal(msg, bob.Decrypt(encrypted, alicePub));
    }
}
