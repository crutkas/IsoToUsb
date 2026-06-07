using System.Runtime.InteropServices;

namespace IsoToUsb.Services;

/// <summary>
/// Mirrors Win32 <c>TBPFLAG</c> — the taskbar-button progress states
/// supported by <c>ITaskbarList3</c>. Drives the colored overlay on the
/// app's taskbar icon (green = normal, yellow = paused, red = error,
/// marquee = indeterminate, none = clear).
/// </summary>
public enum TaskbarProgressState
{
    None = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8,
}

/// <summary>
/// Thin facade over <c>ITaskbarList3</c> so the rest of the app doesn't
/// have to deal with COM. Safe to call from any thread; on first use the
/// instance is created and <c>HrInit</c>'d. All errors are swallowed —
/// a missing taskbar overlay is never worth crashing the build for.
/// </summary>
public static class TaskbarProgress
{
    private static readonly object Gate = new();
    private static ITaskbarList3? _taskbarList;
    private static bool _initFailed;

    public static void SetState(nint hwnd, TaskbarProgressState state)
    {
        if (hwnd == 0)
        {
            return;
        }
        var tb = TryGet();
        if (tb is null)
        {
            return;
        }
        try
        {
            tb.SetProgressState(hwnd, (uint)state);
        }
        catch
        {
        }
    }

    public static void SetValue(nint hwnd, int percent)
    {
        if (hwnd == 0)
        {
            return;
        }
        var tb = TryGet();
        if (tb is null)
        {
            return;
        }
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            tb.SetProgressValue(hwnd, (ulong)percent, 100UL);
        }
        catch
        {
        }
    }

    public static void Clear(nint hwnd) => SetState(hwnd, TaskbarProgressState.None);

    private static ITaskbarList3? TryGet()
    {
        if (_initFailed)
        {
            return null;
        }
        if (_taskbarList is not null)
        {
            return _taskbarList;
        }
        lock (Gate)
        {
            if (_initFailed)
            {
                return null;
            }
            if (_taskbarList is not null)
            {
                return _taskbarList;
            }
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
                if (type is null)
                {
                    _initFailed = true;
                    return null;
                }
                var instance = (ITaskbarList3)Activator.CreateInstance(type)!;
                instance.HrInit();
                _taskbarList = instance;
                return _taskbarList;
            }
            catch
            {
                _initFailed = true;
                return null;
            }
        }
    }

    // Minimal ITaskbarList3 vtable — only the three methods we need. The
    // base ITaskbarList members (HrInit, AddTab, DeleteTab, ActivateTab,
    // SetActiveAlt) must appear in order so the COM vtable layout matches.
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3 (only what we use)
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, uint state);
    }
}
