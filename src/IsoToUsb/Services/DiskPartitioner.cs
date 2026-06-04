using System.Diagnostics;
using System.Management;
using System.Text;

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
    private const string StorageScope = @"\\.\ROOT\Microsoft\Windows\Storage";
    public const string DefaultVolumeLabel = "WIN_USB";

    /// <summary>
    /// Repartition the target disk:
    /// 1. <c>clean</c> (wipes MBR/GPT headers and all partitions).
    /// 2. <c>convert gpt</c>.
    /// 3. <c>create partition primary</c> (max size).
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

        RunDiskpartScript(disk.Number, volumeLabel);

        var scope = new ManagementScope(StorageScope);
        scope.Connect();

        var letter = WaitForVolumeLetter(scope, disk.Number, TimeSpan.FromSeconds(30));
        return $"{letter}:\\";
    }

    private static void RunDiskpartScript(uint diskNumber, string label)
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
            .AppendLine("create partition primary")
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

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("diskpart timed out after 2 minutes.");
            }

            combined = (stdout + "\n" + stderr).Trim();
            exitCode = proc.ExitCode;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }

        // Diskpart's exit code is unreliable (can be non-zero even on success
        // when 'noerr' modifiers were used). Trust the success markers
        // diskpart prints for the critical steps instead.
        bool cleaned = combined.Contains("DiskPart succeeded in cleaning the disk", StringComparison.OrdinalIgnoreCase);
        bool converted = combined.Contains("DiskPart successfully converted the selected disk to GPT format", StringComparison.OrdinalIgnoreCase);
        bool partitionMade = combined.Contains("DiskPart succeeded in creating the specified partition", StringComparison.OrdinalIgnoreCase);
        bool formatted = combined.Contains("DiskPart successfully formatted the volume", StringComparison.OrdinalIgnoreCase);
        bool assigned = combined.Contains("DiskPart successfully assigned the drive letter or mount point", StringComparison.OrdinalIgnoreCase);

        if (cleaned && converted && partitionMade && formatted && assigned)
        {
            return;
        }

        var missing = new List<string>();
        if (!cleaned) missing.Add("clean");
        if (!converted) missing.Add("convert gpt");
        if (!partitionMade) missing.Add("create partition primary");
        if (!formatted) missing.Add("format fs=fat32");
        if (!assigned) missing.Add("assign");

        throw new InvalidOperationException(
            $"diskpart did not complete the required steps (missing: {string.Join(", ", missing)}, exit {exitCode}).\n" +
            $"Output:\n{combined}");
    }

    private static char WaitForVolumeLetter(ManagementScope scope, uint diskNumber, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var query = new ObjectQuery(
            $"SELECT DriveLetter FROM MSFT_Partition WHERE DiskNumber = {diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        while (DateTime.UtcNow < deadline)
        {
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                foreach (var item in searcher.Get())
                {
                    var dl = item["DriveLetter"];
                    var ch = dl switch
                    {
                        char c => c,
                        ushort u => (char)u,
                        byte b => (char)b,
                        _ => '\0',
                    };
                    if (ch >= 'A' && ch <= 'Z')
                    {
                        return ch;
                    }
                }
            }
            Thread.Sleep(500);
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
