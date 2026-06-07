using IsoToUsb.Services;
using IsoToUsb.ViewModels;
using IsoToUsb.Worker;

namespace IsoToUsb.Tests;

/// <summary>
/// Pins the per-byte log-noise gate behaviour. Exercises both the
/// "first tick for a new file always logs" rule and the
/// "intra-file ticks suppress until byteGate bytes pass" rule, plus
/// thread safety since the worker process has no SynchronizationContext
/// and Progress&lt;T&gt; dispatches gate calls to thread-pool threads.
/// </summary>
[TestClass]
public class HeartbeatGateTests
{
    [TestMethod]
    public void First_Tick_For_New_File_Is_Never_Heartbeat()
    {
        var gate = new UsbBuildPipeline.HeartbeatGate(byteGate: 256L * 1024 * 1024);

        Assert.IsFalse(gate.ShouldHeartbeat("sources/boot.wim", 0),
            "First tick for a new file must log.");
        Assert.IsFalse(gate.ShouldHeartbeat("sources/install.wim", 0),
            "First tick for a different file must also log.");
    }

    [TestMethod]
    public void Intra_File_Ticks_Under_Gate_Are_Heartbeats()
    {
        var gate = new UsbBuildPipeline.HeartbeatGate(byteGate: 256L * 1024 * 1024);
        Assert.IsFalse(gate.ShouldHeartbeat("install.wim", 0));   // initial: log

        Assert.IsTrue(gate.ShouldHeartbeat("install.wim", 10 * 1024 * 1024), "10 MiB delta < 256 MiB gate, expect heartbeat.");
        Assert.IsTrue(gate.ShouldHeartbeat("install.wim", 50 * 1024 * 1024), "50 MiB delta < 256 MiB gate, expect heartbeat.");
        Assert.IsTrue(gate.ShouldHeartbeat("install.wim", 200 * 1024 * 1024), "200 MiB delta < 256 MiB gate, expect heartbeat.");
    }

    [TestMethod]
    public void Intra_File_Tick_At_Or_Above_Gate_Logs_Then_Resets()
    {
        var gate = new UsbBuildPipeline.HeartbeatGate(byteGate: 256L * 1024 * 1024);
        Assert.IsFalse(gate.ShouldHeartbeat("install.wim", 0));   // initial
        Assert.IsFalse(gate.ShouldHeartbeat("install.wim", 300L * 1024 * 1024),
            "Crossing the byteGate threshold must log, not heartbeat.");
        // After logging at 300 MiB, next tick at 350 MiB is only +50 MiB → heartbeat.
        Assert.IsTrue(gate.ShouldHeartbeat("install.wim", 350L * 1024 * 1024));
        Assert.IsFalse(gate.ShouldHeartbeat("install.wim", 600L * 1024 * 1024),
            "Another 300 MiB delta past the last log must log again.");
    }

    [TestMethod]
    public void File_Change_Resets_The_Byte_Counter()
    {
        var gate = new UsbBuildPipeline.HeartbeatGate(byteGate: 256L * 1024 * 1024);
        Assert.IsFalse(gate.ShouldHeartbeat("a.wim", 0));
        Assert.IsFalse(gate.ShouldHeartbeat("a.wim", 300L * 1024 * 1024));
        // Switch to a different file — first tick logs.
        Assert.IsFalse(gate.ShouldHeartbeat("b.wim", 0));
        // The counter for "b.wim" started at 0, so a 100 MiB tick is below the gate.
        Assert.IsTrue(gate.ShouldHeartbeat("b.wim", 100L * 1024 * 1024));
    }

