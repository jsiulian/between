using System;
using System.Security.Cryptography;
using System.Text;

namespace chatxp.core;

/// <summary>
/// End-to-end encryption mirroring chat/Encrypt.swift exactly, for byte-compatibility
/// with the iOS app's CryptoKit output.
///
/// Algorithm (verbatim mapping of the Swift implementation):
///   1. ECDH over NIST P-256 (secp256r1) between the local private key and the peer's
///      public key -> shared secret. The public key is transported as a raw SEC1 (X9.63)
///      uncompressed point `04 || X || Y`, matching CryptoKit's `rawRepresentation`
///      (`P256.KeyAgreement.PublicKey(rawRepresentation:)`).
///   2. HKDF-SHA256 key derivation: salt = empty, sharedInfo = "P2P-Secure-Channel-Context",
///      32-byte output -> AES-256 key (CryptoKit `hkdfDerivedSymmetricKey(using:SHA256:salt:sharedInfo:outputByteCount:)`).
///   3. AES-256-GCM seal producing a 12-byte nonce + ciphertext + 16-byte authentication tag.
///   4. The three are packed in the same layout as CryptoKit's
///      AES.GCM.SealedBox.combined (nonce || ciphertext || tag) and Base64-encoded.
///
/// A payload encrypted here is byte-compatible with the Swift app and vice-versa, because
/// both derive the same shared secret and run the same HKDF/AES-GCM.
/// </summary>
public sealed class E2eeCrypto : IDisposable
{
    private const string SharedInfo = "P2P-Secure-Channel-Context";
    private const int NonceLength = 12;     // AES-GCM standard nonce size
    private const int TagSizeBits = 128;    // AES-GCM 128-bit tag
    private const int TagLength = TagSizeBits / 8;

    private readonly ECDiffieHellman _privateKey;

    public E2eeCrypto(ECDiffieHellman privateKey)
    {
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
    }

    /// <summary>
    /// The Base64-encoded raw SEC1 (X9.63) public key (`04 || X || Y`), matching CryptoKit's
    /// `rawRepresentation` used by the Swift app (`Encrypt.swift`, `getPublicKey`). Used by
    /// peers and the signaling server to identify a user.
    /// </summary>
    public string Base64PublicKey =>
        Convert.ToBase64String(ExportRawSec1());

    /// <summary>
    /// Encrypts a UTF-8 plaintext for the given peer (their Base64 raw-SEC1 public key).
    /// Returns a Base64 string of the combined AES-GCM blob (nonce||cipher||tag), or null on failure.
    /// </summary>
    public string? Encrypt(string plaintext, string peerPublicKeyBase64)
    {
        var peerPublicKey = LoadPeerPublicKey(peerPublicKeyBase64);
        if (peerPublicKey is null) return null;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var symmetricKey = DeriveSymmetricKey(peerPublicKey);
        if (symmetricKey is null) return null;

        try
        {
            var nonce = new byte[NonceLength];
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(symmetricKey, TagSizeBits))
            {
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, null);
            }

            // Combine nonce || ciphertext || tag (matches AES.GCM.SealedBox.combined layout).
            var combined = new byte[NonceLength + ciphertext.Length + TagLength];
            Buffer.BlockCopy(nonce, 0, combined, 0, NonceLength);
            Buffer.BlockCopy(ciphertext, 0, combined, NonceLength, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, NonceLength + ciphertext.Length, TagLength);

            return Convert.ToBase64String(combined);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts a Base64 AES-GCM blob from the given peer (their Base64 raw-SEC1 public key).
    /// Returns the plaintext, or null if the tag fails (tampered message / wrong key).
    /// </summary>
    public string? Decrypt(string base64Combined, string peerPublicKeyBase64)
    {
        var peerPublicKey = LoadPeerPublicKey(peerPublicKeyBase64);
        if (peerPublicKey is null) return null;

        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(base64Combined);
        }
        catch (FormatException)
        {
            return null;
        }

        if (combined.Length < NonceLength + TagLength)
            return null;

        var symmetricKey = DeriveSymmetricKey(peerPublicKey);
        if (symmetricKey is null) return null;

        try
        {
            var nonce = new byte[NonceLength];
            var ciphertext = new byte[combined.Length - NonceLength - TagLength];
            var tag = new byte[TagLength];

            Buffer.BlockCopy(combined, 0, nonce, 0, NonceLength);
            Buffer.BlockCopy(combined, NonceLength, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(combined, NonceLength + ciphertext.Length, tag, 0, TagLength);

            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(symmetricKey, TagSizeBits))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, null);
            }
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the ECDH shared secret, then derives a 32-byte AES-256 key with HKDF-SHA256
    /// (empty salt, sharedInfo = "P2P-Secure-Channel-Context").
    /// </summary>
    private byte[]? DeriveSymmetricKey(ECDiffieHellmanPublicKey peerPublicKey)
    {
        byte[] sharedSecret;
        try
        {
            // DeriveKeyFromHash(peer, SHA256, secretPrepend=null, secretAppend=info) = HKDF-SHA256.
            sharedSecret = _privateKey.DeriveKeyFromHash(peerPublicKey, HashAlgorithmName.SHA256, null, Encoding.UTF8.GetBytes(SharedInfo));
        }
        catch (CryptographicException)
        {
            return null;
        }

        return sharedSecret;
    }

    /// <summary>Parses a Base64 raw SEC1 (X9.63) point into an ECDiffieHellmanPublicKey.</summary>
    private ECDiffieHellmanPublicKey? LoadPeerPublicKey(string peerPublicKeyBase64)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(peerPublicKeyBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        // SEC1 uncompressed point: 04 || X(32) || Y(32) for P-256.
        if (raw.Length != 65 || raw[0] != 0x04)
            return null;

        try
        {
            var coordSize = 32;
            var x = new byte[coordSize];
            var y = new byte[coordSize];
            Buffer.BlockCopy(raw, 1, x, 0, coordSize);
            Buffer.BlockCopy(raw, 33, y, 0, coordSize);
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            };
            var peer = ECDiffieHellman.Create();
            peer.ImportParameters(parameters);
            return peer.PublicKey;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Exports this key's public value as a raw SEC1 (X9.63) uncompressed point.
    /// This matches CryptoKit's `rawRepresentation` (Swift `getPublicKey` ->
    /// `SecKeyCopyPublicKey` -> `SecKeyCopyExternalRepresentation`).
    /// </summary>
    private byte[] ExportRawSec1()
    {
        var p = _privateKey.ExportParameters(false);
        var x = p.Q.X!;
        var y = p.Q.Y!;
        // Trim big-endian coordinates to FieldSize bytes.
        var coordSize = _privateKey.KeySize / 8 / 2;
        var outX = x.Length > coordSize ? x[^coordSize..] : x;
        var outY = y.Length > coordSize ? y[^coordSize..] : y;
        var sec1 = new byte[1 + 2 * coordSize];
        sec1[0] = 0x04;
        Buffer.BlockCopy(outX, 0, sec1, 1, coordSize);
        Buffer.BlockCopy(outY, 0, sec1, 1 + coordSize, coordSize);
        return sec1;
    }

    public void Dispose() => _privateKey.Dispose();
}
