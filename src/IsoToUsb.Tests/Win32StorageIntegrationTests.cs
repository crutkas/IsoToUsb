using IsoToUsb.Services;
using IsoToUsb.Services.Internal;

namespace IsoToUsb.Tests;

/// <summary>
/// Integration tests that exercise the real IOCTL-based disk enumeration
/// path against the host machine. Unit tests around <see cref="DiskInfo"/>
/// construction can't catch failures in <see cref="Win32Storage"/> or
/// <see cref="VolumeLookup"/> — those silently return empty when CsWin32
/// signatures, IOCTL access flags, or HANDLE equality semantics drift.
/// The recent silent drive-letter regression slipped past 88 unit tests
/// because every fixture builds <see cref="DiskInfo"/> by hand.
/// </summary>
/// <remarks>
/// These tests assume the host has at least one physical disk hosting the
/// running OS (so the system disk has a drive letter). That's true for
/// every Windows machine including GitHub-hosted runners — they always
/// have a C: drive mounted on disk 0.
/// </remarks>
[TestClass]
public sealed class Win32StorageIntegrationTests
{
    [TestMethod]
    public void EnumerateAllDisks_ReturnsAtLeastOneDisk()
    {
        var disks = Win32Storage.EnumerateAllDisks();
        Assert.IsTrue(
            disks.Count > 0,
            "EnumerateAllDisks returned zero disks — every Windows host has a system disk.");
    }

    [TestMethod]
    public void EnumerateAllDisks_PopulatesSizeAndFriendlyName()
    {
        var disks = Win32Storage.EnumerateAllDisks();
        Assert.IsTrue(disks.Count > 0, "Need at least one disk to validate.");

        foreach (var disk in disks)
        {
            Assert.IsTrue(
                disk.SizeBytes > 0,
                $"Disk {disk.Number} reported SizeBytes=0 — IOCTL_DISK_GET_DRIVE_GEOMETRY_EX likely failed.");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(disk.FriendlyName),
                $"Disk {disk.Number} reported empty FriendlyName — IOCTL_STORAGE_QUERY_PROPERTY likely failed.");
        }
    }

    [TestMethod]
    public void EnumerateAllDisks_AtLeastOneDiskHasDriveLetter()
    {
        // The system disk (where %windir% lives) always has a drive letter.
        // If this fails, VolumeLookup.GetDriveLettersByDiskNumber() is silently
        // returning empty — the exact regression this test exists to catch.
        var disks = Win32Storage.EnumerateAllDisks();

        var withLetters = disks.Where(d => !string.IsNullOrEmpty(d.DriveLetters)).ToList();

        Assert.IsTrue(
            withLetters.Count > 0,
            "No disk reported any drive letters. The host must have at least a system drive " +
            "(C:) — VolumeLookup is failing to walk FindFirstVolume / IOCTL_STORAGE_GET_DEVICE_NUMBER " +
            "/ GetVolumePathNamesForVolumeName. See Services/Internal/VolumeLookup.cs.");
    }

    [TestMethod]
    public void GetDriveLettersByDiskNumber_IncludesSystemDisk()
    {
        // Resolve the system drive letter via Environment.SystemDirectory
        // (e.g. "C:\Windows" → 'C'), then assert VolumeLookup's reverse map
        // contains an entry for the disk hosting that letter.
        var sysDir = Environment.SystemDirectory;
        Assert.IsTrue(
            sysDir.Length >= 2 && sysDir[1] == ':',
            $"Unexpected SystemDirectory shape: {sysDir}");
        var systemLetter = char.ToUpperInvariant(sysDir[0]);

        var systemDiskNumber = VolumeLookup.GetDiskNumberForDriveLetter(systemLetter);
        Assert.IsNotNull(
            systemDiskNumber,
            $"GetDiskNumberForDriveLetter('{systemLetter}') returned null — CreateFile on " +
            $@"\\.\{systemLetter}: failed or IOCTL_STORAGE_GET_DEVICE_NUMBER was refused.");

        var map = VolumeLookup.GetDriveLettersByDiskNumber();
        Assert.IsTrue(
            map.ContainsKey(systemDiskNumber.Value),
            $"GetDriveLettersByDiskNumber() returned a map with no entry for disk " +
            $"{systemDiskNumber.Value} (the system disk). Volume iteration is broken.");

        var letters = map[systemDiskNumber.Value];
        StringAssert.Contains(
            letters,
            $"{systemLetter}:",
            $"System disk's drive-letter list ('{letters}') is missing the system letter '{systemLetter}:'.");
    }

    [TestMethod]
    public void FindDriveLetterForDisk_FindsSystemDiskLetter()
    {
        var sysDir = Environment.SystemDirectory;
        var systemLetter = char.ToUpperInvariant(sysDir[0]);
        var systemDiskNumber = VolumeLookup.GetDiskNumberForDriveLetter(systemLetter);
        Assert.IsNotNull(systemDiskNumber, "Need system disk number to validate FindDriveLetterForDisk.");

        var found = VolumeLookup.FindDriveLetterForDisk(systemDiskNumber.Value);
        Assert.IsNotNull(
            found,
            $"FindDriveLetterForDisk({systemDiskNumber.Value}) returned null for the system disk.");
        // FindDriveLetterForDisk returns the first drive letter on the disk,
        // which may not be the system letter on multi-partition disks (e.g.
        // a system disk with a recovery partition that's been assigned a
        // letter). Just verify it's a plausible drive letter.
        Assert.IsTrue(
            found.Value >= 'A' && found.Value <= 'Z',
            $"FindDriveLetterForDisk returned non-letter '{found.Value}'.");
    }
}
