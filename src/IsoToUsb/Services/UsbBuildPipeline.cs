namespace IsoToUsb.Services;

/// <summary>
/// Identifies which stage is currently running for UI display.
/// </summary>
public enum PipelineStage
{
    ValidateInputs,
    MountIso,
    Repartition,
    CopyFiles,
    SplitInstallWim,
    Verify,
    Eject,
    Done,
}

/// <summary>
/// Progress event from <see cref="UsbBuildPipeline"/>.
/// <paramref name="Percent"/> may be -1 when a stage cannot report a percent.
/// </summary>
public sealed record PipelineProgress(PipelineStage Stage, int Percent, string Message);

/// <summary>
/// Orchestrates the end-to-end "ISO → bootable USB" build using the
/// individual service classes. Honors cancellation between stages.
/// </summary>
public sealed class UsbBuildPipeline
{
    public FileCopier FileCopier { get; init; } = new();
    public DiskPartitioner Partitioner { get; init; } = new();
    public DismSplitter Splitter { get; init; } = new();
    public SampleVerifier Verifier { get; init; } = new();

    public async Task<IReadOnlyList<VerificationResult>> RunAsync(
        string isoPath,
        DiskInfo targetDisk,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentNullException.ThrowIfNull(targetDisk);

        if (!UsbDriveEnumerator.IsTargetable(targetDisk))
        {
            throw new InvalidOperationException(
                $"Disk '{targetDisk.FriendlyName}' is not a safe USB target. " +
                "Aborting before any destructive operation.");
        }

        progress?.Report(new PipelineProgress(PipelineStage.ValidateInputs, 0, "Validating ISO and target..."));
        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException("ISO not found.", isoPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.MountIso, 0, $"Mounting '{Path.GetFileName(isoPath)}'..."));
        using var mounted = IsoMounter.Mount(isoPath);
        IsoContentValidator.EnsureWindowsInstallIso(mounted.MountRoot);
        progress?.Report(new PipelineProgress(PipelineStage.MountIso, 100, $"Mounted at {mounted.MountRoot}"));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.Repartition, 0, $"Wiping and repartitioning {targetDisk.FriendlyName}..."));
        var usbRoot = Partitioner.Repartition(targetDisk);
        progress?.Report(new PipelineProgress(PipelineStage.Repartition, 100, $"Created FAT32 partition at {usbRoot}"));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.CopyFiles, 0, "Copying ISO contents..."));
        var copier = new FileCopier { SkipPredicate = (file, rel) => FileCopier.ShouldSkipInstallWimForFat32(file, rel) };
        var copyProgress = new Progress<CopyProgress>(p =>
        {
            var pct = p.BytesTotal > 0 ? (int)(p.BytesDone * 100 / p.BytesTotal) : 0;
            progress?.Report(new PipelineProgress(PipelineStage.CopyFiles, pct, $"{p.FilesDone}/{p.FilesTotal} {p.CurrentRelativePath}"));
        });
        var skipped = await copier.CopyAsync(mounted.MountRoot, usbRoot, copyProgress, cancellationToken).ConfigureAwait(false);

        if (skipped.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PipelineProgress(PipelineStage.SplitInstallWim, 0, "Splitting install.wim (>4 GiB) with DISM..."));
            var sourceWim = Path.Combine(mounted.MountRoot, "sources", "install.wim");
            var destSourcesDir = Path.Combine(usbRoot, "sources");
            var splitProgress = new Progress<DismProgress>(p =>
            {
                if (p.Percent >= 0)
                {
                    progress?.Report(new PipelineProgress(PipelineStage.SplitInstallWim, p.Percent, p.Line));
                }
            });
            await Splitter.SplitAsync(sourceWim, destSourcesDir, progress: splitProgress, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.Verify, 0, $"Verifying {Verifier.SampleSize} random files..."));
        var verificationResults = await Verifier
            .VerifyAsync(mounted.MountRoot, usbRoot, skipped, cancellationToken)
            .ConfigureAwait(false);
        var failures = verificationResults.Count(r => !r.Match);
        if (failures > 0)
        {
            progress?.Report(new PipelineProgress(PipelineStage.Verify, 100, $"WARNING: {failures} file(s) mismatched."));
        }
        else
        {
            progress?.Report(new PipelineProgress(PipelineStage.Verify, 100, "All sampled files match."));
        }

        progress?.Report(new PipelineProgress(PipelineStage.Done, 100, "USB build complete."));
        return verificationResults;
    }
}
