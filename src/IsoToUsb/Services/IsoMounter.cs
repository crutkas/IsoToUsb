using System.ComponentModel;
using System.Runtime.InteropServices;
using IsoToUsb.Services.Internal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.Vhd;

namespace IsoToUsb.Services;

/// <summary>
/// Mounts an .iso file as a read-only virtual disk via <c>virtdisk.dll</c>
/// (the same mechanism PowerShell's <c>Mount-DiskImage</c> wraps). Dispose
/// to detach. Requires admin.
/// </summary>
public sealed class MountedIso : IDisposable
{
    private readonly SafeFileHandle _handle;
    private bool _detached;

    /// <summary>The drive letter Windows assigned the ISO, e.g. <c>"E:\\"</c>.</summary>
    public string MountRoot { get; }

    /// <summary>The original path of the ISO file on disk.</summary>
    public string IsoPath { get; }

    internal MountedIso(SafeFileHandle handle, string mountRoot, string isoPath)
    {
        _handle = handle;
        MountRoot = mountRoot;
        IsoPath = isoPath;
    }

    public void Dispose()
    {
        if (_detached)
        {
            return;
        }
        _detached = true;
        try
        {
            PInvoke.DetachVirtualDisk(AsHandle(_handle), DETACH_VIRTUAL_DISK_FLAG.DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
        }
        catch
        {
            // Disposal must not throw.
        }
        finally
        {
            _handle.Dispose();
        }
    }

    internal static HANDLE AsHandle(SafeFileHandle safe) => new(safe.DangerousGetHandle());
}

/// <summary>
/// Static helpers for mounting an ISO read-only and resolving the assigned
/// drive letter.
/// </summary>
public static class IsoMounter
{
    // virtdisk.h: device type for ISO images.
    private const uint VIRTUAL_STORAGE_TYPE_DEVICE_ISO = 1;

    // virtdisk.h: {EC984AEC-A0F9-47e9-901F-71415A66345B}
    private static readonly Guid VirtualStorageTypeVendorMicrosoft =
        new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    /// <summary>
    /// Mount the given ISO read-only. The returned <see cref="MountedIso"/>
    /// owns the virtdisk handle; dispose to detach.
    /// </summary>
    public static unsafe MountedIso Mount(string isoPath, TimeSpan? assignmentTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException("ISO file not found.", isoPath);
        }

        var fullPath = Path.GetFullPath(isoPath);
        var beforeLetters = SnapshotDriveLetters();

        var storageType = new VIRTUAL_STORAGE_TYPE
        {
            DeviceId = VIRTUAL_STORAGE_TYPE_DEVICE_ISO,
            VendorId = VirtualStorageTypeVendorMicrosoft,
        };

        var openError = PInvoke.OpenVirtualDisk(
            in storageType,
            fullPath,
            VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_ATTACH_RO | VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_GET_INFO,
            OPEN_VIRTUAL_DISK_FLAG.OPEN_VIRTUAL_DISK_FLAG_NONE,
            null,
            out var handle);
        if (openError != 0)
        {
            throw new Win32Exception((int)openError, $"OpenVirtualDisk failed for '{fullPath}'.");
        }

        var attachError = PInvoke.AttachVirtualDisk(
            MountedIso.AsHandle(handle),
            default(PSECURITY_DESCRIPTOR),
            ATTACH_VIRTUAL_DISK_FLAG.ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY,
            0,
            null,
            null);
        if (attachError != 0)
        {
            handle.Dispose();
            throw new Win32Exception((int)attachError, "AttachVirtualDisk failed.");
        }

        // Drive letter assignment is asynchronous relative to AttachVirtualDisk
        // returning, and concurrent USB mounts (e.g. inserting a stick at the
        // same time) can race the diff-poll fallback. Preferred path: ask
        // virtdisk for the physical drive number we just attached, then look
        // up its drive letter via WMI. Diff-poll is kept as a last resort.
        var deadline = DateTime.UtcNow + (assignmentTimeout ?? TimeSpan.FromSeconds(15));
        string? mountRoot = TryResolveMountByPhysicalPath(handle, deadline)
            ?? TryResolveMountByDiff(beforeLetters, deadline);

