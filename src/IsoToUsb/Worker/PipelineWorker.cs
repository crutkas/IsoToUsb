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
        if (!TryParseArgs(args, out var parsed, out var parseError))
        {
            // No pipe yet, so we can't report this back. Stderr is invisible
            // for a WinExe but the parent treats exit code 2 as arg error.
            System.Diagnostics.Debug.WriteLine($"[IsoToUsb worker] bad args: {parseError}");
            return 2;
        }

        using var pipe = new NamedPipeClientStream(
            serverName: ".",
            pipeName: parsed.PipeName,
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
        // Progress callbacks marshal arbitrary thread-pool threads into the
        // writer; wrap once so every WriteLine is atomic.
        var rawWriter = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
        using var disposeRawWriter = rawWriter;
        var writer = TextWriter.Synchronized(rawWriter);
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
            var disk = ResolveDisk(parsed);

            var pipeline = new UsbBuildPipeline();
            var progress = new Progress<PipelineProgress>(p =>
            {
                WriteProgress(writer, p);
            });

            var task = pipeline.RunAsync(parsed.IsoPath, disk, progress, cts.Token);
            var results = task.GetAwaiter().GetResult();

            var failures = results.Count(r => !r.Match);
            WriteResult(writer, results.Count, failures);
            return 0;
        }
        catch (OperationCanceledException)
        {
            WriteCanceled(writer, "Canceled by user.");
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

    /// <summary>
    /// Looks up the current <see cref="DiskInfo"/> for the disk number that
    /// the UI selected and verifies that its identity (serial + size) still
    /// matches what the UI saw. This closes a TOCTOU window where a user
    /// could unplug the chosen stick between selection and the worker
    /// starting, and a different USB device could land on the same disk
    /// number.
    /// </summary>
    private static DiskInfo ResolveDisk(WorkerArgs args)
    {
        var all = UsbDriveEnumerator.EnumerateAllDisks();
        var disk = all.FirstOrDefault(d => d.Number == args.DiskNumber)
            ?? throw new InvalidOperationException(
                $"Disk number {args.DiskNumber} not found. The device may have been unplugged.");

        if (!UsbDriveEnumerator.IsTargetable(disk))
        {
            throw new InvalidOperationException(
                $"Disk {disk.Number} '{disk.FriendlyName}' is not a safe USB target. " +
                "Aborting before any destructive operation.");
        }

        if (!string.IsNullOrEmpty(args.ExpectedSerial)
            && !string.Equals(disk.SerialNumber, args.ExpectedSerial, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Disk {disk.Number} serial mismatch. " +
                $"Expected '{args.ExpectedSerial}' but found '{disk.SerialNumber}'. " +
                "The device on this disk number changed since you selected it. Aborting.");
        }

        if (args.ExpectedSize != 0 && disk.SizeBytes != args.ExpectedSize)
        {
            throw new InvalidOperationException(
                $"Disk {disk.Number} size mismatch. " +
                $"Expected {args.ExpectedSize:N0} bytes but found {disk.SizeBytes:N0}. " +
                "The device on this disk number changed since you selected it. Aborting.");
        }
        return disk;
    }

    private static void WriteProgress(TextWriter writer, PipelineProgress p)
    {
        try
        {
            writer.WriteLine(string.Concat(
                WorkerProtocol.Progress, "\t",
                StageNames.From(p.Stage), "\t",
                p.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture), "\t",
                WorkerProtocol.Sanitize(p.Message),
                p.IsHeartbeat ? "\t1" : string.Empty));
        }
        catch
        {
            // Pipe may be broken; nothing to do here.
        }
    }

    private static void WriteResult(TextWriter writer, int total, int failures)
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

    private static void WriteError(TextWriter writer, string typeName, string message)
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

    private static void WriteCanceled(TextWriter writer, string message)
    {
        try
        {
            writer.WriteLine(string.Concat(
                WorkerProtocol.Canceled, "\t",
                WorkerProtocol.Sanitize(message)));
        }
        catch
        {
        }
    }

    private readonly record struct WorkerArgs(
        string PipeName,
        string IsoPath,
        uint DiskNumber,
        string ExpectedSerial,
        ulong ExpectedSize);

    /// <summary>
    /// Parses <c>--worker --pipe &lt;name&gt; --iso &lt;path&gt; --disk &lt;number&gt;
    /// [--serial &lt;s&gt;] [--size &lt;bytes&gt;]</c>. Serial and size are
    /// optional only for backward compat; the launcher always sends them.
    /// </summary>
    private static bool TryParseArgs(
        string[] args,
        out WorkerArgs parsed,
        out string error)
    {
        string pipeName = string.Empty;
        string isoPath = string.Empty;
        uint diskNumber = 0;
        string expectedSerial = string.Empty;
        ulong expectedSize = 0;
        error = string.Empty;
        parsed = default;

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
                case "--serial" when i + 1 < args.Length:
                    expectedSerial = args[++i];
                    break;
                case "--size" when i + 1 < args.Length:
                    if (!ulong.TryParse(args[++i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out expectedSize))
                    {
                        error = "invalid --size value";
                        return false;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName)) { error = "missing --pipe"; return false; }
        if (string.IsNullOrWhiteSpace(isoPath)) { error = "missing --iso"; return false; }
        parsed = new WorkerArgs(pipeName, isoPath, diskNumber, expectedSerial, expectedSize);
        return true;
    }
}
