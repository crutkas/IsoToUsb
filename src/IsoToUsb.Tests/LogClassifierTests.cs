using IsoToUsb.ViewModels;

namespace IsoToUsb.Tests;

/// <summary>
/// Pins the colour buckets the log keyword renders in. The
/// classifier is a heuristic, but the heuristic was previously a
/// naive substring contains-check which mis-classified perfectly
/// healthy CopyFiles lines as Error because filenames like
/// <c>failovercluster-clussvc-replacement.man</c> contain the
/// substring "fail". These tests pin the word-boundary regex fix.
/// </summary>
[TestClass]
public class LogClassifierTests
{
    // ---- The screenshot lines: every one of these is a healthy copy and ----
    // ---- must classify as Action (green keyword), NOT Error (red). ----
    // ---- "failoverCluster" was the regression: substring "fail" matched. ----

    [TestMethod]
    [DataRow(@"[CopyFiles] 483/967 sources\replacementmanifests\extensibleauthenticationprotocolhostservice-rep.man")]
    [DataRow(@"[CopyFiles] 484/967 sources\replacementmanifests\failovercluster-clussvc-replacement.man")]
    [DataRow(@"[CopyFiles] 485/967 sources\replacementmanifests\failovercluster-core-wow64-rm.man")]
    [DataRow(@"[CopyFiles] 486/967 sources\replacementmanifests\failovercluster-updating-powershellcore-replacement.man")]
    [DataRow(@"[CopyFiles] 487/967 sources\replacementmanifests\feclient-replacement-th.man")]
    [DataRow(@"[CopyFiles] 488/967 sources\replacementmanifests\fidocredprov_dll_repl.man")]
    [DataRow(@"[CopyFiles] 489/967 sources\replacementmanifests\fileserver-replacement.man")]
    [DataRow(@"[CopyFiles] 490/967 sources\replacementmanifests\font-truetype-fontsregistrysettingsmigration-replacement.man")]
    public void CopyFiles_progress_lines_are_Action_not_Error(string line)
    {
        var (_, _, severity) = MainViewModel.ParseLogContent(line);
        Assert.AreEqual(LogSeverity.Action, severity, $"Healthy copy progress line should NOT classify as Error: {line}");
    }

    // ---- Real failure lines stay Error. ----

    [TestMethod]
    [DataRow("Failed to enumerate drives: Access denied.")]
    [DataRow("ERROR: COMException: 0x80004005")]
    [DataRow("[Verify] sampling aborted: 3 mismatched files.")]
    [DataRow("Exception thrown during partition step.")]
    [DataRow("[CopyFiles] 4/100 fileA.man — copy failed after 3 retries.")]
    public void Real_failure_lines_classify_as_Error(string line)
    {
        var (_, _, severity) = MainViewModel.ParseLogContent(line);
        Assert.AreEqual(LogSeverity.Error, severity);
    }

    // ---- Real warning lines stay Warn. ----

    [TestMethod]
    [DataRow("Warning: long path detected, falling back to UNC prefix.")]
    [DataRow("[CopyFiles] 5/100 fileB.man — skipped (will be split).")]
    [DataRow("Fallback FAT32 formatter selected.")]
    public void Real_warning_lines_classify_as_Warn(string line)
    {
        var (_, _, severity) = MainViewModel.ParseLogContent(line);
        Assert.AreEqual(LogSeverity.Warn, severity);
    }

    // ---- Action lines (the default) keep popping. ----

    [TestMethod]
    [DataRow("[ValidateInputs] ISO checksum OK · 5.42 GB")]
    [DataRow("[MountIso] mounted at G:\\")]
    [DataRow("Selected ISO: D:\\images\\Win11.iso")]
    public void Plain_progress_lines_are_Action(string line)
    {
        var (_, _, severity) = MainViewModel.ParseLogContent(line);
        Assert.AreEqual(LogSeverity.Action, severity);
    }
}
