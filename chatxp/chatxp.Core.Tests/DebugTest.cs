using System;
using System.Security.Cryptography;
using chatxp.core;
using Xunit;
using Xunit.Abstractions;

namespace chatxp.Core.Tests;

public class DebugTest
{
    private readonly ITestOutputHelper _output;
    public DebugTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BuildSpkiFromPoint()
    {
        var alice = new E2eeCrypto(ECDiffieHellman.Create());
        var bob = new E2eeCrypto(ECDiffieHellman.Create());
        var rawBob = Convert.FromBase64String(bob.Base64PublicKey);

        // Build SPKI: SEQUENCE { SEQUENCE{OID ecPublicKey, OID p256}, BIT STRING { 0x00, OCTET STRING{04||X||Y} } }
        // OCTET STRING containing the uncompressed point
        var pointOctet = new byte[1 + rawBob.Length];
        pointOctet[0] = 0x04;
        Array.Copy(rawBob, 1, pointOctet, 1, rawBob.Length - 1);
        // pointOctet is already 04||X||Y since rawBob IS 04||X||Y
        pointOctet = rawBob;

        var bitString = new byte[4 + pointOctet.Length];
        bitString[0] = 0x03; // BIT STRING
        bitString[1] = 0x81; // length > 127 uses 2-byte length
        bitString[2] = (byte)pointOctet.Length;
        bitString[3] = 0x00; // unused bits
        Array.Copy(pointOctet, 0, bitString, 4, pointOctet.Length);

        // algorithm identifier
        var alg = new byte[] { 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 };

        var outer = new byte[2 + alg.Length + bitString.Length];
        outer[0] = 0x30;
        outer[1] = (byte)(alg.Length + bitString.Length);
        Array.Copy(alg, 0, outer, 2, alg.Length);
        Array.Copy(bitString, 0, outer, 2 + alg.Length, bitString.Length);

        _output.WriteLine($"outer SPKI: {Convert.ToHexString(outer)}");
        _output.WriteLine($"outer length={outer.Length}");

        var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(outer, out int consumed);
            _output.WriteLine($"ImportSubjectPublicKeyInfo OK, consumed={consumed} of {outer.Length}");
            _output.WriteLine($"SPKI matches real: {peer.ExportSubjectPublicKeyInfo().SequenceEqual(alice.Base64PublicKey is null ? Array.Empty<byte>() : Convert.FromBase64String(alice.Base64PublicKey))}");
        }
        catch (Exception e)
        {
            _output.WriteLine($"Import FAILED: {e.GetType().Name}: {e.Message}");
        }
        Assert.True(true);
    }
}
