using IsoToUsb.Services;

namespace IsoToUsb.Tests;

[TestClass]
public sealed class UsbDriveEnumeratorTests
{
    private static DiskInfo MakeDisk(
        uint number = 1,
        ushort bus = BusTypes.Usb,
        bool isSystem = false,
        bool isBoot = false,
        bool isReadOnly = false,
        ulong size = 32UL * 1024 * 1024 * 1024,
        ushort mediaType = MediaTypes.Unspecified) =>
        new(
            Number: number,
            FriendlyName: "Test USB",
            SerialNumber: "ABC123",
            SizeBytes: size,
            BusType: bus,
            IsSystem: isSystem,
            IsBoot: isBoot,
            IsReadOnly: isReadOnly,
            MediaType: mediaType);

    [TestMethod]
    public void Usb_NotSystem_NotBoot_NotReadOnly_IsTargetable()
    {
        Assert.IsTrue(UsbDriveEnumerator.IsTargetable(MakeDisk()));
    }

    [TestMethod]
    [DataRow(BusTypes.Sata)]
    [DataRow(BusTypes.Nvme)]
    [DataRow(BusTypes.Sas)]
    [DataRow(BusTypes.Virtual)]
    [DataRow(BusTypes.FileBackedVirtual)]
    [DataRow(BusTypes.Unknown)]
    public void NonUsbBus_IsRejected(ushort bus)
    {
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(bus: bus)));
    }

    [TestMethod]
    public void SystemDisk_IsRejected_EvenIfUsb()
    {
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(isSystem: true)));
    }

    [TestMethod]
    public void BootDisk_IsRejected_EvenIfUsb()
    {
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(isBoot: true)));
    }

    [TestMethod]
    public void ReadOnlyDisk_IsRejected()
    {
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(isReadOnly: true)));
    }

    [TestMethod]
    public void ZeroSizeDisk_IsRejected()
    {
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(size: 0)));
    }

    [TestMethod]
    public void DiskOverMaxSize_IsRejected()
    {
        var oversized = UsbDriveEnumerator.MaxTargetableSize + 1;
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(size: oversized)));
    }

    [TestMethod]
    public void DiskAtMaxSize_IsAllowed()
    {
        Assert.IsTrue(UsbDriveEnumerator.IsTargetable(MakeDisk(size: UsbDriveEnumerator.MaxTargetableSize)));
    }

    [TestMethod]
    public void HddMedia_IsRejected_EvenOnUsb()
    {
        Assert.IsFalse(UsbDriveEnumerator.IsTargetable(MakeDisk(mediaType: MediaTypes.Hdd)));
    }

    [TestMethod]
    [DataRow((ushort)MediaTypes.Unspecified)]
    [DataRow((ushort)MediaTypes.Ssd)]
    [DataRow((ushort)MediaTypes.Scm)]
    public void NonHddMedia_IsAllowed(ushort mediaType)
    {
        Assert.IsTrue(UsbDriveEnumerator.IsTargetable(MakeDisk(mediaType: mediaType)));
    }

    [TestMethod]
    public void FilterTargetable_RetainsOnlyTargetableDisks()
    {
        var all = new[]
        {
            MakeDisk(number: 1, bus: BusTypes.Nvme, isSystem: true),    // OS disk
            MakeDisk(number: 2, bus: BusTypes.Usb),                     // good
            MakeDisk(number: 3, bus: BusTypes.Sata),                    // internal
            MakeDisk(number: 4, bus: BusTypes.Usb, isReadOnly: true),   // bad
            MakeDisk(number: 5, bus: BusTypes.Usb),                     // good
        };

        var picks = UsbDriveEnumerator.FilterTargetable(all).Select(d => d.Number).ToArray();

        CollectionAssert.AreEquivalent(new uint[] { 2, 5 }, picks);
    }

    [TestMethod]
    public void DiskInfo_SizeDisplay_FormatsGigabytes()
    {
        var d = MakeDisk(size: 32UL * 1024 * 1024 * 1024);
        StringAssert.Contains(d.SizeDisplay, "GB");
    }
}
