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
    public void Classify_Splits_Any_Oversize_Wim_Esd_Regardless_Of_Path()
    {
        // Boot.wim and custom-named .esd should both be recognized as
        // splittable so the pipeline never rejects them.
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
    public void Classify_Rejects_Oversize_Swm_Because_Dism_Cannot_Resplit()
    {
        // DISM /Split-Image takes a .wim/.esd input; it cannot re-split an
        // already-split .swm chunk. If a custom ISO ships with a .swm
        // larger than FAT32's 4 GiB-1 per-file limit we MUST reject and
        // abort up front instead of wiping the USB and then failing.
        var path = Path.Combine(Path.GetTempPath(), $"install_{Guid.NewGuid():N}.swm");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var info = new FileInfo(path);
            Assert.AreEqual(
                Fat32FileAction.Reject,
                FileCopier.ClassifyForFat32(info, Path.Combine("sources", "install.swm"), maxBytes: 0));
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

    [TestMethod]
    public async Task Copy_Reports_Intra_File_Progress_For_Large_Files()
    {
        // The split phase copies 4 GB SWM chunks. Before the chunked-progress
        // fix, FileCopier.CopyAsync only reported once per file, which meant
        // the UI sat at 50% for 30-60 seconds per 4 GB SWM. This test pins
        // that we now emit byte-progress events with monotonically growing
        // BytesDone, and that the final report has BytesDone == BytesTotal.
        var src = Directory.CreateTempSubdirectory("largesrc_");
        var dst = Directory.CreateTempSubdirectory("largedst_");
        try
        {
            // 4 MiB is enough to force several 1 MiB read/write loop turns.
            // (We don't need a real 4 GB file — that would slow the suite.)
            var bigPath = Path.Combine(src.FullName, "install.swm");
            using (var fs = new FileStream(bigPath, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(4L * 1024 * 1024);
            }

            var reports = new List<CopyProgress>();
            // Use a synchronous callback rather than Progress<T> (which
            // marshals through the SynchronizationContext) so reports
            // arrive deterministically on this thread before we assert.
            var progress = new ImmediateProgress<CopyProgress>(reports.Add);
            var copier = new FileCopier();
            await copier.CopyAsync(src.FullName, dst.FullName, progress);

            Assert.IsTrue(reports.Count >= 1, "FileCopier should report at least once.");

            // Pin the actual regression that motivated chunked progress:
            // there must be at least one *intra-file* report (BytesDone > 0
            // and < BytesTotal, FilesDone still 0) before the final 100%.
            // The pre-fix behaviour emitted a single final report which
            // would satisfy reports.Count >= 1 silently.
            var intra = reports.FirstOrDefault(r =>
                r.BytesDone > 0 && r.BytesDone < r.BytesTotal && r.FilesDone == 0);
            Assert.IsNotNull(intra,
                "FileCopier must emit intra-file byte progress before the file completes; " +
                "otherwise the UI sits at 50% for 30-60s per 4 GB SWM.");

            // Final report must be exactly 100% for the file.
            var last = reports[^1];
            Assert.AreEqual(last.BytesTotal, last.BytesDone, "Final BytesDone must equal BytesTotal.");
            Assert.AreEqual(1, last.FilesDone);
            Assert.AreEqual(1, last.FilesTotal);

            // BytesDone is non-decreasing — progress never goes backwards.
            for (int i = 1; i < reports.Count; i++)
            {
                Assert.IsTrue(reports[i].BytesDone >= reports[i - 1].BytesDone, $"BytesDone went backwards at index {i}.");
            }
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
