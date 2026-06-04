namespace IsoToUsb.Services;

/// <summary>
/// Sanity-checks that a mounted volume contains the file layout we expect
/// from an official Windows install ISO.
/// </summary>
public static class IsoContentValidator
{
    /// <summary>
    /// Throws <see cref="InvalidDataException"/> if the mount root does not
    /// look like a Windows install ISO (no <c>sources\boot.wim</c>).
    /// </summary>
    public static void EnsureWindowsInstallIso(string mountRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        var bootWim = Path.Combine(mountRoot, "sources", "boot.wim");
        if (!File.Exists(bootWim))
        {
            throw new InvalidDataException(
                $"The mounted ISO does not contain 'sources\\boot.wim'. " +
                $"This tool only supports official Windows install ISOs " +
                $"(checked path: '{bootWim}').");
        }
    }

    /// <summary>
    /// Returns the size in bytes of <c>sources\install.wim</c> on the mount,
    /// or 0 if it does not exist (e.g. some Windows ISOs ship install.esd
    /// instead and don't need splitting).
    /// </summary>
    public static long GetInstallWimSize(string mountRoot)
    {
        var installWim = Path.Combine(mountRoot, "sources", "install.wim");
        return File.Exists(installWim) ? new FileInfo(installWim).Length : 0;
    }
}
