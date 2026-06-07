using System.Diagnostics;
using System.Text.RegularExpressions;

namespace IsoToUsb.Services;

/// <summary>Progress snapshot from <see cref="DismSplitter"/>.</summary>
public sealed record DismProgress(int Percent, string Line);

/// <summary>
/// Drives <c>dism.exe /Split-Image</c> to split a large <c>install.wim</c>
/// into 3800 MiB <c>install.swm</c> / <c>install*.swm</c> chunks that fit
/// on FAT32 (which is capped at 4 GiB per file).
/// </summary>
public sealed class DismSplitter
{
    private const int DefaultChunkMb = 3800;
    private static readonly Regex PercentRegex = new(@"(\d{1,3}(?:[.,]\d+)?)%", RegexOptions.Compiled);

    public string DismPath { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Dism.exe");

    /// <summary>
    /// Splits <paramref name="sourceWim"/> into chunks under
    /// <paramref name="destinationDirectory"/>. The first chunk is named
    /// after <paramref name="outputBaseName"/> (default: source file name
    /// with <c>.swm</c> extension) and subsequent chunks get a numeric
    /// suffix from dism (<c>name2.swm</c>, <c>name3.swm</c>, ...).
    /// </summary>
    public async Task SplitAsync(
        string sourceWim,
        string destinationDirectory,
        int chunkSizeMb = DefaultChunkMb,
        string? outputBaseName = null,
        IProgress<DismProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWim);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!File.Exists(sourceWim))
        {
            throw new FileNotFoundException("Source WIM not found.", sourceWim);
        }
        Directory.CreateDirectory(destinationDirectory);

        var baseName = string.IsNullOrWhiteSpace(outputBaseName)
            ? Path.GetFileNameWithoutExtension(sourceWim) + ".swm"
            : outputBaseName!;
        var swmPath = Path.Combine(destinationDirectory, baseName);

        // Source WIM size is a good estimator of the total SWM payload (chunks
        // are roughly equal in total to the source). Used to drive byte-based
        // progress instead of relying on dism's stdout regex, which often
        // sits at 0% for long stretches then jumps.
        long sourceBytes = 0;
        try { sourceBytes = new FileInfo(sourceWim).Length; } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = DismPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/English");
        psi.ArgumentList.Add("/Split-Image");
        psi.ArgumentList.Add($"/ImageFile:{sourceWim}");
        psi.ArgumentList.Add($"/SWMFile:{swmPath}");
        psi.ArgumentList.Add($"/FileSize:{chunkSizeMb}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stderrBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            var match = PercentRegex.Match(e.Data);
            if (match.Success && int.TryParse(match.Groups[1].Value.Split('.', ',')[0], out var pct))
            {
                progress?.Report(new DismProgress(Math.Clamp(pct, 0, 100), e.Data));
            }
            else
            {
                progress?.Report(new DismProgress(-1, e.Data));
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{DismPath}'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Make sure that a cancellation request actually kills dism.exe.
        // Without this, the parent task observes OperationCanceledException
        // immediately but dism keeps running in the background, holding the
        // SWM file handles open and blocking the next pipeline retry.
        await using var killOnCancel = cancellationToken.Register(static state =>
        {
            try
            {
                var p = (Process)state!;
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }, process);

        // Byte-poller: independent of dism's stdout, just totals the .swm
        // files in the destination directory every 500ms. Gives a smooth,
        // accurate progress signal even when dism stays quiet.
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pollTask = PollSplitSizeAsync(destinationDirectory, baseName, sourceBytes, progress, pollCts.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Wait (unbounded by the original token) for dism to actually
            // exit after the Kill above, then re-throw so callers see the
            // cancel just like they did before.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
        finally
        {
            pollCts.Cancel();
            try { await pollTask.ConfigureAwait(false); } catch { }
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dism /Split-Image failed (exit code {process.ExitCode}). {stderrBuilder}");
        }

        // Final 100% report so consumers see a clean end-of-stage signal
        // even if the poll loop missed the last write.
        progress?.Report(new DismProgress(100, "split complete"));
    }

    /// <summary>
    /// Polls the destination directory's <c>.swm</c> files and reports a
    /// byte-based percent against <paramref name="estimatedTotalBytes"/>
    /// (typically the source WIM size). Capped at 99% so the caller can
    /// emit a clean 100% only after dism actually exits successfully.
    /// </summary>
    private static async Task PollSplitSizeAsync(
        string destDir,
        string baseName,
        long estimatedTotalBytes,
        IProgress<DismProgress>? progress,
        CancellationToken pollToken)
    {
        if (estimatedTotalBytes <= 0 || progress is null)
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(baseName);
        var lastPct = -1;
        try
        {
            while (!pollToken.IsCancellationRequested)
            {
                long total = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(destDir, $"{stem}*.swm", SearchOption.TopDirectoryOnly))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                }
                catch
                {
                    // IO race during enumeration — just try again next tick.
                }

                if (total > 0)
                {
                    var pct = (int)Math.Clamp((double)total * 100.0 / estimatedTotalBytes, 0, 99);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        var mib = total / (1024.0 * 1024.0);
                        progress.Report(new DismProgress(pct, $"split: {mib:N0} MiB written ({pct}%)"));
                    }
                }

                try { await Task.Delay(500, pollToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch
        {
            // Don't let the poller bubble up an exception into the pipeline.
        }
    }
}
