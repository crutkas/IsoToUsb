using IsoToUsb.Services.Internal;

namespace IsoToUsb.Services;

/// <summary>
/// Reads physical disks from the Storage Management API and filters down to
/// the set safe to target: USB bus, not system, not boot, not read-only,
/// not larger than <see cref="MaxTargetableSize"/>, and not a spinning HDD.
/// </summary>
public static class UsbDriveEnumerator
{
    /// <summary>
    /// Hard ceiling on the size of any disk we will ever offer as a target.
    /// 256 GiB comfortably covers commodity USB sticks while rejecting most
    /// external SSDs and any plausible mis-identified internal disk.
    /// </summary>
    public const ulong MaxTargetableSize = 256UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Returns every physical disk on the machine, populated with the same
    /// fields the WMI implementation used to surface. Backed by direct
    /// <c>DeviceIoControl</c> queries (AOT-safe); see <see cref="Win32Storage"/>.
    /// The caller is expected to pipe the result through
    /// <see cref="FilterTargetable"/>.
    /// </summary>
    public static IReadOnlyList<DiskInfo> EnumerateAllDisks() => Win32Storage.EnumerateAllDisks();

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
}
