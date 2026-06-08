using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace IsoToUsb.Services.Internal;

/// <summary>
/// Replaces our former <c>MSFT_Disk</c> / <c>MSFT_PhysicalDisk</c> /
/// <c>MSFT_Partition</c> WMI queries with direct DeviceIoControl calls. WMI's
/// <c>System.Management</c> stack uses reflection on internal COM ctors and
/// is not AOT-safe; raw IOCTLs return plain C structs that CsWin32 surfaces
/// as <c>unsafe struct</c> with no marshalling layer.
/// </summary>
/// <remarks>
/// Enumeration strategy is intentionally dumb: probe <c>\\.\PhysicalDriveN</c>
/// for <c>N</c> in <c>[0, MaxDiskProbe)</c>. Windows assigns disk numbers
/// densely from 0; gaps appear only briefly during hot-plug churn. Skipping
/// SetupDi avoids the device-interface-detail variable-length-buffer pattern
/// for a small probe budget.
/// </remarks>
public static unsafe class Win32Storage
{
    private const int MaxDiskProbe = 32;

    /// <summary>
    /// Mirrors the data we used to read from <c>MSFT_Disk</c> +
    /// <c>MSFT_PhysicalDisk</c> + <c>MSFT_Partition</c>, in the exact shape
    /// our existing pipeline + tests already consume.
    /// </summary>
    public static IReadOnlyList<DiskInfo> EnumerateAllDisks()
    {
        var systemAndBootDisks = TryResolveSystemAndBootDisks();
        var letters = VolumeLookup.GetDriveLettersByDiskNumber();

        var results = new List<DiskInfo>(capacity: 8);
        for (uint n = 0; n < MaxDiskProbe; n++)
        {
            if (!TryOpenDisk(n, out var handle))
            {
                continue;
            }

            try
            {
                if (!TryReadDeviceProps(handle, out var bus, out var removable, out var friendly, out var serial))
                {
                    continue;
                }
                if (!TryReadSize(handle, out var size))
                {
                    continue;
                }
                var isHdd = TryReadSeekPenalty(handle, out var seekPenalty) && seekPenalty;

                var isSystem = systemAndBootDisks.SystemDisk == n;
                var isBoot = systemAndBootDisks.BootDisk == n;
                letters.TryGetValue(n, out var letterList);

                results.Add(new DiskInfo(
                    Number: n,
                    FriendlyName: string.IsNullOrWhiteSpace(friendly) ? "(unknown)" : friendly,
                    SerialNumber: serial ?? string.Empty,
                    SizeBytes: size,
                    BusType: bus,
                    IsSystem: isSystem,
                    IsBoot: isBoot,
                    IsReadOnly: false,
                    MediaType: isHdd ? MediaTypes.Hdd : MediaTypes.Unspecified,
                    DriveLetters: letterList ?? string.Empty));
            }
            finally
            {
                handle.Dispose();
            }
        }
        return results;
    }

    /// <summary>
    /// Polls the Storage Management view of disk <paramref name="diskNumber"/>
    /// until a drive-letter assignment shows up or <paramref name="deadline"/>
    /// elapses. Replaces the WMI poll in DiskPartitioner / IsoMounter.
    /// </summary>
    /// <returns>The assigned drive letter, or <c>null</c> on timeout.</returns>
    public static char? WaitForDriveLetter(uint diskNumber, DateTime deadline, int pollMs = 250)
    {
        while (DateTime.UtcNow < deadline)
        {
            var letter = VolumeLookup.FindDriveLetterForDisk(diskNumber);
            if (letter is not null)
            {
                return letter;
            }
            Thread.Sleep(pollMs);
        }
        return null;
    }

