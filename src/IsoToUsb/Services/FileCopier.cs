namespace IsoToUsb.Services;

using System.Diagnostics;

/// <summary>Snapshot reported to a copy progress observer.</summary>
public sealed record CopyProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string CurrentRelativePath);

/// <summary>
/// How <see cref="FileCopier"/> (and its callers) should handle a file
/// whose size exceeds the FAT32 4 GiB per-file limit.
/// </summary>
public enum Fat32FileAction
{
    /// <summary>File fits on FAT32 — copy as-is.</summary>
    Copy,

    /// <summary>File is too large but is a WIM/ESD that DISM can split.</summary>
    SplitWithDism,

    /// <summary>File is too large and can't be split — abort before
    /// wiping the target.</summary>
    Reject,
}

/// <summary>
/// Recursively copies the contents of a source directory tree to a destination,
/// skipping any files matched by a predicate (used to defer files that exceed
/// the FAT32 4 GiB per-file limit so the pipeline can DISM-split them later).
/// </summary>
public sealed class FileCopier
{
    private const int BufferSize = 1024 * 1024; // 1 MiB

    /// <summary>
    /// FAT32 maximum file size with a small safety margin.
    /// </summary>
    public const long Fat32MaxFileBytes = (4L * 1024 * 1024 * 1024) - 1;

    public Func<FileInfo, string, bool> SkipPredicate { get; init; } = static (_, _) => false;

    /// <summary>
    /// Recursively copy <paramref name="sourceRoot"/> into <paramref name="destinationRoot"/>.
    /// <paramref name="progress"/> fires <em>during</em> each file's byte
    /// stream (throttled to ~5 Hz) AND once at the end of each file. The
    /// intra-file reports matter for the WIM-split phase, where the per-SWM
    /// files are ~4 GB each and would otherwise leave the UI stuck at 50%
    /// for 30-60 seconds at a time.
    /// Returns the list of skipped files (relative paths).
    /// </summary>
    public async Task<IReadOnlyList<string>> CopyAsync(
        string sourceRoot,
        string destinationRoot,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(sourceRoot);
        }
        Directory.CreateDirectory(destinationRoot);

        var allFiles = new DirectoryInfo(sourceRoot)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .ToList();
        var skipped = new List<string>();
        var toCopy = new List<FileInfo>(allFiles.Count);
        foreach (var f in allFiles)
        {
            var rel = Path.GetRelativePath(sourceRoot, f.FullName);
            if (SkipPredicate(f, rel))
            {
                skipped.Add(rel);
            }
            else
            {
                toCopy.Add(f);
            }
        }
        var totalBytes = toCopy.Sum(f => f.Length);
        long bytesDone = 0;
        int filesDone = 0;

        // One shared buffer across the whole copy to keep GC pressure flat
        // on large WIM/SWM streams.
        var buffer = new byte[BufferSize];
        var reportEveryTicks = Stopwatch.Frequency / 5; // ~200 ms
        var lastReportTs = Stopwatch.GetTimestamp() - reportEveryTicks; // fire on first chunk

        foreach (var src in toCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceRoot, src.FullName);
            var dest = Path.Combine(destinationRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var expectedBytes = src.Length;
            await using (var inStream = new FileStream(src.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var outStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await inStream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesDone += read;

                    var now = Stopwatch.GetTimestamp();
                    if (now - lastReportTs >= reportEveryTicks)
                    {
                        lastReportTs = now;
                        progress?.Report(new CopyProgress(bytesDone, totalBytes, filesDone, toCopy.Count, rel));
                    }
                }

                // Force the OS to flush BOTH the .NET-side buffer AND the
                // underlying file-system / device caches to media before we
                // close the handle and trust the on-disk size. Without
                // flushToDisk:true, FAT32 on a removable USB stick can hold
                // multi-GB of pending writes in its write cache and report a
                // file size that doesn't reflect what's actually committed
                // to flash. This was the proximate cause of a silent
                // install2.swm truncation that bricked a Windows Setup at
                // 0x8007000D (ERROR_INVALID_DATA) on the user's first real
                // build (2026-06-07): 3.55 GiB chunk was reported as 1.17
                // GiB on the USB and the pipeline marked the build green.
                outStream.Flush(flushToDisk: true);
            }

            // Belt-and-suspenders: after the streams close + flush, re-stat
            // the destination and refuse to advance if the byte count
            // doesn't match the source. A short file here means the OS lost
            // data in flight (USB controller, FAT32 cache, AV, etc.). We'd
            // rather hard-fail the build than silently ship a corrupt USB
            // that fails Windows Setup mid-image-apply.
            var actualBytes = new FileInfo(dest).Length;
            if (actualBytes != expectedBytes)
            {
                throw new IOException(
                    $"Copy of '{rel}' produced {actualBytes:N0} bytes on the destination " +
                    $"but the source is {expectedBytes:N0} bytes. The destination drive may " +
                    $"have run out of space, been ejected mid-write, or returned a write " +
                    $"error that the OS silently truncated. Refusing to continue with a " +
                    $"corrupted file in place.");
            }

            filesDone++;
            // Always emit a per-file completion event so the count + final
            // byte total are exact at file boundaries, regardless of throttle.
            lastReportTs = Stopwatch.GetTimestamp();
            progress?.Report(new CopyProgress(bytesDone, totalBytes, filesDone, toCopy.Count, rel));
        }

        return skipped;
    }

    /// <summary>
    /// Classifies a file for FAT32-safe handling. Files at or below
    /// <paramref name="maxBytes"/> are <see cref="Fat32FileAction.Copy"/>.
    /// Larger files that are WIM/ESD (split-capable by DISM) are
    /// <see cref="Fat32FileAction.SplitWithDism"/>; everything else
    /// (including a pre-split <c>.swm</c> chunk that somehow ended up larger
    /// than FAT32's per-file limit — DISM cannot re-split a <c>.swm</c>) is
    /// <see cref="Fat32FileAction.Reject"/> so the pipeline aborts before
    /// wiping the target.
    /// </summary>
    public static Fat32FileAction ClassifyForFat32(FileInfo file, string relativePath, long maxBytes = Fat32MaxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (file.Length <= maxBytes)
        {
            return Fat32FileAction.Copy;
        }
        var ext = Path.GetExtension(relativePath);
        if (ext.Equals(".wim", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".esd", StringComparison.OrdinalIgnoreCase))
        {
            return Fat32FileAction.SplitWithDism;
        }
        return Fat32FileAction.Reject;
    }

    /// <summary>
    /// Returns <c>true</c> when <see cref="ClassifyForFat32"/> says the
    /// file should be split by DISM. Convenience predicate for
    /// <see cref="SkipPredicate"/>.
    /// </summary>
    public static bool ShouldSplitForFat32(FileInfo file, string relativePath)
        => ClassifyForFat32(file, relativePath) == Fat32FileAction.SplitWithDism;
}
