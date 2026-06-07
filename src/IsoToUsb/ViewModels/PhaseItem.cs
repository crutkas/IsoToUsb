using CommunityToolkit.Mvvm.ComponentModel;

namespace IsoToUsb.ViewModels;

/// <summary>State of a single phase row in the build tracker.</summary>
public enum PhaseStatus
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
}

/// <summary>
/// Coarse-grained UI status used by the Workshop toolbar to colour the
/// status pill and the elevation badge in the bottom strip.
/// </summary>
public enum StatusKind
{
    Idle,
    Running,
    Success,
    Warning,
    Error,
}

/// <summary>
/// One row in the pipeline phase tracker shown to the user during a build.
/// Driven entirely by progress events; the View binds to <see cref="Status"/>
/// and <see cref="Detail"/> via x:Bind.
/// </summary>
public partial class PhaseItem : ObservableObject
{
    public PhaseItem(string name, string shortName, string railGlyph)
    {
        Name = name;
        ShortName = shortName;
        RailGlyph = railGlyph;
    }

    /// <summary>Long human-readable phase name (e.g. "Wipe and partition USB").</summary>
    public string Name { get; }

    /// <summary>One-word label shown under the rail node (e.g. "Wipe").</summary>
    public string ShortName { get; }

    /// <summary>
    /// Segoe Fluent glyph that represents the phase *identity* on the rail
    /// (e.g. delete icon for Wipe). Stays constant regardless of status —
    /// status is communicated via the node colours, not the icon.
    /// </summary>
    public string RailGlyph { get; }

    [ObservableProperty]
    public partial PhaseStatus Status { get; set; } = PhaseStatus.Pending;

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>Segoe Fluent glyph that represents the current status.</summary>
    public string Glyph => Status switch
    {
        PhaseStatus.Pending => "\uEA3A",  // empty circle
        PhaseStatus.Done => "\uE73E",     // checkmark
        PhaseStatus.Skipped => "\uE738",  // minus / dash
        PhaseStatus.Failed => "\uE894",   // cross
        _ => string.Empty,                 // Running uses ProgressRing instead
    };

    /// <summary>True while this phase is actively running.</summary>
    public bool IsRunning => Status == PhaseStatus.Running;

    /// <summary>True for any non-Running status that shows a glyph.</summary>
    public bool HasGlyph => Status != PhaseStatus.Running;

    partial void OnStatusChanged(PhaseStatus value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasGlyph));
    }
}
