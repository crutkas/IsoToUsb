namespace IsoToUsb.Services;

/// <summary>Snapshot reported to a copy progress observer.</summary>
public sealed record CopyProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal, string CurrentRelativePath);

/// <summary>
/// Recursively copies the contents of a source directory tree to a destination,
/// skipping any files matched by a predicate (used to skip
/// <c>sources\install.wim</c> when it exceeds the FAT32 4 GiB limit).
/// </summary>
public sealed class FileCopier
{
    private const int BufferSize = 1024 * 1024; // 1 MiB

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
    /// Returns true when the file is <c>sources\install.wim</c> and exceeds
    /// <paramref name="maxBytes"/> (FAT32's 4 GiB limit, with a small margin).
    /// </summary>
    public static bool ShouldSkipInstallWimForFat32(FileInfo file, string relativePath, long maxBytes = (4L * 1024 * 1024 * 1024) - 1)
    {
        if (file.Length <= maxBytes)
        {
            return false;
        }
        var normalized = relativePath.Replace('/', '\\');
        return string.Equals(normalized, Path.Combine("sources", "install.wim"), StringComparison.OrdinalIgnoreCase);
    }
}
