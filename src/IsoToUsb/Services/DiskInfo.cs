namespace IsoToUsb.Services;

/// <summary>
/// Lightweight snapshot of an <c>MSFT_Disk</c> row. Pure data only — no WMI
/// handles retained — so unit tests can construct instances directly.
/// </summary>
/// <param name="Number">Disk number (used as <c>\\.\PhysicalDriveN</c> and in
/// every other Storage API call).</param>
/// <param name="FriendlyName">Manufacturer/product string shown to the user.</param>
/// <param name="SerialNumber">Vendor serial; used as a stable identity hint.</param>
/// <param name="SizeBytes">Total disk capacity.</param>
/// <param name="BusType">See <see cref="BusTypes"/>.</param>
/// <param name="IsSystem"><c>true</c> if the disk hosts the running OS.</param>
/// <param name="IsBoot"><c>true</c> if the disk is the firmware boot disk.</param>
/// <param name="IsReadOnly"><c>true</c> if the disk is write-protected.</param>
/// <param name="MediaType">Underlying media type from <c>MSFT_PhysicalDisk</c>;
/// see <see cref="MediaTypes"/>. <c>0</c> if unknown.</param>
/// <param name="DriveLetters">Comma-separated Windows drive letters currently
/// assigned to partitions on this disk (e.g. <c>"E:"</c> or <c>"E:, F:"</c>),
/// or empty string when the disk has no mounted partitions.</param>
public sealed record DiskInfo(
    uint Number,
    string FriendlyName,
    string SerialNumber,
    ulong SizeBytes,
    ushort BusType,
    bool IsSystem,
    bool IsBoot,
    bool IsReadOnly,
    ushort MediaType = 0,
    string DriveLetters = "")
{
    /// <summary>Disk reports BusType == USB.</summary>
    public bool IsUsbBus => BusType == BusTypes.Usb;

    /// <summary>True if the underlying media is a spinning hard drive.</summary>
    public bool IsHdd => MediaType == MediaTypes.Hdd;

    /// <summary>Human-friendly size, e.g. "32.0 GB".</summary>
    public string SizeDisplay => FormatBytes(SizeBytes);

    /// <summary>
    /// Drive-letter fragment safe to inline in UI strings: <c>" (E:)"</c>,
    /// <c>" (E:, F:)"</c>, or empty when no letters are assigned. The leading
    /// space lets the binding concatenate cleanly between the disk number
    /// and the friendly name without leaving a stray <c>()</c> behind.
    /// </summary>
    public string DriveLettersTag =>
        string.IsNullOrEmpty(DriveLetters) ? string.Empty : $" ({DriveLetters})";

    private static string FormatBytes(ulong bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        const double TB = GB * 1024;
        return bytes switch
        {
            >= (ulong)TB => $"{bytes / TB:0.0} TB",
            >= (ulong)GB => $"{bytes / GB:0.0} GB",
            >= (ulong)MB => $"{bytes / MB:0.0} MB",
            >= (ulong)KB => $"{bytes / KB:0.0} KB",
            _ => $"{bytes} B"
        };
    }
}
