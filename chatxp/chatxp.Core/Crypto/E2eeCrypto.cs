using System.Security.Cryptography;
using System.Text;

namespace chatxp.core;

/// <summary>
/// End-to-end encryption mirroring chat/Encrypt.swift exactly, for byte-compatibility
/// with the iOS app's CryptoKit output.
///
/// Algorithm (verbatim mapping of the Swift implementation):
///   1. ECDH over NIST P-256 (secp256r1) between the local private key and the peer's
///      public key -> raw shared secret. The public key is transported as CryptoKit's
///      `rawRepresentation`: the raw 64-byte coordinate pair `X || Y` (NO SEC1 0x04 prefix —
///      verified against CryptoKit, which rejects the 65-byte prefixed form). This matches
///      `P256.KeyAgreement.PublicKey(rawRepresentation:)` as used by Encrypt.swift and
///      ChatView.swift for the from/to identity fields.
///   2. HKDF-SHA256 (RFC 5869): salt = empty (HMAC key = 32 zero bytes), sharedInfo =
///      "P2P-Secure-Channel-Context", 32-byte output -> AES-256 key. This matches CryptoKit's
///      `hkdfDerivedSymmetricKey(using:SHA256.self, salt:Data(), sharedInfo:..., outputByteCount:32)`.
///   3. AES-256-GCM seal producing a 12-byte nonce + ciphertext + 16-byte authentication tag.
///   4. The three are packed in the same layout as CryptoKit's
///      AES.GCM.SealedBox.combined (nonce || ciphertext || tag) and Base64-encoded.
///
/// A payload encrypted here is byte-compatible with the Swift app and vice-versa. Verified
/// against CryptoKit-generated test vectors (see CryptoVectorTests).
/// </summary>
public sealed class E2eeCrypto : IDisposable
{
    private const string SharedInfo = "P2P-Secure-Channel-Context";
    private const int NonceLength = 12;     // AES-GCM standard nonce size
    private const int TagSizeBytes = 16;     // AES-GCM 128-bit tag
    private const int TagLength = TagSizeBytes;

    public const int CoordSize = 32;        // P-256 coordinate size in bytes
    public const int RawPublicKeyLength = 64;   // CryptoKit rawRepresentation: X || Y
    public const int Sec1Length = 65;      // 04 || X || Y (internal DER form only)

    private readonly ECDiffieHellman _privateKey;

    public E2eeCrypto(ECDiffieHellman privateKey)
    {
        _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
        if (_privateKey.KeySize != 256)
            throw new ArgumentException("A P-256 (nistP256/secp256r1) key is required.", nameof(privateKey));
    }

    /// <summary>Creates a new random P-256 key pair.</summary>
    public static E2eeCrypto Create() =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Creates a P-256 key pair from a fixed 32-byte raw private scalar
    /// (CryptoKit's `rawRepresentation` of the private key). Exported for testing
    /// against CryptoKit vectors.</summary>
    public static E2eeCrypto FromPrivateKey(byte[] rawScalar)
    {
        if (rawScalar.Length != CoordSize)
            throw new ArgumentException("Private scalar must be 32 bytes for P-256.", nameof(rawScalar));

        // RFC 5915 ECPrivateKey DER with the public key field omitted (it is OPTIONAL);
        // .NET's ImportECPrivateKey computes Q = d·G on import.
        //   SEQUENCE {
        //     INTEGER 1,
        //     OCTET STRING (32-byte scalar),
        //     [0] { OID prime256v1 }
        //   }
        var der = new byte[2 + 3 + 2 + CoordSize + 12];
        der[0] = 0x30;
        der[1] = (byte)(der.Length - 2); // 49
        der[2] = 0x02; der[3] = 0x01; der[4] = 0x01;                     // INTEGER 1 (version)
        der[5] = 0x04; der[6] = CoordSize;                                // OCTET STRING len 32
        Buffer.BlockCopy(rawScalar, 0, der, 7, CoordSize);
        der[7 + CoordSize + 0] = 0xA0; der[7 + CoordSize + 1] = 0x0A;     // [0] len 10
        der[7 + CoordSize + 2] = 0x06; der[7 + CoordSize + 3] = 0x08;   // OID len 8
        // OID 1.2.840.10045.3.1.7 (prime256v1)
        var oid = new byte[] { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 };
        Buffer.BlockCopy(oid, 0, der, 7 + CoordSize + 4, oid.Length);

        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        key.ImportECPrivateKey(der, out _);
        return new E2eeCrypto(key);
    }

