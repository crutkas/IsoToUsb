using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace IsoToUsb.Services.Internal;

/// <summary>
/// Maps Windows volume objects (drive letters, volume GUID paths) to the
/// physical disk number they live on. Replaces the
/// <c>MSFT_Partition WHERE DiskNumber = N</c> WMI calls scattered through
/// DiskPartitioner + IsoMounter.
/// </summary>
/// <remarks>
/// The Win32 idiom is: walk <c>FindFirstVolume</c> / <c>FindNextVolume</c>,
/// open each volume handle, call <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c> for
/// the disk it sits on, and call <c>GetVolumePathNamesForVolumeNameW</c> for
/// the assigned mount points (drive letters and any mounted folders). This
/// is the same approach <c>mountmgr</c> exposes — no WMI hop.
/// </remarks>
public static unsafe class VolumeLookup
{
    /// <summary>
    /// Builds a map from disk number → comma-separated drive letters
    /// (e.g. <c>"E:"</c>, <c>"E:, F:"</c>). Drive-less volumes are skipped.
    /// </summary>
    public static IReadOnlyDictionary<uint, string> GetDriveLettersByDiskNumber()
    {
        var byDisk = new Dictionary<uint, List<char>>();
        ForEachVolume((volumeName, diskNumber) =>
        {
            var letter = TryGetSingleDriveLetter(volumeName);
            if (letter is null)
            {
                return;
            }
            if (!byDisk.TryGetValue(diskNumber, out var letters))
            {
                letters = new List<char>(capacity: 2);
                byDisk[diskNumber] = letters;
            }
            if (!letters.Contains(letter.Value))
            {
                letters.Add(letter.Value);
            }
        });

        return byDisk.ToDictionary(
            kvp => kvp.Key,
            kvp => string.Join(", ", kvp.Value.Order().Select(c => $"{c}:")));
    }

    /// <summary>
    /// Returns the first drive letter assigned to a volume on the given
    /// disk, or <c>null</c> if none. Used by the post-format wait loops.
    /// </summary>
    public static char? FindDriveLetterForDisk(uint diskNumber)
    {
        char? found = null;
        ForEachVolume((volumeName, n) =>
        {
            if (found is not null || n != diskNumber)
            {
                return;
            }
            found = TryGetSingleDriveLetter(volumeName);
        });
        return found;
    }

    /// <summary>
    /// Returns the disk number that the volume mounted at
    /// <paramref name="driveLetter"/> lives on. <c>null</c> when the letter
    /// isn't mounted, isn't on a physical disk, or the IOCTL is refused
    /// (e.g. for network drives).
    /// </summary>
    public static uint? GetDiskNumberForDriveLetter(char driveLetter)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        if (letter < 'A' || letter > 'Z')
        {
            return null;
        }
        // Volume open path is "\\.\X:" (no trailing slash). Required share
        // mode is RW because system volumes are perpetually open elsewhere.
        var path = $@"\\.\{letter}:";
        using var handle = PInvoke.CreateFile(
            path,
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            hTemplateFile: null);
        if (handle.IsInvalid)
        {
            return null;
        }
        return TryGetDeviceNumber(handle);
    }

    private static char? TryGetSingleDriveLetter(string volumeName)
    {
        // GetVolumePathNamesForVolumeName returns a double-null-terminated
        // multi-string of mount points. We only care about drive-letter
        // mount points ("X:\"), not arbitrary mounted folders.
        const int bufferSize = 1024;
        var buffer = stackalloc char[bufferSize];
        var ok = PInvoke.GetVolumePathNamesForVolumeName(
            volumeName,
            buffer,
            bufferSize,
            out var returnedLen);
        if (!ok || returnedLen == 0)
        {
            return null;
        }

        // Walk the multi-string in place.
        for (int i = 0; i < returnedLen;)
        {
            int start = i;
            while (i < returnedLen && buffer[i] != '\0') { i++; }
            var segLen = i - start;
            i++; // skip the null between entries

            if (segLen >= 3 && buffer[start + 1] == ':' && buffer[start + 2] == '\\')
            {
                var ch = char.ToUpperInvariant(buffer[start]);
                if (ch >= 'A' && ch <= 'Z')
                {
                    return ch;
                }
            }
        }
        return null;
    }

    private static uint? TryGetDeviceNumber(SafeFileHandle handle)
    {
        STORAGE_DEVICE_NUMBER number;
        uint bytesReturned = 0;
        var ok = PInvoke.DeviceIoControl(
            new HANDLE(handle.DangerousGetHandle()),
            PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER,
            lpInBuffer: null,
            nInBufferSize: 0,
            &number,
            (uint)sizeof(STORAGE_DEVICE_NUMBER),
            &bytesReturned,
            lpOverlapped: null);
        if (!ok)
        {
            return null;
        }
        if (number.DeviceNumber == unchecked((uint)-1))
        {
            return null;
        }
        return number.DeviceNumber;
    }

    private static void ForEachVolume(Action<string, uint> visit)
    {
        const int bufferSize = 256;
        var nameBuf = stackalloc char[bufferSize];

        var findHandle = PInvoke.FindFirstVolume(nameBuf, bufferSize);
        if (findHandle == InvalidHandle)
        {
            return;
        }

        try
        {
            do
            {
                // FindFirst/Next yield "\\?\Volume{GUID}\" — keep the
                // trailing backslash for GetVolumePathNamesForVolumeName but
                // strip it for CreateFile.
                var volumeName = new string(nameBuf);
                if (string.IsNullOrEmpty(volumeName))
                {
                    continue;
                }
                var openPath = volumeName.TrimEnd('\\');

                using var volumeHandle = PInvoke.CreateFile(
                    openPath,
                    0,
                    FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                    lpSecurityAttributes: null,
                    FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                    FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                    hTemplateFile: null);
                if (volumeHandle.IsInvalid)
                {
                    continue;
                }
                var diskNumber = TryGetDeviceNumber(volumeHandle);
                if (diskNumber is null)
                {
                    continue;
                }
                visit(volumeName, diskNumber.Value);
            }
            while (PInvoke.FindNextVolume(findHandle, nameBuf, bufferSize));
        }
        finally
        {
            PInvoke.FindVolumeClose(findHandle);
        }
    }

    private static readonly HANDLE InvalidHandle = new(new IntPtr(-1));
}
