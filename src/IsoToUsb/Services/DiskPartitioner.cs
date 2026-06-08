using System.Diagnostics;
using System.Text;
using IsoToUsb.Services.Internal;

namespace IsoToUsb.Services;

/// <summary>
/// Wipes a target USB disk and creates the single FAT32 GPT partition
/// layout used for UEFI Windows install media. The destructive
/// clean/convert/partition/format pipeline is delegated to in-box
/// <c>diskpart.exe</c> because the WMI <c>MSFT_Disk.Initialize</c> path
/// races with Windows automount (Clear leaves the disk RAW, automount
/// re-MBRs it before we can Initialize as GPT, producing ReturnValue
/// 41001 — "Disk is already initialized"). Diskpart issues all steps in a
/// single script transaction so automount cannot interleave.
/// </summary>
public sealed class DiskPartitioner
{
    public const string DefaultVolumeLabel = "WIN_USB";

    /// <summary>
    /// Maximum size of the FAT32 partition we create, in MiB. The Windows
    /// built-in FAT32 formatter refuses anything beyond 32 GiB. Capping at
    /// 32000 MiB (~31.25 GiB) leaves a small safety margin on disks larger
    /// than 32 GiB; the rest of the disk is left unallocated.
    /// </summary>
    private const uint Fat32CapMiB = 32_000;

    /// <summary>
    /// Disk sizes at or below this threshold use the entire disk for the
    /// FAT32 partition. Above it, the partition is capped at
    /// <see cref="Fat32CapMiB"/>.
    /// </summary>
    private const ulong Fat32CapTriggerBytes = (ulong)Fat32CapMiB * 1024 * 1024;

    /// <summary>
    /// Repartition the target disk:
    /// 1. <c>clean</c> (wipes MBR/GPT headers and all partitions).
    /// 2. <c>convert gpt</c>.
    /// 3. <c>create partition primary</c> (capped at 32000 MiB on large disks).
    /// 4. <c>format fs=fat32 quick label=&lt;label&gt;</c>.
    /// 5. <c>assign</c> (next available drive letter).
    /// </summary>
    /// <returns>The assigned drive letter root, e.g. <c>"F:\\"</c>.</returns>
    public string Repartition(DiskInfo disk, string volumeLabel = DefaultVolumeLabel)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeLabel);

        if (!IsSafeLabel(volumeLabel))
        {
            throw new ArgumentException(
                "Volume label may only contain letters, digits, spaces, '-' or '_' (max 11 chars for FAT32).",
                nameof(volumeLabel));
        }

        RunDiskpartScript(disk.Number, disk.SizeBytes, volumeLabel);

        var letter = WaitForVolumeLetter(disk.Number, TimeSpan.FromSeconds(30));
        return $"{letter}:\\";
    }

    private static void RunDiskpartScript(uint diskNumber, ulong diskSizeBytes, string label)
    {
        // Notes on the script:
        //  - `attributes disk clear readonly` only matters for USBs with the
        //    read-only attribute set; `noerr` keeps the script going if not.
        //  - We deliberately do NOT `online disk noerr` because if the disk
        //    is already online (the normal case) diskpart prints a
        //    "Virtual Disk Service error" line that confuses output parsers
        //    and isn't suppressed by `noerr` in older diskpart builds.
        //    The disk is already online: we just enumerated it via WMI.
        //  - `select partition 1` after `create partition primary` is
        //    required so `format` finds a current volume. Without it,
        //    `format` can fail with "There is no volume selected."
        //  - `format` is run BEFORE `assign` so Windows doesn't see an
        //    unformatted volume appear and pop the "format this drive?"
        //    AutoPlay dialog.
        //  - On disks > 32 GiB, we cap the FAT32 partition at 32000 MiB
        //    because the in-box Windows formatter refuses larger FAT32
        //    volumes. The rest of the disk is left unallocated.
        var createPartition = diskSizeBytes > Fat32CapTriggerBytes
            ? $"create partition primary size={Fat32CapMiB.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : "create partition primary";

        var script = new StringBuilder()
            .AppendLine($"select disk {diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .AppendLine("attributes disk clear readonly noerr")
            .AppendLine("clean")
            // After `clean`, the disk is RAW (no partition table). `convert gpt`
            // requires the disk to be in MBR state ("A basic MBR disk must be
            // empty before you can convert it to a GPT disk"), so we explicitly
            // initialize as MBR first. `noerr` makes this a no-op if the disk
            // is already MBR.
            .AppendLine("convert mbr noerr")
            .AppendLine("convert gpt")
            .AppendLine(createPartition)
            // `rescan` forces Windows' Volume Manager to enumerate volumes for
            // the partition we just created. Without it, fast USB 3.0 sticks
            // (and ARM64 ISOs that take an extra moment to settle) fail
            // `format` with "There is no volume selected" because diskpart's
            // partition focus has no associated volume object yet.
            // `rescan` may lose focus, so re-select disk + partition after.
            .AppendLine("rescan")
            .AppendLine($"select disk {diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .AppendLine("select partition 1")
            .AppendLine($"format fs=fat32 quick label=\"{label}\"")
            .AppendLine("assign")
            .AppendLine("exit")
            .ToString();

        var scriptPath = Path.Combine(Path.GetTempPath(),
            $"isotousb-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(scriptPath, script);

        string combined;
        int exitCode;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add(scriptPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start diskpart.exe.");

            // Drain stdout/stderr asynchronously so a long diskpart run
            // can't deadlock if its output exceeds the pipe buffer
            // (synchronous ReadToEnd before WaitForExit also blocks the
            // 2-minute timeout from ever firing while output is pending).
            var sb = new StringBuilder();
            var sync = new object();
            void Append(string? line)
            {
                if (line is null) return;
                lock (sync) { sb.AppendLine(line); }
            }
            proc.OutputDataReceived += (_, e) => Append(e.Data);
            proc.ErrorDataReceived += (_, e) => Append(e.Data);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("diskpart timed out after 2 minutes.");
            }
            // Ensure all async output has been drained before we read sb.
            proc.WaitForExit();

            lock (sync) { combined = sb.ToString().Trim(); }
            exitCode = proc.ExitCode;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }

        // Diskpart's success markers are localized, so we can't parse them
        // reliably on non-English Windows installs. Instead, trust the
        // post-condition: the caller verifies a drive letter appears on the
        // target disk's first partition. Surface the exit code + captured
        // output only when that post-condition fails, so the user has
        // something to diagnose.
        if (exitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"diskpart exited with code {exitCode}.\nOutput:\n{combined}");
    }

    private static char WaitForVolumeLetter(uint diskNumber, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var letter = Win32Storage.WaitForDriveLetter(diskNumber, deadline);
        if (letter is not null)
        {
            return letter.Value;
        }
        throw new InvalidOperationException(
            $"Timed out waiting for a drive letter on disk {diskNumber}. " +
            "diskpart reported success but no mounted volume appeared.");
    }

    private static bool IsSafeLabel(string label)
    {
        if (label.Length is 0 or > 11)
        {
            return false;
        }
        foreach (var ch in label)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_'))
            {
                return false;
            }
        }
        return true;
    }
}
