using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IsoToUsb.Services;

/// <summary>
/// Fires <see cref="Changed"/> when a disk is added or removed at the OS
/// level (USB insertion/removal, eSATA, etc.) so the UI can refresh its
/// drive list without forcing the user to click Refresh.
/// </summary>
/// <remarks>
/// Implemented on top of <c>WM_DEVICECHANGE</c> + <c>RegisterDeviceNotification</c>
/// against <c>GUID_DEVINTERFACE_DISK</c> on a message-only window running
/// on a dedicated background thread. Replaces the prior
/// <c>ManagementEventWatcher</c> (WMI / <c>System.Management</c>) impl,
/// which used reflection on internal COM ctors and is not AOT-safe.
/// Events fire on the watcher thread; subscribers MUST marshal back to
/// the UI thread.
/// </remarks>
public sealed partial class UsbHotPlugWatcher : IDisposable
{
    private const string WindowClassName = "IsoToUsb.UsbHotPlugWatcher";
    private const uint WM_DEVICECHANGE = 0x0219;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint DBT_DEVICEARRIVAL = 0x8000;
    private const uint DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const uint DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x0;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // GUID_DEVINTERFACE_DISK — {53F56307-B6BF-11D0-94F2-00A0C91EFB8B}
    private static readonly Guid GuidDevInterfaceDisk = new(
        0x53F56307, 0xB6BF, 0x11D0,
        0x94, 0xF2, 0x00, 0xA0, 0xC9, 0x1E, 0xFB, 0x8B);

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    // We support a single live watcher at a time. The static WndProc has no
    // way to recover the instance from the HWND (we don't subclass with
    // GWLP_USERDATA — keeps SetWindowLongPtr off the AOT surface), so the
    // singleton sidesteps that. Locked via Interlocked.CompareExchange to
    // detect accidental double-Start.
    private static UsbHotPlugWatcher? _instance;

    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _notifyHandle;
    private long _lastFiredTicks;
    private bool _disposed;
    private readonly ManualResetEventSlim _started = new(false);

    public event EventHandler? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _instance, this, null) is not null)
        {
            throw new InvalidOperationException(
                "Only one UsbHotPlugWatcher may run at a time.");
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "UsbHotPlugWatcher",
        };
        _thread.Start();
        _started.Wait();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Ask the message loop to break by posting WM_CLOSE; WndProc
        // converts WM_DESTROY into PostQuitMessage which exits GetMessage.
        if (_hwnd != IntPtr.Zero)
        {
            _ = PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        try
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best-effort cleanup
        }
        _thread = null;
        Interlocked.CompareExchange(ref _instance, null, this);
        _started.Dispose();
    }

    private unsafe void MessageLoop()
    {
        var hinstance = GetModuleHandleW(null);

        // Register the window class. lpfnWndProc must be a function-pointer
        // (NOT a managed delegate) so this stays AOT-clean.
        var className = Marshal.StringToHGlobalUni(WindowClassName);
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProcStatic,
                hInstance = hinstance,
                lpszClassName = className,
            };
            // RegisterClassEx returns the class atom (>0) on success or 0 on
            // failure. We ignore ERROR_CLASS_ALREADY_EXISTS because a
            // stop-then-start cycle may leave the class registered.
            _ = RegisterClassExW(in wc);

            _hwnd = CreateWindowExW(
                dwExStyle: 0,
                lpClassName: className,
                lpWindowName: IntPtr.Zero,
                dwStyle: 0,
                X: 0, Y: 0, nWidth: 0, nHeight: 0,
                hWndParent: HWND_MESSAGE,
                hMenu: IntPtr.Zero,
                hInstance: hinstance,
                lpParam: IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                var filter = new DEV_BROADCAST_DEVICEINTERFACE_W
                {
                    dbcc_size = (uint)sizeof(DEV_BROADCAST_DEVICEINTERFACE_W),
                    dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_reserved = 0,
                    dbcc_classguid = GuidDevInterfaceDisk,
                };
                _notifyHandle = RegisterDeviceNotificationW(_hwnd, &filter, DEVICE_NOTIFY_WINDOW_HANDLE);
            }

            _started.Set();

            // Run the message pump until WM_QUIT.
            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(in msg);
                _ = DispatchMessageW(in msg);
            }
        }
        finally
        {
            if (_notifyHandle != IntPtr.Zero)
            {
                _ = UnregisterDeviceNotification(_notifyHandle);
                _notifyHandle = IntPtr.Zero;
            }
            if (_hwnd != IntPtr.Zero)
            {
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            _ = UnregisterClassW(className, hinstance);
            Marshal.FreeHGlobal(className);
            _started.Set(); // unblock Start() even if class registration failed
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static IntPtr WndProcStatic(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam)
    {
        switch (msg)
        {
            case WM_DEVICECHANGE:
                var w = (uint)wparam.ToInt64();
                if (w == DBT_DEVICEARRIVAL || w == DBT_DEVICEREMOVECOMPLETE)
                {
                    FireDebounced();
                    return new IntPtr(1);
                }
                break;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FireDebounced()
    {
        var inst = _instance;
        if (inst is null)
        {
            return;
        }
        var nowTicks = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref inst._lastFiredTicks);
        if (nowTicks - last < Debounce.Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref inst._lastFiredTicks, nowTicks);
        try
        {
            inst.Changed?.Invoke(inst, EventArgs.Empty);
        }
        catch
        {
            // Subscribers must not be allowed to take down the message pump.
        }
    }

    // ------------------------------------------------------------------
    // Manual P/Invoke. We use LibraryImport for AOT-clean source-gen
    // marshalling. CsWin32 with allowMarshaling=true would emit delegate-
    // based WNDPROC plumbing that is not NativeAOT-friendly, so the
    // window-management surface is hand-rolled here.
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE_W
    {
        public uint dbcc_size;
        public uint dbcc_devicetype;
        public uint dbcc_reserved;
        public Guid dbcc_classguid;
        // dbcc_name (WCHAR[1]) follows — we never read it on filter, and
        // for filter-input it can be omitted (kernel sees dbcc_size as the
        // canonical length).
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(IntPtr lpClassName, IntPtr hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle,
        IntPtr lpClassName,
        IntPtr lpWindowName,
        uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hwnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    private static unsafe partial IntPtr RegisterDeviceNotificationW(
        IntPtr hRecipient,
        DEV_BROADCAST_DEVICEINTERFACE_W* notificationFilter,
        uint flags);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterDeviceNotification", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterDeviceNotification(IntPtr handle);
}
