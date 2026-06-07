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

    [TestMethod]
    public async Task Copy_Produces_Destination_Exactly_Source_Size()
    {
        // Defense against the install2.swm silent-truncation bug
        // (2026-06-07): FileCopier MUST produce a destination file whose
        // length exactly equals the source length. The post-write check
        // inside CopyAsync hard-fails on mismatch; on success, the file
        // on disk must round-trip cleanly.
        var src = Directory.CreateTempSubdirectory("exactsrc_");
        var dst = Directory.CreateTempSubdirectory("exactdst_");
        try
        {
            // Three files of varied non-power-of-two sizes that span
            // multiple 1 MiB read buffers — chosen to flush out off-by-one
            // bugs in the read/write loop.
            var sizes = new Dictionary<string, long>
            {
                ["small.bin"] = 7L,
                ["medium.bin"] = (1L * 1024 * 1024) + 123, // 1 MiB + 123
                ["large.bin"] = (3L * 1024 * 1024) + 999,  // 3 MiB + 999
            };
            foreach (var (name, len) in sizes)
            {
                using var fs = new FileStream(Path.Combine(src.FullName, name), FileMode.Create, FileAccess.Write);
                fs.SetLength(len);
            }

            await new FileCopier().CopyAsync(src.FullName, dst.FullName);

            foreach (var (name, expected) in sizes)
            {
                var actual = new FileInfo(Path.Combine(dst.FullName, name)).Length;
                Assert.AreEqual(expected, actual, $"{name}: copy must produce exact source size.");
            }
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void AssertCopiedSizeMatches_NoOp_When_Sizes_Match()
    {
        // The size assertion is the single safety net that would have
        // surfaced the install2.swm truncation as a hard error instead of
        // a green pipeline + corrupt USB. Pin both arms (match + mismatch)
        // so the throw cannot be regressed away by a future "optimization".
        var path = Path.Combine(Path.GetTempPath(), $"fcasrt_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            FileCopier.AssertCopiedSizeMatches(path, expectedBytes: 5, relativePathForMessage: "sources/install.swm");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AssertCopiedSizeMatches_Throws_With_Diagnostic_When_Truncated()
    {
        // Regression: real install2.swm truncation from 3.55 GiB to 1.17
        // GiB landed silently because there was no post-write check at
        // all. This test pins that the helper throws IOException and that
        // the message names the file + both byte counts so the failure is
        // diagnosable without a debugger.
        var path = Path.Combine(Path.GetTempPath(), $"fcasrt_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[1168]); // simulating the observed truncation
        try
        {
            var ex = Assert.Throws<IOException>(() =>
                FileCopier.AssertCopiedSizeMatches(path, expectedBytes: 3552, relativePathForMessage: "sources/install2.swm"));
            StringAssert.Contains(ex.Message, "install2.swm");
            StringAssert.Contains(ex.Message, "1,168");
            StringAssert.Contains(ex.Message, "3,552");
            StringAssert.Contains(ex.Message, "Refusing to continue");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AssertCopiedSizeMatches_Throws_When_Destination_Grew_Past_Source()
    {
        // The check is symmetric on purpose: if dest > src, something
        // wrote extra bytes (rare, but possible if a stale file wasn't
        // truncated to zero before the new copy started). Don't silently
        // ship that either.
        var path = Path.Combine(Path.GetTempPath(), $"fcasrt_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[100]);
        try
        {
            Assert.Throws<IOException>(() =>
                FileCopier.AssertCopiedSizeMatches(path, expectedBytes: 50, relativePathForMessage: "x"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