    /// <summary>
    /// The Base64-encoded raw public key in CryptoKit's `rawRepresentation` format:
    /// the raw 64-byte pair `X || Y` (no SEC1 prefix). This is the exact string the Swift
    /// app uses as a user's identity (`ChatView.swift`: `publicKey.rawRepresentation.base64EncodedString()`)
    /// and as the peer key argument to `encryptP2PMessage`/`decryptP2PMessage`.
    /// </summary>
    public string Base64PublicKey
    {
        get
        {
            var p = _privateKey.ExportParameters(false);
            var raw = new byte[RawPublicKeyLength];
            Buffer.BlockCopy(p.Q.X!, 0, raw, 0, CoordSize);
            Buffer.BlockCopy(p.Q.Y!, 0, raw, CoordSize, CoordSize);
            return Convert.ToBase64String(raw);
        }
    }

    /// <summary>
    /// Persistence of the identity key (v1, per business-requirements.md NFR-3):
    /// the PKCS#8 private key is written to an app-data file with user-only permissions
    /// where the OS supports it (Unix: chmod 600; Windows: relies on the user profile
    /// directory ACL). Hardware-backed storage (Secure Enclave equivalent) is out of
    /// scope and surfaced as a limitation in the app settings.
    /// </summary>
    public static E2eeCrypto? FromIdentityFile(string path)
    {
        if (!File.Exists(path)) return null;
        var der = File.ReadAllBytes(path);
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        key.ImportPkcs8PrivateKey(der, out _);
        return new E2eeCrypto(key);
    }

