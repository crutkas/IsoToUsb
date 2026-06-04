using IsoToUsb.Services;

namespace IsoToUsb.Tests;

[TestClass]
public class SampleVerifierTests
{
    [TestMethod]
    public async Task Verify_Reports_Match_For_Identical_Files()
    {
        var src = Directory.CreateTempSubdirectory("verifysrc_");
        var dst = Directory.CreateTempSubdirectory("verifydst_");
        try
        {
            var srcFile = Path.Combine(src.FullName, "a.bin");
            var dstFile = Path.Combine(dst.FullName, "a.bin");
            var bytes = new byte[1024];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(srcFile, bytes);
            File.WriteAllBytes(dstFile, bytes);

            var verifier = new SampleVerifier { SampleSize = 5 };
            var results = await verifier.VerifyAsync(src.FullName, dst.FullName);
            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Match);
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Verify_Reports_Mismatch_For_Different_Files()
    {
        var src = Directory.CreateTempSubdirectory("verifysrc_");
        var dst = Directory.CreateTempSubdirectory("verifydst_");
        try
        {
            File.WriteAllText(Path.Combine(src.FullName, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(dst.FullName, "a.txt"), "world");

            var verifier = new SampleVerifier { SampleSize = 5 };
            var results = await verifier.VerifyAsync(src.FullName, dst.FullName);
            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Match);
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Verify_Reports_Missing_Destination_File()
    {
        var src = Directory.CreateTempSubdirectory("verifysrc_");
        var dst = Directory.CreateTempSubdirectory("verifydst_");
        try
        {
            File.WriteAllText(Path.Combine(src.FullName, "a.txt"), "hello");

            var verifier = new SampleVerifier { SampleSize = 5 };
            var results = await verifier.VerifyAsync(src.FullName, dst.FullName);
            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Match);
            StringAssert.Contains(results[0].Reason ?? string.Empty, "Missing");
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Verify_Skips_Excluded_Paths()
    {
        var src = Directory.CreateTempSubdirectory("verifysrc_");
        var dst = Directory.CreateTempSubdirectory("verifydst_");
        try
        {
            var sources = Directory.CreateDirectory(Path.Combine(src.FullName, "sources"));
            File.WriteAllText(Path.Combine(sources.FullName, "install.wim"), "huge");
            var dstSources = Directory.CreateDirectory(Path.Combine(dst.FullName, "sources"));
            File.WriteAllText(Path.Combine(dstSources.FullName, "install.swm"), "split");

            var verifier = new SampleVerifier { SampleSize = 5 };
            var results = await verifier.VerifyAsync(
                src.FullName,
                dst.FullName,
                skipRelativePaths: new[] { Path.Combine("sources", "install.wim") });
            Assert.AreEqual(0, results.Count);
        }
        finally
        {
            src.Delete(recursive: true);
            dst.Delete(recursive: true);
        }
    }
}
