using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using IsoToUsb.Interop;
using IsoToUsb.Services;
using IsoToUsb.ViewModels;
using System.Collections.Specialized;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace IsoToUsb;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    private UsbHotPlugWatcher? _hotPlugWatcher;
    private bool _logPinnedToBottom = true;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDrives();
        // Drain any startup log lines (e.g. UIPI filter result) so the
        // user can see them in the Log expander.
        foreach (var line in App.StartupLog)
        {
            ViewModel.Log(line);
        }
        App.StartupLog.Clear();

        // Auto-scroll the log to the latest line, but only when the user
        // is already pinned to the bottom — if they've scrolled up to
        // inspect, don't yank them back.
        ViewModel.LogLines.CollectionChanged += OnLogLinesChanged;
        LogScrollViewer.ViewChanged += OnLogScrollViewChanged;
        ScrollLogToBottom();

        // Re-enumerate when a USB stick is plugged in or pulled out so
        // the user doesn't have to click Refresh.
        try
        {
            _hotPlugWatcher = new UsbHotPlugWatcher();
            _hotPlugWatcher.Changed += OnHotPlugChanged;
            _hotPlugWatcher.Start();
        }
        catch (System.Exception ex)
        {
            ViewModel.Log($"Hot-plug watcher unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LogLines.CollectionChanged -= OnLogLinesChanged;
        LogScrollViewer.ViewChanged -= OnLogScrollViewChanged;
        if (_hotPlugWatcher is not null)
        {
            _hotPlugWatcher.Changed -= OnHotPlugChanged;
            _hotPlugWatcher.Dispose();
            _hotPlugWatcher = null;
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add &&
            e.Action != NotifyCollectionChangedAction.Reset)
        {
            return;
        }
        if (!_logPinnedToBottom)
        {
            return;
        }
        // The new TextBlock hasn't been measured yet; defer one tick so
        // ScrollableHeight reflects the just-added row.
        App.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            ScrollLogToBottom);
    }

    private void OnLogScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            return;
        }
        // Treat "within 4 px of bottom" as pinned so a rounding gap from
        // measure-vs-arrange doesn't cause us to give up on auto-scroll.
        const double tolerance = 4.0;
        _logPinnedToBottom =
            LogScrollViewer.ScrollableHeight <= 0 ||
            LogScrollViewer.VerticalOffset >= LogScrollViewer.ScrollableHeight - tolerance;
    }

    private void ScrollLogToBottom()
    {
        LogScrollViewer.UpdateLayout();
        LogScrollViewer.ChangeView(
            horizontalOffset: null,
            verticalOffset: LogScrollViewer.ScrollableHeight,
            zoomFactor: null,
            disableAnimation: true);
    }

    private void OnHotPlugChanged(object? sender, System.EventArgs e)
    {
        // WMI fires on a thread-pool thread; marshal to the UI thread
        // before touching ObservableCollections / bindings.
        App.DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.IsBusy)
            {
                return;
            }
            ViewModel.RefreshDrives();
        });
    }

    private void OnDropTargetDragOver(object sender, DragEventArgs e)
    {
        try
        {
            if (ViewModel.IsBusy)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Use this ISO";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.LogDropError($"DragOver failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async void OnDropTargetDrop(object sender, DragEventArgs e)
    {
        // async void event handlers must catch everything; an unhandled
        // exception here (very common with elevated-app drag-drop from
        // medium-IL Explorer) bubbles into the XAML callback and fails-fast
        // the whole process (0xc000027b).
        DragOperationDeferral? deferral = null;
        try
        {
            if (ViewModel.IsBusy)
            {
                return;
            }
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                ViewModel.LogDropError("Drop ignored: no StorageItems on the data view.");
                return;
            }
            deferral = e.GetDeferral();
            var items = await e.DataView.GetStorageItemsAsync();
            var iso = items.OfType<StorageFile>()
                .FirstOrDefault(f => f.FileType.Equals(".iso", System.StringComparison.OrdinalIgnoreCase));
            if (iso is not null)
            {
                ViewModel.SetIso(iso.Path);
            }
            else
            {
                ViewModel.LogDropError("Drop did not contain a .iso file.");
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.LogDropError(
                $"Drag-drop failed ({ex.GetType().Name}: {ex.Message}). " +
                "Use the Browse button instead.");
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        // FileOpenPicker (WinRT) silently returns null in elevated unpackaged
        // WinUI 3 apps. Use Win32 GetOpenFileNameW instead — it works.
        try
        {
            var path = Win32FilePicker.PickFile(
                ownerHwnd: App.WindowHandle,
                title: "Select Windows ISO",
                filter: "ISO image files (*.iso)\0*.iso\0All files (*.*)\0*.*\0");
            if (!string.IsNullOrWhiteSpace(path))
            {
                ViewModel.SetIso(path);
            }
        }
        catch (System.Exception ex)
        {
            ViewModel.LogDropError($"Browse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
