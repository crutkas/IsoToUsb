using IsoToUsb.Services;

namespace IsoToUsb.Tests;

[TestClass]
public class UsbBuildPipelineTests
{
    [TestMethod]
    public void VerifyAllSwmChunksLanded_Passes_When_All_Chunks_Match()
    {
        var temp = Directory.CreateTempSubdirectory("swmtemp_");
        var dest = Directory.CreateTempSubdirectory("swmdest_");
        try
        {
            WriteFixedSize(Path.Combine(temp.FullName, "install.swm"), 1024);
            WriteFixedSize(Path.Combine(temp.FullName, "install2.swm"), 512);
            WriteFixedSize(Path.Combine(dest.FullName, "install.swm"), 1024);
            WriteFixedSize(Path.Combine(dest.FullName, "install2.swm"), 512);

            // Must NOT throw.
            UsbBuildPipeline.VerifyAllSwmChunksLanded(temp.FullName, dest.FullName, "sources/install.wim");
        }
        finally
        {
            temp.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void VerifyAllSwmChunksLanded_Throws_When_A_Chunk_Is_Missing()
    {
        // Regression: real-world bug (2026-06-07) where the SWM phase
        // produced 2 chunks in temp but install2.swm never landed on the
        // USB, the pipeline reported green, and Windows Setup failed with
        // 0x8007000D mid-install. The new helper MUST throw.
        var temp = Directory.CreateTempSubdirectory("swmtemp_");
        var dest = Directory.CreateTempSubdirectory("swmdest_");
        try
        {
            WriteFixedSize(Path.Combine(temp.FullName, "install.swm"), 1024);
            WriteFixedSize(Path.Combine(temp.FullName, "install2.swm"), 512);
            WriteFixedSize(Path.Combine(dest.FullName, "install.swm"), 1024);
            // install2.swm intentionally missing from dest.

            var ex = Assert.Throws<IOException>(() =>
                UsbBuildPipeline.VerifyAllSwmChunksLanded(temp.FullName, dest.FullName, "sources/install.wim"));
            StringAssert.Contains(ex.Message, "install2.swm");
            StringAssert.Contains(ex.Message, "0x8007000D");
        }
        finally
        {
            temp.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void VerifyAllSwmChunksLanded_Throws_When_A_Chunk_Is_Truncated()
    {
        var temp = Directory.CreateTempSubdirectory("swmtemp_");
        var dest = Directory.CreateTempSubdirectory("swmdest_");
        try
        {
            // Numbers chosen to read like the real-world bug in the failure
            // message while keeping tmp disk usage tiny.
            WriteFixedSize(Path.Combine(temp.FullName, "install.swm"), 1024);
            WriteFixedSize(Path.Combine(temp.FullName, "install2.swm"), 3520);
            WriteFixedSize(Path.Combine(dest.FullName, "install.swm"), 1024);
            WriteFixedSize(Path.Combine(dest.FullName, "install2.swm"), 1168); // truncated

            var ex = Assert.Throws<IOException>(() =>
                UsbBuildPipeline.VerifyAllSwmChunksLanded(temp.FullName, dest.FullName, "sources/install.wim"));
            StringAssert.Contains(ex.Message, "install2.swm");
            StringAssert.Contains(ex.Message, "3,520");
            StringAssert.Contains(ex.Message, "1,168");
        }
        finally
        {
            temp.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void VerifyAllSwmChunksLanded_Reports_Both_Missing_And_Truncated_At_Once()
    {
        var temp = Directory.CreateTempSubdirectory("swmtemp_");
        var dest = Directory.CreateTempSubdirectory("swmdest_");
        try
        {
            WriteFixedSize(Path.Combine(temp.FullName, "install.swm"), 1024);
            WriteFixedSize(Path.Combine(temp.FullName, "install2.swm"), 1024);
            WriteFixedSize(Path.Combine(temp.FullName, "install3.swm"), 1024);
            WriteFixedSize(Path.Combine(dest.FullName, "install.swm"), 512); // truncated
            // install2.swm missing
            WriteFixedSize(Path.Combine(dest.FullName, "install3.swm"), 1024); // ok

            var ex = Assert.Throws<IOException>(() =>
                UsbBuildPipeline.VerifyAllSwmChunksLanded(temp.FullName, dest.FullName, "x.wim"));
            StringAssert.Contains(ex.Message, "install.swm");
            StringAssert.Contains(ex.Message, "install2.swm");
            StringAssert.Contains(ex.Message, "Missing chunks");
            StringAssert.Contains(ex.Message, "Truncated chunks");
        }
        finally
        {
            temp.Delete(recursive: true);
            dest.Delete(recursive: true);
        }
    }

    private static void WriteFixedSize(string path, long bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(bytes);
    }
}