    private static bool TryOpenDisk(uint diskNumber, out SafeFileHandle handle)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        // Open with zero access — we only need to issue IOCTLs that report
        // device metadata, not read/write the disk. Sharing R|W is required
        // so we never block another process that legitimately owns the disk.
        handle = PInvoke.CreateFile(
            path,
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            hTemplateFile: null);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }
        return true;
    }

    private static bool TryReadDeviceProps(
        SafeFileHandle handle,
        out ushort busType,
        out bool removable,
        out string friendlyName,
        out string serialNumber)
    {
        busType = 0;
        removable = false;
        friendlyName = string.Empty;
        serialNumber = string.Empty;

        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageDeviceProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
        };

        // STORAGE_DEVICE_DESCRIPTOR has a header followed by trailing
        // null-terminated ANSI strings (vendor, product, serial) referenced
        // by byte offsets. 1 KiB is plenty.
        const int bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        if (!IoControl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(STORAGE_PROPERTY_QUERY), buffer, bufferSize, out _))
        {
            return false;
        }

        var descriptor = (STORAGE_DEVICE_DESCRIPTOR*)buffer;
        busType = (ushort)descriptor->BusType;
        removable = descriptor->RemovableMedia;
        var vendor = ReadAnsiAt(buffer, bufferSize, descriptor->VendorIdOffset);
        var product = ReadAnsiAt(buffer, bufferSize, descriptor->ProductIdOffset);
        serialNumber = ReadAnsiAt(buffer, bufferSize, descriptor->SerialNumberOffset);

        friendlyName = (vendor + " " + product).Trim();
        return true;
    }

    private static bool TryReadSize(SafeFileHandle handle, out ulong size)
    {
        size = 0;
        DISK_GEOMETRY_EX geom;
        if (!IoControl(handle, PInvoke.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, &geom, sizeof(DISK_GEOMETRY_EX), out _))
        {
            return false;
        }
        size = (ulong)geom.DiskSize;
        return true;
    }

    private static bool TryReadSeekPenalty(SafeFileHandle handle, out bool incursPenalty)
    {
        incursPenalty = false;
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
        };

        DEVICE_SEEK_PENALTY_DESCRIPTOR desc;
        if (!IoControl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, &query, sizeof(STORAGE_PROPERTY_QUERY), &desc, sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR), out _))
        {
            return false;
        }
        incursPenalty = desc.IncursSeekPenalty;
        return true;
    }

    /// <summary>
    /// Identifies the system disk and the firmware boot disk by opening the
    /// volume that hosts <c>%windir%</c> and asking which disk it lives on.
    /// On all consumer hardware they coincide; we report the same number for
    /// both, matching the prior <c>MSFT_Disk.IsSystem</c> / <c>IsBoot</c>
    /// behavior for the disks we filter out.
    /// </summary>
    private static (uint? SystemDisk, uint? BootDisk) TryResolveSystemAndBootDisks()
    {
        try
        {
            // GetWindowsDirectory normally returns "C:\Windows" but is locale-
            // and OS-install dependent — never hard-code.
            var sb = new char[260];
            uint len;
            fixed (char* p = sb)
            {
                len = PInvoke.GetWindowsDirectory(new PWSTR(p), (uint)sb.Length);
            }
            if (len == 0 || len > sb.Length)
            {
                return (null, null);
            }
            var windir = new string(sb, 0, (int)len);
            if (string.IsNullOrEmpty(windir) || windir.Length < 2 || windir[1] != ':')
            {
                return (null, null);
            }
            var systemLetter = char.ToUpperInvariant(windir[0]);
            var disk = VolumeLookup.GetDiskNumberForDriveLetter(systemLetter);
            return (disk, disk);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool IoControl(
        SafeFileHandle handle,
        uint controlCode,
        void* input,
        int inputSize,
        void* output,
        int outputSize,
        out uint bytesReturned)
    {
        uint returned = 0;
        var ok = PInvoke.DeviceIoControl(
            new HANDLE(handle.DangerousGetHandle()),
            controlCode,
            input,
            (uint)inputSize,
            output,
            (uint)outputSize,
            &returned,
            null);
        bytesReturned = returned;
        return ok;
    }

    private static string ReadAnsiAt(byte* buffer, int bufferLen, uint offset)
    {
        if (offset == 0 || offset >= bufferLen)
        {
            return string.Empty;
        }
        var start = (int)offset;
        var end = start;
        while (end < bufferLen && buffer[end] != 0)
        {
            end++;
        }
        if (end == start)
        {
            return string.Empty;
        }
        return Encoding.ASCII.GetString(buffer + start, end - start).Trim();
    }
}
