using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using IsoToUsb.Worker;

namespace IsoToUsb.Services;

/// <summary>
/// Final outcome reported by the elevated worker process.
/// </summary>
public sealed record WorkerOutcome(
    bool Success,
    int TotalSampled,
    int Failures,
    string? ErrorType,
    string? ErrorMessage);

/// <summary>
/// Launches the current executable elevated with <c>--worker</c> and
/// communicates with it over a duplex named pipe. The UI process stays
/// at <c>asInvoker</c> so the WinRT file picker and Explorer drag-drop
/// keep working; only the disk-touching pipeline runs as administrator.
/// </summary>
public static class ElevatedWorkerLauncher
{
    /// <summary>
    /// Triggered when the worker writes a progress line. Always raised on a
    /// thread-pool thread — caller is responsible for marshalling to the UI
    /// thread (the existing <see cref="IProgress{T}"/> contract handles it).
    /// </summary>
    private static void OnProgress(IProgress<PipelineProgress>? progress, PipelineProgress p)
    {
        progress?.Report(p);
    }

    /// <summary>
    /// Runs the pipeline in an elevated child process.
    /// </summary>
    /// <param name="isoPath">Absolute path to the source ISO.</param>
    /// <param name="targetDisk">Pre-validated USB target disk.</param>
    /// <param name="progress">Receives progress events streamed from the worker.</param>
    /// <param name="cancellationToken">When canceled, sends <c>CANCEL</c> to the worker and waits.</param>
    /// <returns>The worker's final outcome — never throws for pipeline errors (they're reported on the result).</returns>
    /// <exception cref="OperationCanceledException">If launch itself was canceled.</exception>
    /// <exception cref="InvalidOperationException">If the user denied UAC or the worker exe is missing.</exception>
    public static async Task<WorkerOutcome> RunAsync(
        string isoPath,
        DiskInfo targetDisk,
        IProgress<PipelineProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentNullException.ThrowIfNull(targetDisk);

        var workerExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine current executable path.");

        var pipeName = "IsoToUsb-" + Guid.NewGuid().ToString("N");

        // Create the server BEFORE spawning the worker so the worker's
        // Connect() call always finds it.
        await using var server = new NamedPipeServerStream(
            pipeName: pipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous);

        // ShellExecute with Verb="runas" — raises UAC. We can't redirect
        // stdio with ShellExecute, which is exactly why we use a named pipe.
        var psi = new ProcessStartInfo
        {
            FileName = workerExe,
            UseShellExecute = true,
            Verb = IsAlreadyElevated() ? string.Empty : "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--worker");
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--iso");
        psi.ArgumentList.Add(isoPath);
        psi.ArgumentList.Add("--disk");
        psi.ArgumentList.Add(targetDisk.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Process? worker;
        try
        {
            worker = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 0x800704C7 || ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user clicked No on the UAC prompt.
            throw new InvalidOperationException("Elevation was declined. The disk operations need administrator rights to run.", ex);
        }

        try
        {
            // Wait for the worker to connect. Bounded so we don't hang
            // forever if the elevated child failed to start cleanly.
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await server.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Elevated worker did not connect within 15 seconds.");
                }
            }

            using var reader = new StreamReader(server, new System.Text.UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

            // If the user cancels mid-run, write CANCEL to the pipe; the
            // worker reads it and signals its CancellationTokenSource.
            using var cancelRegistration = cancellationToken.Register(() =>
            {
                try { writer.WriteLine(WorkerProtocol.CancelCommand); }
                catch { /* pipe may be torn down */ }
            });

            WorkerOutcome? outcome = null;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var parts = line.Split('\t');
                switch (parts[0])
                {
                    case WorkerProtocol.Progress when parts.Length >= 4:
                        if (int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            OnProgress(progress, new PipelineProgress(StageNames.Parse(parts[1]), pct, parts[3]));
                        }
                        break;

                    case WorkerProtocol.Log when parts.Length >= 2:
                        OnProgress(progress, new PipelineProgress(PipelineStage.ValidateInputs, -1, parts[1]));
                        break;

                    case WorkerProtocol.Result when parts.Length >= 3:
                        if (int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var total) &&
                            int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var failures))
                        {
                            outcome = new WorkerOutcome(true, total, failures, null, null);
                        }
                        break;

                    case WorkerProtocol.Error when parts.Length >= 3:
                        outcome = new WorkerOutcome(false, 0, 0, parts[1], parts[2]);
                        break;
                }
            }

            await worker.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            if (outcome is not null)
            {
                return outcome;
            }

            return worker.ExitCode == 0
                ? new WorkerOutcome(true, 0, 0, null, null)
                : new WorkerOutcome(false, 0, 0, "WorkerExit", $"Worker exited with code {worker.ExitCode} without reporting a result.");
        }
        finally
        {
            try { worker.Dispose(); } catch { }
        }
    }

    private static bool IsAlreadyElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
