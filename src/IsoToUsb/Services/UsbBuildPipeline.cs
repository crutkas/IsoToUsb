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
/// <summary>
/// One progress tick from the pipeline.
/// <para>
/// <see cref="IsHeartbeat"/> is <c>true</c> for intra-file byte-progress
/// updates (~5 Hz from <see cref="FileCopier"/>): the UI should advance
/// the progress bar and the live status pill but NOT append a fresh log
/// line. Otherwise a 400 MB boot.wim would produce 30+ identical log
/// lines and read as if the copy were stuck. Non-heartbeat events fire
/// once per file boundary and per stage transition — those are the
/// log-worthy ticks.
/// </para>
/// </summary>
public sealed record PipelineProgress(PipelineStage Stage, int Percent, string Message, bool IsHeartbeat = false);

/// <summary>
/// Orchestrates the end-to-end "ISO → bootable USB" build using the
/// individual service classes. Honors cancellation between stages.
/// </summary>
public sealed class UsbBuildPipeline
{
    /// <summary>
    /// Coarse byte gate that decides when an in-flight copy emits a fresh
    /// LOG line (vs. a silent progress-bar heartbeat). 50 MiB gives a log
    /// line about every 5 seconds on a typical USB 3.0 stick (~10 MB/s)
    /// so a 3.5 GiB SWM produces ~70 motion lines instead of the ~14 that
    /// 256 MiB used to produce (which felt stuck for 25+ seconds between
    /// entries even though the bottom bar kept moving).
    /// </summary>
    private const long LogByteGate = 50L * 1024 * 1024;

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
        var copyGate = new HeartbeatGate(LogByteGate);
        var copyProgress = new Progress<CopyProgress>(p =>
        {
            var pct = p.BytesTotal > 0 ? (int)(p.BytesDone * 100 / p.BytesTotal) : 0;
            // 1-based "currently working on file X of N" — much friendlier
            // than "0/N done" while the first file is mid-stream.
            var current = Math.Min(p.FilesDone + 1, p.FilesTotal);
            var doneMb = p.BytesDone / (1024 * 1024);
            var totalMb = p.BytesTotal / (1024 * 1024);
            // FileCopier fires ~5 Hz during each file's byte stream. To keep
            // the log readable we log the FIRST tick for a new file AND a
            // milestone every LogByteGate (50 MiB ≈ one line every ~5s on a
            // 10 MB/s USB stick). Everything in between is a heartbeat that
            // only advances the bar / status pill. Small files get exactly
            // one log line; multi-GB SWMs get ~70 motion lines per 3.5 GB
            // chunk so the log keeps pace with the bottom progress bar
            // instead of feeling stuck for 25+ seconds between entries.
            // Gate state is locked because Progress<T> dispatches to
            // thread-pool threads without a sync context in the worker
            // process.
            var isHeartbeat = copyGate.ShouldHeartbeat(p.CurrentRelativePath, p.BytesDone);
            progress?.Report(new PipelineProgress(
                PipelineStage.CopyFiles,
                pct,
                $"{current}/{p.FilesTotal} {p.CurrentRelativePath} · {doneMb}/{totalMb} MB",
                isHeartbeat));
        });
        var skipped = await copier.CopyAsync(mounted.MountRoot, usbRoot, copyProgress, cancellationToken).ConfigureAwait(false);

