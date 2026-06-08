using System.Runtime.CompilerServices;
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
/// <remarks>
/// Implemented as a function-pointer vtable call rather than a
/// <c>[ComImport]</c> RCW so it is AOT-clean: <c>Activator.CreateInstance</c>
/// and reflection-based COM marshalling are forbidden under NativeAOT, but
/// a raw <c>CoCreateInstance</c> + manual vtable invocation works without
/// any runtime metadata.
/// </remarks>
public static unsafe partial class TaskbarProgress
{
    // CLSID_TaskbarList — {56FDF344-FD6D-11D0-958A-006097C9A090}
    private static readonly Guid CLSID_TaskbarList = new(0x56FDF344, 0xFD6D, 0x11D0,
        0x95, 0x8A, 0x00, 0x60, 0x97, 0xC9, 0xA0, 0x90);

    // IID_ITaskbarList3 — {EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF}
    private static readonly Guid IID_ITaskbarList3 = new(0xEA1AFB91, 0x9E28, 0x4B86,
        0x90, 0xE9, 0x9E, 0x9F, 0x8A, 0x5E, 0xEF, 0xAF);

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    // ITaskbarList3 vtable slot layout. IUnknown is 0..2, then ITaskbarList
    // (5 methods), ITaskbarList2 (1), ITaskbarList3 (then SetProgressValue
    // and SetProgressState as the first two). Stable since Windows 7.
    private const int Slot_Release = 2;
    private const int Slot_HrInit = 3;
    private const int Slot_SetProgressValue = 9;
    private const int Slot_SetProgressState = 10;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        void* pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out void* ppv);

    private static readonly object Gate = new();
    private static void* _taskbar;     // raw ITaskbarList3*
    private static bool _initFailed;

    public static void SetState(nint hwnd, TaskbarProgressState state)
    {
        if (hwnd == 0)
        {
            return;
        }
        var tb = TryGet();
        if (tb == null)
        {
            return;
        }
        try
        {
            var vt = *(void***)tb;
            var setState = (delegate* unmanaged[Stdcall]<void*, nint, uint, int>)vt[Slot_SetProgressState];
            _ = setState(tb, hwnd, (uint)state);
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
        if (tb == null)
        {
            return;
        }
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            var vt = *(void***)tb;
            var setValue = (delegate* unmanaged[Stdcall]<void*, nint, ulong, ulong, int>)vt[Slot_SetProgressValue];
            _ = setValue(tb, hwnd, (ulong)percent, 100UL);
        }
        catch
        {
        }
    }

    public static void Clear(nint hwnd) => SetState(hwnd, TaskbarProgressState.None);

    private static void* TryGet()
    {
        if (_initFailed)
        {
            return null;
        }
        if (_taskbar != null)
        {
            return _taskbar;
        }
        lock (Gate)
        {
            if (_initFailed)
            {
                return null;
            }
            if (_taskbar != null)
            {
                return _taskbar;
            }
            try
            {
                var hr = CoCreateInstance(
                    in CLSID_TaskbarList,
                    null,
                    CLSCTX_INPROC_SERVER,
                    in IID_ITaskbarList3,
                    out var pv);
                if (hr < 0 || pv == null)
                {
                    _initFailed = true;
                    return null;
                }

                // HrInit() — ITaskbarList method that must be called before
                // SetProgressValue / SetProgressState produce any visible
                // effect. Some Windows versions return S_OK; others return
                // S_FALSE on a non-taskbar HWND. Treat anything failing-bit
                // as "no overlay this session" but release the COM pointer
                // so we don't leak.
                var vt = *(void***)pv;
                var hrInit = (delegate* unmanaged[Stdcall]<void*, int>)vt[Slot_HrInit];
                var initHr = hrInit(pv);
                if (initHr < 0)
                {
                    Release(pv);
                    _initFailed = true;
                    return null;
                }

                _taskbar = pv;
                return _taskbar;
            }
            catch
            {
                _initFailed = true;
                return null;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Release(void* p)
    {
        if (p == null) return;
        var vt = *(void***)p;
        var release = (delegate* unmanaged[Stdcall]<void*, uint>)vt[Slot_Release];
        _ = release(p);
    }
}
