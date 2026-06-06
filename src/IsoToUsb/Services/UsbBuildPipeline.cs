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

        // Pre-flight scan: classify every file on the ISO before doing
        // anything destructive. If any file is too large for FAT32 and
        // cannot be DISM-split, abort BEFORE the wipe so the user keeps
        // their data on the USB stick.
        cancellationToken.ThrowIfCancellationRequested();
        var rejected = ScanForFat32Rejects(mounted.MountRoot);
        if (rejected.Count > 0)
        {
            var first = rejected[0];
            throw new InvalidOperationException(
                $"This ISO contains files larger than 4 GiB that aren't WIM/ESD/SWM and therefore can't fit on FAT32. " +
                $"First offender: '{first.RelativePath}' ({first.SizeBytes:N0} bytes). " +
                $"IsoToUsb only writes FAT32 USBs, so this image isn't supported.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.Repartition, 0, $"Wiping and repartitioning {targetDisk.FriendlyName}..."));
        var usbRoot = Partitioner.Repartition(targetDisk);
        progress?.Report(new PipelineProgress(PipelineStage.Repartition, 100, $"Created FAT32 partition at {usbRoot}"));

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.CopyFiles, 0, "Copying ISO contents..."));
        var copier = new FileCopier { SkipPredicate = (file, rel) => FileCopier.ShouldSplitForFat32(file, rel) };
        var copyProgress = new Progress<CopyProgress>(p =>
        {
            var pct = p.BytesTotal > 0 ? (int)(p.BytesDone * 100 / p.BytesTotal) : 0;
            progress?.Report(new PipelineProgress(PipelineStage.CopyFiles, pct, $"{p.FilesDone}/{p.FilesTotal} {p.CurrentRelativePath}"));
        });
        var skipped = await copier.CopyAsync(mounted.MountRoot, usbRoot, copyProgress, cancellationToken).ConfigureAwait(false);

        if (skipped.Count > 0)
        {
            // Split each large WIM/ESD/SWM into FAT32-safe chunks. The
            // original install.wim is the common case, but Windows server
            // ISOs and slipstreamed images sometimes also ship boot.wim
            // close to the limit.
            for (int i = 0; i < skipped.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = skipped[i];
                progress?.Report(new PipelineProgress(
                    PipelineStage.SplitInstallWim,
                    0,
                    $"Splitting {rel} ({i + 1}/{skipped.Count}) with DISM..."));
                var sourceWim = Path.Combine(mounted.MountRoot, rel);
                var destDir = Path.Combine(usbRoot, Path.GetDirectoryName(rel) ?? string.Empty);
                var swmBase = Path.GetFileNameWithoutExtension(rel) + ".swm";
                var splitProgress = new Progress<DismProgress>(p =>
                {
                    if (p.Percent >= 0)
                    {
                        progress?.Report(new PipelineProgress(PipelineStage.SplitInstallWim, p.Percent, p.Line));
                    }
                });
                await Splitter.SplitAsync(
                    sourceWim,
                    destDir,
                    outputBaseName: swmBase,
                    progress: splitProgress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
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

    private readonly record struct RejectedFile(string RelativePath, long SizeBytes);

    /// <summary>
    /// Walks the mounted ISO looking for files that exceed FAT32's per-file
    /// limit and aren't split-capable WIM/ESD/SWM images. Returns each one
    /// so the pipeline can surface a clear pre-wipe error.
    /// </summary>
    private static IReadOnlyList<RejectedFile> ScanForFat32Rejects(string mountRoot)
    {
        var rejects = new List<RejectedFile>();
        var root = new DirectoryInfo(mountRoot);
        foreach (var f in root.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(mountRoot, f.FullName);
            if (FileCopier.ClassifyForFat32(f, rel) == Fat32FileAction.Reject)
            {
                rejects.Add(new RejectedFile(rel, f.Length));
            }
        }
        return rejects;
    }
}
