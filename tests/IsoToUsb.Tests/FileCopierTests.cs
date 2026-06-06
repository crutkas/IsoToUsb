using IsoToUsb.Services;

namespace IsoToUsb.Tests;

[TestClass]
public class FileCopierTests
{
    [TestMethod]
    public void Classify_Marks_Large_InstallWim_As_Split()
    {
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[1]);
        try
        {
            var info = new FileInfo(path);
            Assert.AreEqual(
                Fat32FileAction.SplitWithDism,
                FileCopier.ClassifyForFat32(info, Path.Combine("sources", "install.wim"), maxBytes: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Classify_Allows_Small_InstallWim_As_Copy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var info = new FileInfo(path);
            Assert.AreEqual(
                Fat32FileAction.Copy,
                FileCopier.ClassifyForFat32(info, Path.Combine("sources", "install.wim"), maxBytes: 100));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Classify_Splits_Any_Oversize_Wim_Esd_Swm_Regardless_Of_Path()
    {
        // Boot.wim, custom-named .esd, and pre-split .swm should all be
        // recognized as splittable so the pipeline never rejects them.
        var path = Path.Combine(Path.GetTempPath(), $"boot_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var info = new FileInfo(path);
            Assert.AreEqual(
                Fat32FileAction.SplitWithDism,
                FileCopier.ClassifyForFat32(info, Path.Combine("sources", "boot.wim"), maxBytes: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Classify_Rejects_Other_Oversize_Files()
    {
        // A hypothetical 5 GB non-WIM payload (e.g. a giant .vhdx) must
        // be rejected so the pipeline aborts before wiping the USB.
        var path = Path.Combine(Path.GetTempPath(), $"payload_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var info = new FileInfo(path);
            Assert.AreEqual(
                Fat32FileAction.Reject,
                FileCopier.ClassifyForFat32(info, "payload.bin", maxBytes: 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Classify_Handles_Forward_Slash_Paths()
    {
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, new byte[1]);
        try
        {
            var info = new FileInfo(path);
            Assert.AreEqual(
                Fat32FileAction.SplitWithDism,
                FileCopier.ClassifyForFat32(info, "sources/install.wim", maxBytes: 0));
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
