namespace IsoToUsb.Services;

/// <summary>
/// Disk BusType values reported by the Storage Management API
/// (<c>MSFT_Disk.BusType</c>). Names mirror the official enumeration; only
/// the values we actually care about have inline comments.
/// See: https://learn.microsoft.com/windows-hardware/drivers/storage/msft-disk
/// </summary>
public static class BusTypes
{
    public const ushort Unknown = 0;
    public const ushort Scsi = 1;
    public const ushort Atapi = 2;
    public const ushort Ata = 3;
    public const ushort Ieee1394 = 4;
    public const ushort Ssa = 5;
    public const ushort FibreChannel = 6;

    /// <summary>USB. The only bus we accept as a target.</summary>
    public const ushort Usb = 7;

    public const ushort RAID = 8;
    public const ushort iSCSI = 9;
    public const ushort Sas = 10;
    public const ushort Sata = 11;
    public const ushort Sd = 12;
    public const ushort Mmc = 13;
    public const ushort Virtual = 14;
    public const ushort FileBackedVirtual = 15;
    public const ushort Spaces = 16;
    public const ushort Nvme = 17;
    public const ushort SCM = 18;
    public const ushort UFS = 19;
    public const ushort MaxReserved = 127;
}
