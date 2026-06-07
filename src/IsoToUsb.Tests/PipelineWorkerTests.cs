using IsoToUsb.Worker;

namespace IsoToUsb.Tests;

[TestClass]
public class PipelineWorkerTests
{
    [TestMethod]
    public async Task WatchForCancelOrDisconnectAsync_Cancels_Pipeline_On_Disconnect()
    {
        // A StringReader's ReadLineAsync returns null immediately at EOS,
        // which mirrors the named-pipe disconnect case (parent UI killed).
        using var reader = new StringReader(string.Empty);
        using var pipelineCts = new CancellationTokenSource();
        using var stopCts = new CancellationTokenSource();

        await PipelineWorker.WatchForCancelOrDisconnectAsync(reader, pipelineCts, stopCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(pipelineCts.IsCancellationRequested,
            "Worker must cancel its pipeline CTS when the controlling pipe closes, " +
            "otherwise an orphaned elevated worker keeps doing destructive disk I/O.");
    }

    [TestMethod]
    public async Task WatchForCancelOrDisconnectAsync_Cancels_Pipeline_On_Cancel_Command()
    {
        using var reader = new StringReader(WorkerProtocol.CancelCommand + "\n");
        using var pipelineCts = new CancellationTokenSource();
        using var stopCts = new CancellationTokenSource();

        await PipelineWorker.WatchForCancelOrDisconnectAsync(reader, pipelineCts, stopCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(pipelineCts.IsCancellationRequested,
            "Explicit CANCEL command must still cancel the pipeline.");
    }

    [TestMethod]
    public async Task WatchForCancelOrDisconnectAsync_Cancels_Pipeline_On_Read_Exception()
    {
        // ThrowingReader simulates a broken pipe (IOException) during read.
        using var pipelineCts = new CancellationTokenSource();
        using var stopCts = new CancellationTokenSource();

        await PipelineWorker.WatchForCancelOrDisconnectAsync(new ThrowingReader(), pipelineCts, stopCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(pipelineCts.IsCancellationRequested,
            "Any read exception (broken pipe etc.) must coerce into a pipeline cancel.");
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string? ReadLine() => throw new IOException("simulated broken pipe");
        public override Task<string?> ReadLineAsync() => throw new IOException("simulated broken pipe");
    }
}