    public void SaveIdentityFile(string path)
    {
        var der = _privateKey.ExportPkcs8PrivateKey();
        File.WriteAllBytes(path, der);
        // Unix: user-only read. Windows: profile dir ACL gives the same effect.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Encrypts a UTF-8 plaintext for the given peer (their Base64 raw-SEC1 public key).
    /// Returns a Base64 string of the combined AES-GCM blob (nonce||cipher||tag), or null on failure.
    /// </summary>
    public string? Encrypt(string plaintext, string peerPublicKeyBase64)
    {
        return Encrypt(Encoding.UTF8.GetBytes(plaintext), peerPublicKeyBase64);
    }

    /// <summary>Byte-array variant of <see cref="Encrypt(string, string)"/> (used for image payloads).</summary>
    public string? Encrypt(byte[] plaintext, string peerPublicKeyBase64)
    {
        var peerPublicKey = LoadPeerPublicKey(peerPublicKeyBase64);
        if (peerPublicKey is null) return null;

        var symmetricKey = DeriveSymmetricKey(peerPublicKey);
        if (symmetricKey is null) return null;

        try
        {
            var nonce = new byte[NonceLength];
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(symmetricKey, TagSizeBytes))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, null);
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
    /// Returns the plaintext string, or null if the tag fails (tampered / wrong key).
    /// </summary>
    public string? Decrypt(string base64Combined, string peerPublicKeyBase64)
    {
        if (combinedToPlain(base64Combined, peerPublicKeyBase64) is not { } plain)
            return null;
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Byte-array variant of <see cref="Decrypt(string, string)"/> (used for image payloads).</summary>
    public byte[]? DecryptBytes(string base64Combined, string peerPublicKeyBase64) =>
        combinedToPlain(base64Combined, peerPublicKeyBase64);

    private byte[]? combinedToPlain(string base64Combined, string peerPublicKeyBase64)
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
            using (var aes = new AesGcm(symmetricKey, TagSizeBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, null);
            }
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// HKDF-SHA256 (RFC 5869) of the ECDH shared secret: empty salt (HMAC key = 32 zero bytes),
    /// sharedInfo = "P2P-Secure-Channel-Context", 32-byte output. Matches CryptoKit's
    /// `hkdfDerivedSymmetricKey`.
    /// </summary>
    private byte[]? DeriveSymmetricKey(ECDiffieHellmanPublicKey peerPublicKey)
    {
        byte[] sharedSecret;
        try
        {
            sharedSecret = _privateKey.DeriveRawSecretAgreement(peerPublicKey);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return HkdfSha256(sharedSecret, Array.Empty<byte>(),
            Encoding.UTF8.GetBytes(SharedInfo), 32);
    }

    /// <summary>Test hook: exposes the derived symmetric key for vector verification.</summary>
    internal byte[] DeriveSymmetricKeyForTest(string peerPublicKeyBase64)
    {
        var peer = LoadPeerPublicKey(peerPublicKeyBase64)
            ?? throw new InvalidOperationException("Failed to load peer public key.");
        return DeriveSymmetricKey(peer)
            ?? throw new InvalidOperationException("Key derivation failed.");
    }

    /// <summary>HKDF-SHA256 (RFC 5869): extract then expand.</summary>
    internal static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int outputLength)
    {
        // Extract: PRK = HMAC-Hash(salt, IKM). RFC 5869 §2.3: "if the salt is not provided,
        // it is set to a string of HashLen zeros" (32 for SHA-256).
        var hmacKey = salt.Length == 0 ? new byte[32] : salt;
        byte[] prk;
        using (var hmac = new HMACSHA256(hmacKey))
        {
            prk = hmac.ComputeHash(ikm);
        }

        // Expand: T(0) = empty; T(i) = HMAC-Hash(PRK, T(i-1) || info || i); OKM = first L bytes.
        var okm = new byte[outputLength];
        var t = Array.Empty<byte>();
        var generated = 0;
        for (byte counter = 1; generated < outputLength; counter++)
        {
            using var hmac = new HMACSHA256(prk);
            var input = new byte[t.Length + info.Length + 1];
            Buffer.BlockCopy(t, 0, input, 0, t.Length);
            Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
            input[^1] = counter;
            t = hmac.ComputeHash(input);
            var take = Math.Min(t.Length, outputLength - generated);
            Buffer.BlockCopy(t, 0, okm, generated, take);
            generated += take;
        }
        return okm;
    }

    /// <summary>
    /// Parses a Base64 public key in CryptoKit's `rawRepresentation` format (`X || Y`, 64 bytes)
    /// into an ECDiffieHellmanPublicKey. For DER import we prepend the SEC1 0x04 prefix to
    /// form a valid point.
    /// </summary>
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

        // CryptoKit rawRepresentation: X(32) || Y(32), no 0x04 prefix.
        if (raw.Length != RawPublicKeyLength)
            return null;

        try
        {
            // Build the SEC1 point (04 || X || Y) and wrap it in a standard P-256
            // SubjectPublicKeyInfo (DER), then import. On macOS ImportParameters with a
            // bare Q throws AppleCFError -50, but ImportSubjectPublicKeyInfo accepts the
            // standard DER form.
            var spki = new byte[SpkiPrefix.Length + Sec1Length];
            Buffer.BlockCopy(SpkiPrefix, 0, spki, 0, SpkiPrefix.Length);
            spki[SpkiPrefix.Length] = 0x04;
            Buffer.BlockCopy(raw, 0, spki, SpkiPrefix.Length + 1, RawPublicKeyLength);

            var peer = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            peer.ImportSubjectPublicKeyInfo(spki, out _);
            return peer.PublicKey;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Standard DER SubjectPublicKeyInfo header for an EC P-256 public key followed by a raw
    /// SEC1 point: SEQUENCE { SEQUENCE { OID 1.2.840.10045.2.1, OID 1.2.840.10045.3.1.7 },
    /// BIT STRING { 00, 04 || X || Y } }.
    /// </summary>
    private static readonly byte[] SpkiPrefix =
    {
        0x30, 0x59,             // SEQUENCE, length 89
        0x30, 0x13,             //   SEQUENCE, length 19
        0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,  // OID 1.2.840.10045.2.1 (ecPublicKey)
        0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, // OID 1.2.840.10045.3.1.7 (prime256v1)
        0x03, 0x42, 0x00        //   BIT STRING, length 66, 0 unused bits
    };

    public void Dispose() => _privateKey.Dispose();
}
