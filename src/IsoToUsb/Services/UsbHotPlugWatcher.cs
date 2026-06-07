using System.Management;

namespace IsoToUsb.Services;

/// <summary>
/// Fires <see cref="Changed"/> when a disk is added or removed at the OS
/// level (USB insertion/removal, eSATA, etc.) so the UI can refresh its
/// drive list without forcing the user to click Refresh.
/// </summary>
/// <remarks>
/// Implemented on top of WMI's <c>__InstanceOperationEvent</c> against
/// <c>Win32_DiskDrive</c>. WMI polls internally at the WITHIN interval
/// (2 s here) — fast enough to feel instant on insert, light enough not
/// to burn CPU. Events fire on a thread-pool thread; subscribers MUST
/// marshal back to the UI thread.
/// </remarks>
public sealed class UsbHotPlugWatcher : IDisposable
{
    private ManagementEventWatcher? _watcher;
    private DateTime _lastFiredUtc = DateTime.MinValue;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    public event EventHandler? Changed;

    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }
        var query = new WqlEventQuery(
            "SELECT * FROM __InstanceOperationEvent WITHIN 2 " +
            "WHERE TargetInstance ISA 'Win32_DiskDrive'");
        _watcher = new ManagementEventWatcher(query);
        _watcher.EventArrived += OnEventArrived;
        _watcher.Start();
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        // Coalesce the arrival + creation + modification + volume mount
        // bursts that fire in quick succession for a single USB insertion.
        var now = DateTime.UtcNow;
        if (now - _lastFiredUtc < Debounce)
        {
            return;
        }
        _lastFiredUtc = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }
        try
        {
            _watcher.EventArrived -= OnEventArrived;
            _watcher.Stop();
        }
        catch
        {
            // best-effort cleanup
        }
        _watcher.Dispose();
        _watcher = null;
    }
}