        if (skipped.Count > 0)
        {
            // Split each large WIM/ESD/SWM into FAT32-safe chunks. The
            // original install.wim is the common case, but Windows server
            // ISOs and slipstreamed images sometimes also ship boot.wim
            // close to the limit.
            //
            // Strategy: split to a local temp directory on the system drive
            // (NVMe-fast, ~1-3 GB/s) instead of directly to USB FAT32
            // (~30-150 MB/s). DISM's small synchronous writes are especially
            // slow on FAT32; running the split phase locally is typically
            // 10x+ faster overall. The resulting SWMs are then copied to the
            // USB with our own FileCopier (1 MiB sequential buffers + great
            // per-byte progress).
            //
            // Progress mapping within the SplitInstallWim stage:
            //   0..50%  = local split (byte-poll from DismSplitter)
            //   50..100% = copy SWMs to USB (per-byte from FileCopier)
            for (int i = 0; i < skipped.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = skipped[i];
                var sourceWim = Path.Combine(mounted.MountRoot, rel);
                var finalDestDir = Path.Combine(usbRoot, Path.GetDirectoryName(rel) ?? string.Empty);
                var swmBase = Path.GetFileNameWithoutExtension(rel) + ".swm";

                var sourceBytes = new FileInfo(sourceWim).Length;
                var tempRoot = Path.Combine(
                    Path.GetTempPath(),
                    "IsoToUsb-split-" + Guid.NewGuid().ToString("N"));

                // Free-space check on the temp drive so we fail fast with a
                // clear message instead of running dism for 30s only to die.
                EnsureFreeSpace(tempRoot, sourceBytes + 256L * 1024 * 1024);

                progress?.Report(new PipelineProgress(
                    PipelineStage.SplitInstallWim,
                    0,
                    $"Splitting {rel} locally ({i + 1}/{skipped.Count})..."));

                Directory.CreateDirectory(tempRoot);
                try
                {
                    // Phase 1: split to local temp (0..50%).
                    var splitProgress = new Progress<DismProgress>(p =>
                    {
                        if (p.Percent >= 0)
                        {
                            progress?.Report(new PipelineProgress(
                                PipelineStage.SplitInstallWim,
                                p.Percent / 2,
                                p.Line));
                        }
                    });
                    await Splitter.SplitAsync(
                        sourceWim,
                        tempRoot,
                        outputBaseName: swmBase,
                        progress: splitProgress,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    // Phase 2: copy split chunks to the USB (50..100%).
                    progress?.Report(new PipelineProgress(
                        PipelineStage.SplitInstallWim,
                        50,
                        $"Copying split chunks to USB..."));
                    var swmCopier = new FileCopier();
                    var swmGate = new HeartbeatGate(LogByteGate);
                    var swmCopyProgress = new Progress<CopyProgress>(p =>
                    {
                        if (p.BytesTotal <= 0)
                        {
                            return;
                        }
                        var copyPct = (int)(p.BytesDone * 100 / p.BytesTotal);
                        var overallPct = 50 + copyPct / 2;
                        // 1-based "currently working on chunk X of N", with a
                        // running MB count so the user can see a 4 GB SWM
                        // making byte-level progress mid-copy (FileCopier
                        // emits ~5 reports/s during the stream).
                        var current = Math.Min(p.FilesDone + 1, p.FilesTotal);
                        var doneMb = p.BytesDone / (1024 * 1024);
                        var totalMb = p.BytesTotal / (1024 * 1024);
                        // Same gate as CopyFiles: one log line per chunk
                        // boundary plus one every 50 MiB so a 3.5 GiB SWM
                        // shows ~70 motion lines (one every ~5s on a USB
                        // 3.0 stick) instead of feeling stuck between
                        // entries. HeartbeatGate locks gate state across
                        // thread-pool callbacks (no sync context in the
                        // worker process).
                        var isHeartbeat = swmGate.ShouldHeartbeat(p.CurrentRelativePath, p.BytesDone);
                        progress?.Report(new PipelineProgress(
                            PipelineStage.SplitInstallWim,
                            overallPct,
                            $"copy {current}/{p.FilesTotal} {p.CurrentRelativePath} · {doneMb}/{totalMb} MB",
                            isHeartbeat));
                    });
                    Directory.CreateDirectory(finalDestDir);
                    await swmCopier
                        .CopyAsync(tempRoot, finalDestDir, swmCopyProgress, cancellationToken)
                        .ConfigureAwait(false);

                    // After every SWM chunk copy lands, cross-check that
                    // every chunk DISM produced in temp now exists on the
                    // USB at the same byte count. FileCopier's per-file
                    // length check already throws on a truncated copy, but
                    // it can't catch the case where DISM produces N chunks
                    // and only N-1 get enumerated (race, AV lock, etc.).
                    // This is the only chance we get to detect that BEFORE
                    // Windows Setup detonates with 0x8007000D mid-install.
                    VerifyAllSwmChunksLanded(tempRoot, finalDestDir, rel);
                }
                finally
                {
                    // AV / indexer / pending I/O can briefly hold handles on
                    // the freshly written SWM chunks. Retry with small backoff
                    // so we don't leak multi-GB temp dirs in the common case,
                    // and surface a Warning to the log if the final retry
                    // can't reclaim the space.
                    await TryDeleteTempDirAsync(tempRoot, progress).ConfigureAwait(false);
                }
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
    /// Decides whether a per-byte progress tick is a noisy heartbeat (only
    /// drives the bar) or a milestone the log should record. Logs the
    /// first tick for each new file PLUS one tick every <c>byteGate</c>
    /// bytes within the same file. Thread-safe because Progress&lt;T&gt;
    /// in the worker process (no SynchronizationContext) dispatches to
    /// arbitrary thread-pool threads and the gate's <c>_lastFile</c> /
    /// <c>_lastBytes</c> would otherwise race.
    /// </summary>
    internal sealed class HeartbeatGate
    {
        private readonly object _lock = new();
        private readonly long _byteGate;
        private string? _lastFile;
        private long _lastBytes;

        public HeartbeatGate(long byteGate) => _byteGate = byteGate;

        public bool ShouldHeartbeat(string currentFile, long bytesDone)
        {
            lock (_lock)
            {
                var fileChanged = _lastFile != currentFile;
                if (fileChanged)
                {
                    _lastFile = currentFile;
                    _lastBytes = 0;
                }
                var isHeartbeat = !fileChanged && (bytesDone - _lastBytes) < _byteGate;
                if (!isHeartbeat)
                {
                    _lastBytes = bytesDone;
                }
                return isHeartbeat;
            }
        }
    }

    /// <summary>
    /// Best-effort recursive delete with backoff. AV scanners and the
    /// Windows Search indexer can briefly hold handles on the freshly
    /// written split chunks, so a single Delete attempt often fails right
    /// after a 6 GB SWM split and silently leaks multi-GB of %TEMP%.
    /// </summary>
    /// <remarks>
    /// Runs in the cleanup finally block so it MUST NOT throw. If every
    /// retry fails we emit a Warning-class PipelineProgress so the user
    /// can see the path that leaked and remove it manually.
    /// </remarks>
    private static async Task TryDeleteTempDirAsync(string tempRoot, IProgress<PipelineProgress>? progress)
    {
        const int MaxAttempts = 5;
        Exception? last = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(tempRoot))
                {
                    return;
                }
                Directory.Delete(tempRoot, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == MaxAttempts)
                {
                    break;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt)).ConfigureAwait(false);
                }
                catch
                {
                    // Don't propagate cleanup-loop wait failures.
                }
            }
        }

        try
        {
            progress?.Report(new PipelineProgress(
                PipelineStage.SplitInstallWim,
                -1,
                $"Warning: could not delete temp split dir '{tempRoot}': {last?.Message}. " +
                "Remove it manually to reclaim disk space."));
        }
        catch
        {
            // Reporting must never escape cleanup.
        }
    }

    /// <summary>
    /// Ensures the drive that hosts <paramref name="anyPathOnDrive"/> has at
    /// least <paramref name="requiredBytes"/> free. Throws a clear error
    /// before the split phase so users get actionable feedback instead of a
    /// dism exit-code surprise mid-build.
    /// </summary>
    private static void EnsureFreeSpace(string anyPathOnDrive, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(anyPathOnDrive));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"Not enough free space on {drive.Name} to split locally first. " +
                    $"Need {requiredBytes / (1024.0 * 1024 * 1024):N1} GiB, " +
                    $"have {drive.AvailableFreeSpace / (1024.0 * 1024 * 1024):N1} GiB.");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // Treat probe failures as "unknown" — don't block the build on a
            // metadata error from an unusual filesystem.
        }
    }

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

    /// <summary>
    /// After the SWM split-and-copy pair, confirm that every chunk DISM
    /// produced in <paramref name="tempRoot"/> exists on the USB at
    /// <paramref name="finalDestDir"/> with the exact same byte count.
    /// FileCopier's per-file size check would already throw on a truncated
    /// chunk, but this catches the orthogonal "missing chunk entirely"
    /// failure mode (DISM produced install.swm + install2.swm + install3.swm
    /// but only the first two landed on the USB). Throws IOException with
    /// full diagnostic context so the pipeline aborts BEFORE marking the
    /// build green.
    /// </summary>
    internal static void VerifyAllSwmChunksLanded(string tempRoot, string finalDestDir, string sourceRel)
    {
        var tempChunks = new DirectoryInfo(tempRoot)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(tempRoot, f.FullName), f => f.Length, StringComparer.OrdinalIgnoreCase);

        long tempTotal = tempChunks.Values.Sum();
        long destTotal = 0;
        var missing = new List<string>();
        var sizeMismatch = new List<string>();

        foreach (var (rel, expectedLen) in tempChunks)
        {
            var destPath = Path.Combine(finalDestDir, rel);
            if (!File.Exists(destPath))
            {
                missing.Add($"{rel} (expected {expectedLen:N0} bytes)");
                continue;
            }
            var actualLen = new FileInfo(destPath).Length;
            destTotal += actualLen;
            if (actualLen != expectedLen)
            {
                sizeMismatch.Add($"{rel} (expected {expectedLen:N0}, got {actualLen:N0})");
            }
        }

        if (missing.Count == 0 && sizeMismatch.Count == 0)
        {
            return;
        }

        throw new IOException(
            $"After splitting '{sourceRel}' into {tempChunks.Count} chunk(s) " +
            $"({tempTotal:N0} bytes total) and copying to the USB, the destination " +
            $"is incomplete (USB total: {destTotal:N0} bytes). " +
            (missing.Count > 0 ? $"Missing chunks: {string.Join(", ", missing)}. " : string.Empty) +
            (sizeMismatch.Count > 0 ? $"Truncated chunks: {string.Join(", ", sizeMismatch)}. " : string.Empty) +
            "This would cause Windows Setup to fail with 0x8007000D mid-install. " +
            "Refusing to mark the build complete.");
    }
}
