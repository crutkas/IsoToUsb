using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IsoToUsb.Services;
using Microsoft.UI.Xaml.Controls;

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

    /// <summary>
    /// Headline shown on the completion InfoBar (e.g. "Done!", "Failed",
    /// "Cancelled"). Null while the banner is hidden.
    /// </summary>
    [ObservableProperty]
    public partial string? ResultTitle { get; set; }

    /// <summary>
    /// Severity for the completion InfoBar — picks the icon and accent
    /// color (green check / red cross / amber warning).
    /// </summary>
    [ObservableProperty]
    public partial InfoBarSeverity ResultSeverity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>True whenever the completion banner should be visible.</summary>
    public bool IsResultVisible => !string.IsNullOrEmpty(Result);

    /// <summary>
    /// Single status the Workshop toolbar pill renders from: Idle when no
    /// build has started, Running while the pipeline is active, and one of
    /// Success / Warning / Error after a build settles.
    /// </summary>
    public StatusKind Status =>
        IsResultVisible
            ? ResultSeverity switch
            {
                InfoBarSeverity.Success => StatusKind.Success,
                InfoBarSeverity.Warning => StatusKind.Warning,
                InfoBarSeverity.Error => StatusKind.Error,
                _ => StatusKind.Idle,
            }
            : IsBusy ? StatusKind.Running : StatusKind.Idle;

    /// <summary>Short text shown inside the toolbar status pill.</summary>
    public string StatusPillText
    {
        get
        {
            if (IsResultVisible)
            {
                return ResultTitle ?? "Done";
            }
            if (!IsBusy)
            {
                return "Idle";
            }
            var stage = ExtractStage(CurrentOperation);
            return ProgressPercent > 0 ? $"{stage} · {ProgressPercent}%" : stage;
        }
    }

    private static string ExtractStage(string op)
    {
        // CurrentOperation is formatted "[Stage] Message" by the progress
        // callback; pull the stage name back out and humanise it.
        if (string.IsNullOrEmpty(op) || !op.StartsWith('['))
        {
            return "Building";
        }
        var end = op.IndexOf(']');
        if (end <= 1)
        {
            return "Building";
        }
        var stage = op[1..end];
        return stage switch
        {
            "ValidateInputs" => "Validating",
            "MountIso" => "Mounting ISO",
            "Repartition" => "Partitioning",
            "CopyFiles" => "Copying",
            "SplitInstallWim" => "Splitting WIM",
            "Verify" => "Verifying",
            "Eject" => "Ejecting",
            "Done" => "Done",
            _ => stage,
        };
    }

    partial void OnResultChanged(string? value)
    {
        OnPropertyChanged(nameof(IsResultVisible));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusPillText));
    }

    partial void OnResultTitleChanged(string? value) => OnPropertyChanged(nameof(StatusPillText));

    partial void OnResultSeverityChanged(InfoBarSeverity value) => OnPropertyChanged(nameof(Status));

    partial void OnCurrentOperationChanged(string value) => OnPropertyChanged(nameof(StatusPillText));

    partial void OnProgressPercentChanged(int value) => OnPropertyChanged(nameof(StatusPillText));

    public ObservableCollection<DiskInfo> Drives { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>
    /// Ordered list of pipeline phases shown to the user. Stays
    /// constant for the lifetime of the ViewModel — each row's
    /// <see cref="PhaseItem.Status"/> mutates as the build proceeds.
    /// </summary>
    public ObservableCollection<PhaseItem> Phases { get; } = new(
        new[]
        {
            new PhaseItem("Validate ISO and target"),
            new PhaseItem("Mount install image"),
            new PhaseItem("Wipe and partition USB"),
            new PhaseItem("Copy files to USB"),
            new PhaseItem("Split large WIM files"),
            new PhaseItem("Verify random sample"),
        });

    // Index alongside Phases for O(1) lookup from PipelineStage.
    private PhaseItem PhaseValidate => Phases[0];
    private PhaseItem PhaseMount => Phases[1];
    private PhaseItem PhasePartition => Phases[2];
    private PhaseItem PhaseCopy => Phases[3];
    private PhaseItem PhaseSplit => Phases[4];
    private PhaseItem PhaseVerify => Phases[5];

    public bool CanStart =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(IsoPath) &&
        File.Exists(IsoPath) &&
        SelectedDrive is not null;

    partial void OnIsoPathChanged(string? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnSelectedDriveChanged(DiskInfo? value) => OnPropertyChanged(nameof(CanStart));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusPillText));
    }

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

        // Re-select the previously chosen disk if it's still present;
        // otherwise fall back to the first available USB drive so the
        // user doesn't have to manually pick after every hot-plug or
        // fresh launch.
        DiskInfo? next = null;
        if (previousNumber is uint n)
        {
            next = Drives.FirstOrDefault(d => d.Number == n);
        }
        next ??= Drives.FirstOrDefault();
        SelectedDrive = next;
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
        ResultTitle = null;
        ResultSeverity = InfoBarSeverity.Informational;
        ResetPhases();
        _cts = new CancellationTokenSource();
        var progress = new Progress<PipelineProgress>(p =>
        {
            if (p.Percent >= 0)
            {
                ProgressPercent = p.Percent;
            }
            CurrentOperation = $"[{p.Stage}] {p.Message}";
            AppendLog(CurrentOperation);
            UpdatePhase(p);
        });

        try
        {
            // UI is asInvoker; the destructive pipeline runs in a separate
            // elevated process spawned here. UAC prompts on this call.
            var outcome = await ElevatedWorkerLauncher
                .RunAsync(IsoPath, SelectedDrive, progress, _cts.Token)
                .ConfigureAwait(true);

            if (outcome.Canceled)
            {
                ResultTitle = "Cancelled";
                Result = "The build was cancelled before it finished. The USB drive may be in an inconsistent state.";
                ResultSeverity = InfoBarSeverity.Warning;
                MarkRemainingPhases(PhaseStatus.Skipped);
            }
            else if (outcome.Success)
            {
                ProgressPercent = 100;
                ResultTitle = "Done — your USB is ready to boot";
                Result = outcome.Failures == 0
                    ? $"All {outcome.TotalSampled} sampled files verified. You can safely remove the USB drive and use it to install Windows."
                    : $"Completed with warnings — {outcome.Failures}/{outcome.TotalSampled} sampled files mismatched. The USB should still boot, but a re-flash is recommended.";
                ResultSeverity = outcome.Failures == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning;
                FinalizePhases();
            }
            else
            {
                ResultTitle = "Failed";
                Result = outcome.ErrorMessage ?? "The build failed for an unknown reason. See the log for details.";
                ResultSeverity = InfoBarSeverity.Error;
                MarkRunningPhaseFailed();
                MarkRemainingPhases(PhaseStatus.Skipped);
            }
            AppendLog($"{ResultTitle}: {Result}");
        }
        catch (OperationCanceledException)
        {
            ResultTitle = "Cancelled";
            Result = "Cancelled by user.";
            ResultSeverity = InfoBarSeverity.Warning;
            MarkRemainingPhases(PhaseStatus.Skipped);
            AppendLog("Cancelled by user.");
        }
        catch (Exception ex)
        {
            ResultTitle = "Failed";
            Result = ex.Message;
            ResultSeverity = InfoBarSeverity.Error;
            MarkRunningPhaseFailed();
            MarkRemainingPhases(PhaseStatus.Skipped);
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

    /// <summary>Resets every phase row to Pending with no detail text.</summary>
    private void ResetPhases()
    {
        foreach (var phase in Phases)
        {
            phase.Status = PhaseStatus.Pending;
            phase.Detail = string.Empty;
        }
    }

    /// <summary>
    /// Routes a single pipeline progress event into the phase tracker:
    /// marks the matching row Running, copies the message into Detail,
    /// and promotes any earlier rows that are still Pending to Done so
    /// the user sees a clean cascade (we don't fire a "done" event for
    /// every stage individually).
    /// </summary>
    private void UpdatePhase(PipelineProgress p)
    {
        var target = PhaseForStage(p.Stage);
        if (target is null)
        {
            return;
        }
        var index = Phases.IndexOf(target);
        for (int i = 0; i < index; i++)
        {
            if (Phases[i].Status is PhaseStatus.Pending or PhaseStatus.Running)
            {
                Phases[i].Status = PhaseStatus.Done;
            }
        }
        if (target.Status != PhaseStatus.Done)
        {
            target.Status = p.Percent >= 100 ? PhaseStatus.Done : PhaseStatus.Running;
        }
        target.Detail = p.Message;
    }

    private PhaseItem? PhaseForStage(PipelineStage stage) => stage switch
    {
        PipelineStage.ValidateInputs => PhaseValidate,
        PipelineStage.MountIso => PhaseMount,
        PipelineStage.Repartition => PhasePartition,
        PipelineStage.CopyFiles => PhaseCopy,
        PipelineStage.SplitInstallWim => PhaseSplit,
        PipelineStage.Verify => PhaseVerify,
        _ => null, // Eject + Done are tracked via InfoBar, not phase rows
    };

    /// <summary>
    /// Called on success: any phase still Pending (e.g. SplitInstallWim
    /// when the ISO has no oversized WIMs) becomes Skipped; everything
    /// else becomes Done. Visually distinguishes "didn't need to run"
    /// from "ran and succeeded".
    /// </summary>
    private void FinalizePhases()
    {
        foreach (var phase in Phases)
        {
            if (phase.Status == PhaseStatus.Pending)
            {
                phase.Status = PhaseStatus.Skipped;
            }
            else if (phase.Status == PhaseStatus.Running)
            {
                phase.Status = PhaseStatus.Done;
            }
        }
    }

    /// <summary>Marks the currently-Running phase (if any) as Failed.</summary>
    private void MarkRunningPhaseFailed()
    {
        foreach (var phase in Phases)
        {
            if (phase.Status == PhaseStatus.Running)
            {
                phase.Status = PhaseStatus.Failed;
                return;
            }
        }
    }

    /// <summary>Marks every still-Pending phase with the given status (Skipped on cancel/fail).</summary>
    private void MarkRemainingPhases(PhaseStatus status)
    {
        foreach (var phase in Phases)
        {
            if (phase.Status == PhaseStatus.Pending)
            {
                phase.Status = status;
            }
        }
    }
}
