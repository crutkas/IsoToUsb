namespace IsoToUsb.ViewModels;

/// <summary>
/// Coarse classification used to colour the keyword of a log line.
/// Drives <see cref="Converters.LogSeverityToBrushConverter"/>.
/// </summary>
public enum LogSeverity
{
    /// <summary>Default-colour text; no special keyword highlight.</summary>
    Info,

    /// <summary>An active step ("validate", "mount", "wipe", "copy"…). Green.</summary>
    Action,

    /// <summary>Soft warning ("fallback", "skipped", "long path"…). Amber.</summary>
    Warn,

    /// <summary>Hard error ("failed", "exception", "abort"…). Red.</summary>
    Error,
}

/// <summary>
/// A single line in the live log, pre-parsed into three parts so the
/// XAML can render the timestamp dimmed, the keyword in a severity colour,
/// and the rest of the message in the default body colour.
/// </summary>
/// <param name="Timestamp">Bracketed timestamp prefix incl. trailing space, e.g. <c>"[12:04:01] "</c>. May be empty.</param>
/// <param name="Keyword">First word after the timestamp, no surrounding whitespace. May be empty.</param>
/// <param name="Rest">Everything after the keyword, with a leading space. May be empty.</param>
/// <param name="Severity">Colour bucket for <paramref name="Keyword"/>.</param>
public sealed record LogLine(string Timestamp, string Keyword, string Rest, LogSeverity Severity);
