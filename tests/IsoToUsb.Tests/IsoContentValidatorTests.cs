using IsoToUsb.Services;

namespace IsoToUsb.Tests;

[TestClass]
public class IsoContentValidatorTests
{
    [TestMethod]
    public void Ensure_Throws_When_BootWim_Missing()
    {
        var tempDir = Directory.CreateTempSubdirectory("isovalid_");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir.FullName, "sources"));
            Assert.Throws<InvalidDataException>(() =>
                IsoContentValidator.EnsureWindowsInstallIso(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Ensure_Succeeds_When_BootWim_Exists()
    {
        var tempDir = Directory.CreateTempSubdirectory("isovalid_");
        try
        {
            var sources = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "sources"));
            File.WriteAllText(Path.Combine(sources.FullName, "boot.wim"), "stub");
            IsoContentValidator.EnsureWindowsInstallIso(tempDir.FullName);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void GetInstallWimSize_Returns_0_When_Missing()
    {
        var tempDir = Directory.CreateTempSubdirectory("isovalid_");
        try
        {
            Assert.AreEqual(0, IsoContentValidator.GetInstallWimSize(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void GetInstallWimSize_Returns_File_Size()
    {
        var tempDir = Directory.CreateTempSubdirectory("isovalid_");
        try
        {
            var sources = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "sources"));
            var path = Path.Combine(sources.FullName, "install.wim");
            File.WriteAllBytes(path, new byte[1024]);
            Assert.AreEqual(1024, IsoContentValidator.GetInstallWimSize(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
