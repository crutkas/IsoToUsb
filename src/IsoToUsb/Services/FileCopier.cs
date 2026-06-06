namespace IsoToUsb.Services;

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
    /// <paramref name="progress"/> fires after each file completes.
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

        foreach (var src in toCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceRoot, src.FullName);
            var dest = Path.Combine(destinationRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            await using (var inStream = new FileStream(src.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var outStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await inStream.CopyToAsync(outStream, BufferSize, cancellationToken).ConfigureAwait(false);
            }

            bytesDone += src.Length;
            filesDone++;
            progress?.Report(new CopyProgress(bytesDone, totalBytes, filesDone, toCopy.Count, rel));
        }

        return skipped;
    }

    /// <summary>
    /// Classifies a file for FAT32-safe handling. Files at or below
    /// <paramref name="maxBytes"/> are <see cref="Fat32FileAction.Copy"/>.
    /// Larger files that are WIM/ESD/SWM (split-capable by DISM) are
    /// <see cref="Fat32FileAction.SplitWithDism"/>; everything else is
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
            || ext.Equals(".esd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".swm", StringComparison.OrdinalIgnoreCase))
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
