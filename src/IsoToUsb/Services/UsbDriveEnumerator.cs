using System.Management;

namespace IsoToUsb.Services;

/// <summary>
/// Reads physical disks from the Storage Management API and filters down to
/// the set safe to target: USB bus, not system, not boot, not read-only,
/// not larger than <see cref="MaxTargetableSize"/>, and not a spinning HDD.
/// </summary>
public static class UsbDriveEnumerator
{
    private const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary>
    /// Hard ceiling on the size of any disk we will ever offer as a target.
    /// 256 GiB comfortably covers commodity USB sticks while rejecting most
    /// external SSDs and any plausible mis-identified internal disk.
    /// </summary>
    public const ulong MaxTargetableSize = 256UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Returns every <c>MSFT_Disk</c> instance on the machine, with <see
    /// cref="DiskInfo.MediaType"/> populated from <c>MSFT_PhysicalDisk</c> when
    /// the numbers line up. The caller is expected to pipe the result through
    /// <see cref="FilterTargetable"/>.
    /// </summary>
    public static IReadOnlyList<DiskInfo> EnumerateAllDisks()
    {
        var scope = new ManagementScope(StorageNamespace);
        var mediaTypes = QueryMediaTypes(scope);
        var driveLetters = QueryDriveLetters(scope);

        var query = new ObjectQuery(
            "SELECT Number, FriendlyName, SerialNumber, Size, BusType, IsSystem, IsBoot, IsReadOnly FROM MSFT_Disk");
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var collection = searcher.Get();

        var results = new List<DiskInfo>(collection.Count);
        foreach (ManagementObject disk in collection)
        {
            using (disk)
            {
                results.Add(MapToDiskInfo(disk, mediaTypes, driveLetters));
            }
        }
        return results;
    }

    /// <summary>
    /// USB-bus disks that aren't the system disk, boot disk, write-protected,
    /// larger than <see cref="MaxTargetableSize"/>, or backed by spinning media.
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
            && disk.SizeBytes > 0
            && disk.SizeBytes <= MaxTargetableSize
            && !disk.IsHdd;
    }

    internal static DiskInfo MapToDiskInfo(
        ManagementBaseObject mbo,
        IReadOnlyDictionary<uint, ushort>? mediaTypes = null,
        IReadOnlyDictionary<uint, string>? driveLetters = null)
    {
        var number = ConvertTo<uint>(mbo["Number"]);
        ushort mediaType = MediaTypes.Unspecified;
        if (mediaTypes is not null && mediaTypes.TryGetValue(number, out var mt))
        {
            mediaType = mt;
        }
        string letters = string.Empty;
        if (driveLetters is not null && driveLetters.TryGetValue(number, out var dl))
        {
            letters = dl;
        }
        return new DiskInfo(
            Number: number,
            FriendlyName: mbo["FriendlyName"]?.ToString()?.Trim() ?? "(unknown)",
            SerialNumber: mbo["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
            SizeBytes: ConvertTo<ulong>(mbo["Size"]),
            BusType: ConvertTo<ushort>(mbo["BusType"]),
            IsSystem: ConvertTo<bool>(mbo["IsSystem"]),
            IsBoot: ConvertTo<bool>(mbo["IsBoot"]),
            IsReadOnly: ConvertTo<bool>(mbo["IsReadOnly"]),
            MediaType: mediaType,
            DriveLetters: letters);
    }

    /// <summary>
    /// Builds a map from <c>MSFT_Disk.Number</c> → <c>MSFT_PhysicalDisk.MediaType</c>.
    /// Returns an empty dictionary if the WMI query throws (e.g. permission
    /// or driver issues) so enumeration never fails over a bonus signal.
    /// </summary>
    private static IReadOnlyDictionary<uint, ushort> QueryMediaTypes(ManagementScope scope)
    {
        var map = new Dictionary<uint, ushort>();
        try
        {
            var query = new ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var collection = searcher.Get();
            foreach (ManagementObject pd in collection)
            {
                using (pd)
                {
                    var deviceId = pd["DeviceId"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId)
                        || !uint.TryParse(deviceId, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        continue;
                    }
                    map[number] = ConvertTo<ushort>(pd["MediaType"]);
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return map;
    }

    /// <summary>
    /// Builds a map from <c>MSFT_Disk.Number</c> → comma-separated list of
    /// assigned drive letters (e.g. <c>"E:"</c> or <c>"E:, F:"</c>). Disks
    /// with no mounted partitions are absent from the map. Failures are
    /// swallowed so enumeration never breaks over a cosmetic signal.
    /// </summary>
    private static IReadOnlyDictionary<uint, string> QueryDriveLetters(ManagementScope scope)
    {
        var map = new Dictionary<uint, List<char>>();
        try
        {
            var query = new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var collection = searcher.Get();
            foreach (ManagementObject part in collection)
            {
                using (part)
                {
                    var diskNumber = ConvertTo<uint>(part["DiskNumber"]);
                    var ch = part["DriveLetter"] switch
                    {
                        char c => c,
                        ushort u => (char)u,
                        byte b => (char)b,
                        _ => '\0',
                    };
                    if (ch < 'A' || ch > 'Z')
                    {
                        continue;
                    }
                    if (!map.TryGetValue(diskNumber, out var letters))
                    {
                        letters = new List<char>(capacity: 2);
                        map[diskNumber] = letters;
                    }
                    if (!letters.Contains(ch))
                    {
                        letters.Add(ch);
                    }
                }
            }
        }
        catch (ManagementException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return map.ToDictionary(
            kvp => kvp.Key,
            kvp => string.Join(", ", kvp.Value.Order().Select(c => $"{c}:")));
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
