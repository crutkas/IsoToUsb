using IsoToUsb.Services;

namespace IsoToUsb.Tests;

[TestClass]
public class FileCopierTests
{
    [TestMethod]
    public void Skip_Predicate_Skips_Large_InstallWim()
    {
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[1]);
        try
        {
            var info = new FileInfo(path);
            Assert.IsTrue(FileCopier.ShouldSkipInstallWimForFat32(info, Path.Combine("sources", "install.wim"), maxBytes: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Skip_Predicate_Allows_Small_InstallWim()
    {
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var info = new FileInfo(path);
            Assert.IsFalse(FileCopier.ShouldSkipInstallWimForFat32(info, Path.Combine("sources", "install.wim"), maxBytes: 100));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Skip_Predicate_Allows_Other_Files_Even_If_Large()
    {
        var path = Path.Combine(Path.GetTempPath(), $"boot_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var info = new FileInfo(path);
            Assert.IsFalse(FileCopier.ShouldSkipInstallWimForFat32(info, Path.Combine("sources", "boot.wim"), maxBytes: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Skip_Predicate_Accepts_Forward_Slash_Paths()
    {
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[1]);
        try
        {
            var info = new FileInfo(path);
            Assert.IsTrue(FileCopier.ShouldSkipInstallWimForFat32(info, "sources/install.wim", maxBytes: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Copy_Skips_Files_Matched_By_Predicate()
    {
        var src = Directory.CreateTempSubdirectory("copysrc_");
        var dst = Directory.CreateTempSubdirectory("copydst_");
        try
        {
            File.WriteAllText(Path.Combine(src.FullName, "keep.txt"), "keep me");
            File.WriteAllText(Path.Combine(src.FullName, "skip.txt"), "skip me");

            var copier = new FileCopier { SkipPredicate = (_, rel) => rel.EndsWith("skip.txt", StringComparison.OrdinalIgnoreCase) };
            var skipped = await copier.CopyAsync(src.FullName, dst.FullName);

            Assert.IsTrue(File.Exists(Path.Combine(dst.FullName, "keep.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(dst.FullName, "skip.txt")));
            Assert.AreEqual(1, skipped.Count);
            Assert.AreEqual("skip.txt", skipped[0]);
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }
}
