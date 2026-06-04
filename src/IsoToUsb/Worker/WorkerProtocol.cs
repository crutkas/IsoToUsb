namespace IsoToUsb.Worker;

/// <summary>
/// Line-based, tab-delimited protocol used between the non-elevated UI
/// process and the elevated pipeline worker process over a duplex named pipe.
/// </summary>
/// <remarks>
/// Worker → Parent messages (one per line):
/// <list type="bullet">
///   <item><c>P\t&lt;stageName&gt;\t&lt;percent&gt;\t&lt;message&gt;</c> — progress report</item>
///   <item><c>L\t&lt;message&gt;</c> — log-only line (no progress change)</item>
///   <item><c>R\t&lt;totalSampled&gt;\t&lt;failures&gt;</c> — pipeline completed successfully</item>
///   <item><c>E\t&lt;exceptionType&gt;\t&lt;message&gt;</c> — pipeline threw</item>
/// </list>
/// Parent → Worker:
/// <list type="bullet">
///   <item><c>CANCEL</c> — request cancellation</item>
/// </list>
/// Tabs, CR, and LF are stripped from message payloads to keep the protocol
/// trivially parseable.
/// </remarks>
internal static class WorkerProtocol
{
    public const string Progress = "P";
    public const string Log = "L";
    public const string Result = "R";
    public const string Error = "E";
    public const string CancelCommand = "CANCEL";

    /// <summary>Strips characters that would break line/field parsing.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }
}

/// <summary>Stable string names for <see cref="Services.PipelineStage"/> sent over the wire.</summary>
internal static class StageNames
{
    public static string From(Services.PipelineStage stage) => stage switch
    {
        Services.PipelineStage.ValidateInputs => "ValidateInputs",
        Services.PipelineStage.MountIso => "MountIso",
        Services.PipelineStage.Repartition => "Repartition",
        Services.PipelineStage.CopyFiles => "CopyFiles",
        Services.PipelineStage.SplitInstallWim => "SplitInstallWim",
        Services.PipelineStage.Verify => "Verify",
        Services.PipelineStage.Eject => "Eject",
        Services.PipelineStage.Done => "Done",
        _ => stage.ToString(),
    };

    public static Services.PipelineStage Parse(string name) => name switch
    {
        "ValidateInputs" => Services.PipelineStage.ValidateInputs,
        "MountIso" => Services.PipelineStage.MountIso,
        "Repartition" => Services.PipelineStage.Repartition,
        "CopyFiles" => Services.PipelineStage.CopyFiles,
        "SplitInstallWim" => Services.PipelineStage.SplitInstallWim,
        "Verify" => Services.PipelineStage.Verify,
        "Eject" => Services.PipelineStage.Eject,
        "Done" => Services.PipelineStage.Done,
        _ => Services.PipelineStage.ValidateInputs,
    };
}
