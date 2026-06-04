using System.IO.Pipes;
using System.Text;
using IsoToUsb.Services;

namespace IsoToUsb.Worker;

/// <summary>
/// Headless, elevated entry point that runs the destructive
/// <see cref="UsbBuildPipeline"/>. Driven by the non-elevated UI process via
/// a duplex named pipe. Never initializes WinUI/XAML.
/// </summary>
/// <remarks>
/// Invoked when <see cref="Program.Main"/> sees <c>--worker</c> in the
/// command line. The corresponding launcher in the UI process is
/// <see cref="ElevatedWorkerLauncher"/>.
/// </remarks>
internal static class PipelineWorker
{
    /// <summary>
    /// Parses worker args, connects to the parent pipe, runs the pipeline,
    /// streams progress, and returns the process exit code.
    /// </summary>
    /// <returns>0 on success, 1 on pipeline error, 2 on bad arguments.</returns>
    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var pipeName, out var isoPath, out var diskNumber, out var parseError))
        {
            // No pipe yet, so we can't report this back. Stderr is invisible
            // for a WinExe but the parent treats exit code 2 as arg error.
            System.Diagnostics.Debug.WriteLine($"[IsoToUsb worker] bad args: {parseError}");
            return 2;
        }

        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(10_000);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IsoToUsb worker] pipe connect failed: {ex.Message}");
            return 1;
        }

        // ReadMode.Byte by default; line-based via StreamReader/Writer.
        using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var cts = new CancellationTokenSource();
        using var watcherStop = new CancellationTokenSource();

        // Background task that listens for the parent's CANCEL command.
        // ReadLineAsync on a connected NamedPipeClientStream returns null
        // when the pipe is closed (parent exited or dropped its end).
        var cancelWatcher = Task.Run(async () =>
        {
            try
            {
                while (!watcherStop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(watcherStop.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }
                    if (line.Equals(WorkerProtocol.CancelCommand, StringComparison.Ordinal))
                    {
                        cts.Cancel();
                        return;
                    }
                }
            }
            catch
            {
                // Pipe died or watcher was stopped — treat as cancel so any
                // in-flight pipeline aborts cleanly.
                try { cts.Cancel(); } catch { }
            }
        });

        try
        {
            var disk = ResolveDisk(diskNumber);

            var pipeline = new UsbBuildPipeline();
            var progress = new Progress<PipelineProgress>(p =>
            {
                WriteProgress(writer, p);
            });

            var task = pipeline.RunAsync(isoPath, disk, progress, cts.Token);
            var results = task.GetAwaiter().GetResult();

            var failures = results.Count(r => !r.Match);
            WriteResult(writer, results.Count, failures);
            return 0;
        }
        catch (OperationCanceledException)
        {
            WriteError(writer, nameof(OperationCanceledException), "Canceled by user.");
            return 1;
        }
        catch (Exception ex)
        {
            WriteError(writer, ex.GetType().Name, ex.Message);
            return 1;
        }
        finally
        {
            try { writer.Flush(); } catch { }
            try { watcherStop.Cancel(); } catch { }
            // Give the background reader a moment to observe cancellation and
            // unwind before the using blocks tear the pipe down underneath it.
            try { cancelWatcher.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try { cts.Cancel(); } catch { }
        }
    }

    private static DiskInfo ResolveDisk(uint diskNumber)
    {
        var all = UsbDriveEnumerator.EnumerateAllDisks();
        var disk = all.FirstOrDefault(d => d.Number == diskNumber)
            ?? throw new InvalidOperationException(
                $"Disk number {diskNumber} not found. The device may have been unplugged.");

        if (!UsbDriveEnumerator.IsTargetable(disk))
        {
            throw new InvalidOperationException(
                $"Disk {disk.Number} '{disk.FriendlyName}' is not a safe USB target. " +
                "Aborting before any destructive operation.");
        }
        return disk;
    }

    private static void WriteProgress(StreamWriter writer, PipelineProgress p)
    {
        try
        {
            writer.WriteLine(string.Concat(
                WorkerProtocol.Progress, "\t",
                StageNames.From(p.Stage), "\t",
                p.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture), "\t",
                WorkerProtocol.Sanitize(p.Message)));
        }
        catch
        {
            // Pipe may be broken; nothing to do here.
        }
    }

    private static void WriteResult(StreamWriter writer, int total, int failures)
    {
        try
        {
            writer.WriteLine(string.Concat(
                WorkerProtocol.Result, "\t",
                total.ToString(System.Globalization.CultureInfo.InvariantCulture), "\t",
                failures.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch
        {
        }
    }

    private static void WriteError(StreamWriter writer, string typeName, string message)
    {
        try
        {
            writer.WriteLine(string.Concat(
                WorkerProtocol.Error, "\t",
                WorkerProtocol.Sanitize(typeName), "\t",
                WorkerProtocol.Sanitize(message)));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Parses <c>--worker --pipe &lt;name&gt; --iso &lt;path&gt; --disk &lt;number&gt;</c>.
    /// </summary>
    private static bool TryParseArgs(
        string[] args,
        out string pipeName,
        out string isoPath,
        out uint diskNumber,
        out string error)
    {
        pipeName = string.Empty;
        isoPath = string.Empty;
        diskNumber = 0;
        error = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Length:
                    pipeName = args[++i];
                    break;
                case "--iso" when i + 1 < args.Length:
                    isoPath = args[++i];
                    break;
                case "--disk" when i + 1 < args.Length:
                    if (!uint.TryParse(args[++i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out diskNumber))
                    {
                        error = "invalid --disk value";
                        return false;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName)) { error = "missing --pipe"; return false; }
        if (string.IsNullOrWhiteSpace(isoPath)) { error = "missing --iso"; return false; }
        return true;
    }
}
