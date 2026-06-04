using System.Management;

namespace IsoToUsb.Services;

/// <summary>
/// Reads physical disks from the Storage Management API and filters down to
/// the set safe to target: USB bus, not system, not boot, not read-only.
/// </summary>
public static class UsbDriveEnumerator
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary>
    /// Returns every <c>MSFT_Disk</c> instance on the machine. The caller is
    /// expected to pipe the result through <see cref="FilterTargetable"/>.
    /// </summary>
    public static IReadOnlyList<DiskInfo> EnumerateAllDisks()
    {
        var scope = new ManagementScope(StorageNamespace);
        var query = new ObjectQuery(
            "SELECT Number, FriendlyName, SerialNumber, Size, BusType, IsSystem, IsBoot, IsReadOnly FROM MSFT_Disk");
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var collection = searcher.Get();

        var results = new List<DiskInfo>(collection.Count);
        foreach (ManagementObject disk in collection)
        {
            using (disk)
            {
                results.Add(MapToDiskInfo(disk));
            }
        }
        return results;
    }

    /// <summary>
    /// USB-bus disks that aren't the system disk, boot disk, or write-protected.
    /// This is the only set we will ever offer the user as a target.
    /// </summary>
    public static IEnumerable<DiskInfo> FilterTargetable(IEnumerable<DiskInfo> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return disks.Where(IsTargetable);
    }

    /// <summary>
    /// True if the disk is safe to wipe and repartition as a Windows install USB.
    /// </summary>
    public static bool IsTargetable(DiskInfo disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return disk.IsUsbBus
            && !disk.IsSystem
            && !disk.IsBoot
            && !disk.IsReadOnly
            && disk.SizeBytes > 0;
    }

    internal static DiskInfo MapToDiskInfo(ManagementBaseObject mbo)
    {
        return new DiskInfo(
            Number: ConvertTo<uint>(mbo["Number"]),
            FriendlyName: mbo["FriendlyName"]?.ToString()?.Trim() ?? "(unknown)",
            SerialNumber: mbo["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
            SizeBytes: ConvertTo<ulong>(mbo["Size"]),
            BusType: ConvertTo<ushort>(mbo["BusType"]),
            IsSystem: ConvertTo<bool>(mbo["IsSystem"]),
            IsBoot: ConvertTo<bool>(mbo["IsBoot"]),
            IsReadOnly: ConvertTo<bool>(mbo["IsReadOnly"]));
    }

    private static T ConvertTo<T>(object? value) where T : struct
    {
        if (value is null)
        {
            return default;
        }
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