    [TestMethod]
    public void Gate_Is_Thread_Safe_Under_Concurrent_Callers()
    {
        // Smoke test for the lock. Many threads spamming the gate must not
        // throw or corrupt state (the pre-lock version had unsynchronized
        // field writes that could in principle tear/race).
        var gate = new UsbBuildPipeline.HeartbeatGate(byteGate: 1024);
        var done = new ManualResetEventSlim();
        var errors = 0;
        var threads = new List<Thread>();
        for (int t = 0; t < 8; t++)
        {
            int seed = t;
            threads.Add(new Thread(() =>
            {
                var rng = new Random(seed);
                for (int i = 0; i < 5000; i++)
                {
                    try
                    {
                        gate.ShouldHeartbeat(seed % 2 == 0 ? "a" : "b", rng.Next(0, 1_000_000));
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }));
        }
        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();
        Assert.AreEqual(0, errors);
    }
}

/// <summary>
/// Pins the wire-protocol sanitization step. The protocol is line- and
/// tab-delimited, so any tab/CR/LF in a user-facing message would break
/// parsing on the parent side.
/// </summary>
[TestClass]
public class WorkerProtocolTests
{
    [TestMethod]
    public void Sanitize_Replaces_Tab_Cr_Lf_With_Spaces()
    {
        Assert.AreEqual("hello world",
            WorkerProtocol.Sanitize("hello\tworld"));
        Assert.AreEqual("line1 line2",
            WorkerProtocol.Sanitize("line1\nline2"));
        Assert.AreEqual("a b c",
            WorkerProtocol.Sanitize("a\rb\nc"));
        Assert.AreEqual("mixed a b c d",
            WorkerProtocol.Sanitize("mixed\ta\rb\nc\td"));
    }

    [TestMethod]
    public void Sanitize_Passes_Through_Safe_Content()
    {
        Assert.AreEqual("nothing to do here",
            WorkerProtocol.Sanitize("nothing to do here"));
        Assert.AreEqual(@"C:\Users\me\image.iso",
            WorkerProtocol.Sanitize(@"C:\Users\me\image.iso"));
    }

    [TestMethod]
    public void Sanitize_Handles_Null_And_Empty()
    {
        Assert.AreEqual(string.Empty, WorkerProtocol.Sanitize(null));
        Assert.AreEqual(string.Empty, WorkerProtocol.Sanitize(string.Empty));
    }
}

/// <summary>
/// Pins the glyph that maps to each PhaseStatus. The Cancelled arm in
/// particular shipped silently and would regress unnoticed without a test.
/// </summary>
[TestClass]
public class PhaseItemTests
{
    [TestMethod]
    [DataRow(PhaseStatus.Pending, "\uEA3A")]
    [DataRow(PhaseStatus.Done, "\uE73E")]
    [DataRow(PhaseStatus.Skipped, "\uE738")]
    [DataRow(PhaseStatus.Failed, "\uE894")]
    [DataRow(PhaseStatus.Cancelled, "\uE711")]
    public void Glyph_Matches_Expected_Per_Status(PhaseStatus status, string expected)
    {
        var p = new PhaseItem("Some phase", "Some", "\uE7C5") { Status = status };
        Assert.AreEqual(expected, p.Glyph);
    }

    [TestMethod]
    public void Glyph_Is_Empty_For_Running_So_ProgressRing_Renders_Instead()
    {
        var p = new PhaseItem("Some phase", "Some", "\uE7C5") { Status = PhaseStatus.Running };
        Assert.AreEqual(string.Empty, p.Glyph);
        Assert.IsTrue(p.IsRunning);
        Assert.IsFalse(p.HasGlyph);
    }

    [TestMethod]
    public void Status_Change_Notifies_Glyph_And_IsRunning()
    {
        var p = new PhaseItem("Some phase", "Some", "\uE7C5");
        var notifiedProps = new List<string?>();
        p.PropertyChanged += (_, e) => notifiedProps.Add(e.PropertyName);

        p.Status = PhaseStatus.Running;
        CollectionAssert.Contains(notifiedProps, "Glyph");
        CollectionAssert.Contains(notifiedProps, "IsRunning");
        CollectionAssert.Contains(notifiedProps, "HasGlyph");
    }
}
