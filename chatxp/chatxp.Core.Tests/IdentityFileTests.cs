using chatxp.core;
using Xunit;

namespace chatxp.Core.Tests;

public class IdentityFileTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"chatxp-identity-{Guid.NewGuid():N}");

    [Fact]
    public void Corrupt_Primary_Is_Recovered_From_Backup()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "identity.key");
            var original = E2eeCrypto.Create();
            var id = original.Base64PublicKey;
            original.SaveIdentityFile(path);

            File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03 });

            var recovered = E2eeCrypto.FromIdentityFile(path);
            Assert.NotNull(recovered);
            Assert.Equal(id, recovered!.Base64PublicKey);

            // The primary was restored too, not just read from the backup.
            Assert.Equal(id, E2eeCrypto.FromIdentityFile(path)!.Base64PublicKey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unrecoverable_Identity_Throws_Instead_Of_Rotating()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "identity.key");
            File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03 });

            Assert.Throws<InvalidDataException>(() => E2eeCrypto.FromIdentityFile(path));
            Assert.Single(Directory.GetFiles(dir, "identity.key.corrupt-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
