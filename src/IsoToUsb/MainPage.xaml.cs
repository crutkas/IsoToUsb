using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using IsoToUsb.Interop;
using IsoToUsb.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace IsoToUsb;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.RefreshDrives();
            // Drain any startup log lines (e.g. UIPI filter result) so the
            // user can see them in the Log expander.
            foreach (var line in App.StartupLog)
            {
                ViewModel.Log(line);
            }
            App.StartupLog.Clear();
        };
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
