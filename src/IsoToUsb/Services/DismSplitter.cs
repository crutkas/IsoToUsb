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
    /// <paramref name="destinationDirectory"/> named <c>install.swm</c>,
    /// <c>install2.swm</c>, etc.
    /// </summary>
    public async Task SplitAsync(
        string sourceWim,
        string destinationDirectory,
        int chunkSizeMb = DefaultChunkMb,
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

        var swmPath = Path.Combine(destinationDirectory, "install.swm");

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

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dism /Split-Image failed (exit code {process.ExitCode}). {stderrBuilder}");
        }
    }
}