        if (mountRoot is null)
        {
            try { PInvoke.DetachVirtualDisk(MountedIso.AsHandle(handle), DETACH_VIRTUAL_DISK_FLAG.DETACH_VIRTUAL_DISK_FLAG_NONE, 0); }
            catch { /* best-effort */ }
            handle.Dispose();
            throw new InvalidOperationException(
                "ISO attached but no new drive letter appeared within the timeout. " +
                "Another mount may have occurred concurrently — please retry.");
        }

        return new MountedIso(handle, mountRoot, fullPath);
    }

    /// <summary>
    /// Asks virtdisk for the physical drive path of the attached ISO
    /// (<c>\\.\PhysicalDriveN</c>), then polls the volume table for a drive
    /// letter assigned to that disk. This binds the result to the disk we
    /// just attached, not to whichever drive happened to appear during a
    /// race window. Returns <c>null</c> if virtdisk can't yet produce a
    /// physical path or no partition has a letter assigned within the
    /// deadline.
    /// </summary>
    private static unsafe string? TryResolveMountByPhysicalPath(SafeFileHandle handle, DateTime deadline)
    {
        uint diskNumber;
        try
        {
            if (!TryGetPhysicalDriveNumber(handle, out diskNumber))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var letter = Win32Storage.WaitForDriveLetter(diskNumber, deadline, pollMs: 150);
        return letter is null ? null : $"{letter.Value}:\\";
    }

    /// <summary>
    /// Fallback used only when the WMI lookup didn't resolve. Polls
    /// <see cref="PInvoke.GetLogicalDrives"/> looking for any new letter
    /// that appeared since before <c>AttachVirtualDisk</c>; ambiguous if
    /// more than one new letter appears (a separate USB plugin during the
    /// window) — returns <c>null</c> rather than guess.
    /// </summary>
    private static string? TryResolveMountByDiff(HashSet<string> beforeLetters, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            var added = SnapshotDriveLetters().Except(beforeLetters).ToList();
            if (added.Count == 1)
            {
                return added[0];
            }
            if (added.Count > 1)
            {
                return null;
            }
            Thread.Sleep(150);
        }
        return null;
    }

    /// <summary>
    /// Parses the <c>\\.\PhysicalDriveN</c> path returned by
    /// <c>GetVirtualDiskPhysicalPath</c>. Returns false when virtdisk says
    /// the physical path isn't ready yet (typical for the first few hundred
    /// milliseconds after AttachVirtualDisk returns).
    /// </summary>
    private static bool TryGetPhysicalDriveNumber(SafeFileHandle handle, out uint diskNumber)
    {
        diskNumber = 0;
        const int CharCapacity = 260;
        uint sizeBytes = CharCapacity * sizeof(char);
        Span<char> buffer = stackalloc char[CharCapacity];
        var err = PInvoke.GetVirtualDiskPhysicalPath(handle, ref sizeBytes, buffer);
        if (err != 0)
        {
            return false;
        }
        var nul = buffer.IndexOf('\0');
        var path = new string(buffer[..(nul >= 0 ? nul : buffer.Length)]);
        return TryParsePhysicalDriveNumber(path, out diskNumber);
    }

    internal static bool TryParsePhysicalDriveNumber(string path, out uint diskNumber)
    {
        diskNumber = 0;
        if (string.IsNullOrEmpty(path)) return false;
        const string prefix = @"\\.\PhysicalDrive";
        var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var numberPart = path[(idx + prefix.Length)..].TrimEnd('\0');
        return uint.TryParse(numberPart, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out diskNumber);
    }

    private static HashSet<string> SnapshotDriveLetters()
    {
        var bitmap = PInvoke.GetLogicalDrives();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 26; i++)
        {
            if ((bitmap & (1u << i)) != 0)
            {
                set.Add($"{(char)('A' + i)}:\\");
            }
        }
        return set;
    }
}
