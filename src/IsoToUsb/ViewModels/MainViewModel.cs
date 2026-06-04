using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsoToUsb.Services;

namespace IsoToUsb.ViewModels;

/// <summary>
/// The single ViewModel driving the IsoToUsb UI: pick ISO, pick drive,
/// click Start, watch progress.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
    }

    [ObservableProperty]
    public partial string? IsoPath { get; set; }

    [ObservableProperty]
    public partial DiskInfo? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string CurrentOperation { get; set; } =
        "Drop a Windows ISO here, or click Browse, then pick a USB drive and click Start.";

    [ObservableProperty]
    public partial string? Result { get; set; }

    public ObservableCollection<DiskInfo> Drives { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public bool CanStart =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(IsoPath) &&
        File.Exists(IsoPath) &&
        SelectedDrive is not null;

    partial void OnIsoPathChanged(string? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnSelectedDriveChanged(DiskInfo? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    /// <summary>Re-enumerate USB drives. Safe to call any time.</summary>
    [RelayCommand]
    public void RefreshDrives()
    {
        var previousNumber = SelectedDrive?.Number;
        Drives.Clear();
        try
        {
            var all = UsbDriveEnumerator.EnumerateAllDisks();
            foreach (var d in UsbDriveEnumerator.FilterTargetable(all))
            {
                Drives.Add(d);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to enumerate drives: {ex.Message}");
        }
        if (previousNumber is uint n)
        {
            SelectedDrive = Drives.FirstOrDefault(d => d.Number == n);
        }
    }

    /// <summary>
    /// Sets the candidate ISO path after validating the extension and existence.
    /// </summary>
    public void SetIso(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            IsoPath = null;
            return;
        }
        if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Ignoring '{Path.GetFileName(path)}' (not a .iso file).");
            return;
        }
        if (!File.Exists(path))
        {
            AppendLog($"File not found: {path}");
            return;
        }
        IsoPath = path;
        AppendLog($"Selected ISO: {path}");
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        if (!CanStart || IsoPath is null || SelectedDrive is null)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        Result = null;
        _cts = new CancellationTokenSource();
        var progress = new Progress<PipelineProgress>(p =>
        {
            if (p.Percent >= 0)
            {
                ProgressPercent = p.Percent;
            }
            CurrentOperation = $"[{p.Stage}] {p.Message}";
            AppendLog(CurrentOperation);
        });

        try
        {
            // UI is asInvoker; the destructive pipeline runs in a separate
            // elevated process spawned here. UAC prompts on this call.
            var outcome = await ElevatedWorkerLauncher
                .RunAsync(IsoPath, SelectedDrive, progress, _cts.Token)
                .ConfigureAwait(true);

            if (outcome.Success)
            {
                Result = outcome.Failures == 0
                    ? $"Success — {outcome.TotalSampled} sampled files verified, USB is ready to boot."
                    : $"Completed with warnings — {outcome.Failures}/{outcome.TotalSampled} sampled files mismatched.";
            }
            else
            {
                Result = $"Failed: {outcome.ErrorMessage}";
            }
            AppendLog(Result);
        }
        catch (OperationCanceledException)
        {
            Result = "Cancelled.";
            AppendLog("Cancelled by user.");
        }
        catch (Exception ex)
        {
            Result = $"Failed: {ex.Message}";
            AppendLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Public surface for the View to report a drag-drop failure without
    /// crashing the process. Logged and surfaced as the current status line.
    /// </summary>
    public void LogDropError(string message)
    {
        AppendLog(message);
        CurrentOperation = message;
    }

    /// <summary>
    /// Public surface for the View to append a line to the log without
    /// touching the status line. Used for startup diagnostics (e.g. UIPI
    /// filter result).
    /// </summary>
    public void Log(string message) => AppendLog(message);

    private void AppendLog(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        LogLines.Add(stamped);
        while (LogLines.Count > 500)
        {
            LogLines.RemoveAt(0);
        }
    }
}
