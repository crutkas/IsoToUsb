using System.Security.Cryptography;

namespace IsoToUsb.Services;

/// <summary>
/// Per-file verification result.
/// </summary>
public sealed record VerificationResult(string RelativePath, bool Match, string? Reason);

/// <summary>
/// Spot-checks a USB copy of a Windows ISO by SHA-256 hashing a small
/// random sample of files on both sides.
/// </summary>
public sealed class SampleVerifier
{
    public int SampleSize { get; init; } = 20;

    /// <summary>
    /// Pick up to <see cref="SampleSize"/> files (excluding
    /// <paramref name="skipRelativePaths"/>), hash both copies, return results.
    /// </summary>
    /// <param name="progress">
    /// Optional per-file callback fired after each file's source+destination
    /// hashes have been compared. Lets the pipeline log "verified X" lines
    /// in real time instead of reporting only a single "all match" summary
    /// after every file has been hashed.
    /// </param>
    public async Task<IReadOnlyList<VerificationResult>> VerifyAsync(
        string sourceRoot,
        string destinationRoot,
        IEnumerable<string>? skipRelativePaths = null,
        IProgress<VerificationResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var skip = new HashSet<string>(
            (skipRelativePaths ?? Array.Empty<string>()).Select(NormalizeRelative),
            StringComparer.OrdinalIgnoreCase);

        var candidates = new DirectoryInfo(sourceRoot)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(sourceRoot, f.FullName))
            .Where(rel => !skip.Contains(NormalizeRelative(rel)))
            .ToList();

        if (candidates.Count == 0)
        {
            return Array.Empty<VerificationResult>();
        }

        var rng = Random.Shared;
        var picks = candidates
            .OrderBy(_ => rng.Next())
            .Take(Math.Min(SampleSize, candidates.Count))
            .ToList();

        var results = new List<VerificationResult>(picks.Count);
        foreach (var rel in picks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var srcPath = Path.Combine(sourceRoot, rel);
            var dstPath = Path.Combine(destinationRoot, rel);

            VerificationResult result;
            if (!File.Exists(dstPath))
            {
                result = new VerificationResult(rel, false, "Missing on destination.");
            }
            else
            {
                var srcHash = await HashAsync(srcPath, cancellationToken).ConfigureAwait(false);
                var dstHash = await HashAsync(dstPath, cancellationToken).ConfigureAwait(false);
                var match = srcHash.AsSpan().SequenceEqual(dstHash);
                result = new VerificationResult(rel, match, match ? null : "SHA-256 mismatch.");
            }
            results.Add(result);
            progress?.Report(result);
        }
        return results;
    }

    private static string NormalizeRelative(string rel) => rel.Replace('/', '\\');

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
    }
}
