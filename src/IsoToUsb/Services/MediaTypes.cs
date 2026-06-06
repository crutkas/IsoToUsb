namespace IsoToUsb.Services;

/// <summary>
/// MediaType values reported by <c>MSFT_PhysicalDisk</c>.
/// See: https://learn.microsoft.com/windows-hardware/drivers/storage/msft-physicaldisk
/// </summary>
public static class MediaTypes
{
    /// <summary>Type unknown to the driver — typical for USB sticks.</summary>
    public const ushort Unspecified = 0;

    /// <summary>Spinning magnetic hard disk drive.</summary>
    public const ushort Hdd = 3;

    /// <summary>Solid-state drive (NAND).</summary>
    public const ushort Ssd = 4;

    /// <summary>Storage Class Memory (e.g. Intel Optane).</summary>
    public const ushort Scm = 5;
}
