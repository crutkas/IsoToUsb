using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using IsoToUsb.ViewModels;
using System.Numerics;

namespace IsoToUsb.Converters;

/// <summary>Inverts a boolean for binding.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : false;
}

/// <summary>Maps <c>true</c> -> <see cref="Visibility.Visible"/>, <c>false</c> -> <see cref="Visibility.Collapsed"/>.
/// Required because <c>x:Bind</c> does not auto-coerce <see cref="bool"/> to <see cref="Visibility"/>.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Maps <c>true</c> -> <see cref="Visibility.Collapsed"/>, <c>false</c> -> <see cref="Visibility.Visible"/>.</summary>
public sealed class InvertedBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Collapsed;
}

/// <summary>Maps <c>null</c>/empty strings to <see cref="Visibility.Collapsed"/>, otherwise <see cref="Visibility.Visible"/>.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s && string.IsNullOrWhiteSpace(s)) return Visibility.Collapsed;
        return Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="PhaseStatus"/> to an appropriate foreground brush:
/// done = system accent green, failed = red, skipped = muted, otherwise the
/// default text color. Used by the phase-tracker FontIcon glyph.
/// </summary>
public sealed class PhaseStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not PhaseStatus s)
        {
            return new SolidColorBrush(Colors.Gray);
        }
        return s switch
        {
            PhaseStatus.Done => new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x80, 0x3C)),    // green
            PhaseStatus.Failed => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),  // red
            PhaseStatus.Skipped => new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88)), // gray
            _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x60)),                   // pending = subtle
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="PhaseStatus"/> to a bold strong-text foreground for
/// the phase title (Running renders in accent so the currently active row
/// reads as the focal point).
/// </summary>
public sealed class PhaseStatusToTitleBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not PhaseStatus s)
        {
            return Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
        return s switch
        {
            PhaseStatus.Done => new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x80, 0x3C)),
            PhaseStatus.Running => Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            PhaseStatus.Failed => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
            PhaseStatus.Skipped => Application.Current.Resources["TextFillColorTertiaryBrush"],
            _ => Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Multi-role brush mapping for the Workshop status pill. The converter
/// parameter selects which slot (<c>background</c>, <c>border</c>, or
/// <c>foreground</c>) to return for a given <see cref="StatusKind"/> —
/// keeping all of the pill's chromatic logic in one place.
/// </summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var kind = value is StatusKind k ? k : StatusKind.Idle;
        var role = (parameter as string ?? "foreground").ToLowerInvariant();
        return role switch
        {
            "background" => Background(kind),
            "border" => Border(kind),
            _ => Foreground(kind),
        };
    }

    private static SolidColorBrush Background(StatusKind k) => k switch
    {
        StatusKind.Running => new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD4)),
        StatusKind.Success => new SolidColorBrush(Color.FromArgb(0x22, 0x10, 0x80, 0x3C)),
        StatusKind.Warning => new SolidColorBrush(Color.FromArgb(0x22, 0xC4, 0x6E, 0x00)),
        StatusKind.Error => new SolidColorBrush(Color.FromArgb(0x22, 0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Colors.Transparent),
    };

    private static SolidColorBrush Border(StatusKind k) => k switch
    {
        StatusKind.Running => new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x78, 0xD4)),
        StatusKind.Success => new SolidColorBrush(Color.FromArgb(0xA0, 0x10, 0x80, 0x3C)),
        StatusKind.Warning => new SolidColorBrush(Color.FromArgb(0xA0, 0xC4, 0x6E, 0x00)),
        StatusKind.Error => new SolidColorBrush(Color.FromArgb(0xA0, 0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88)),
    };

    private static SolidColorBrush Foreground(StatusKind k) => k switch
    {
        StatusKind.Running => new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x67, 0xC0)),
        StatusKind.Success => new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x80, 0x3C)),
        StatusKind.Warning => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x6E, 0x00)),
        StatusKind.Error => new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x60)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="LogSeverity"/> to the brush used to colour the keyword
/// of a log line. Looks up <c>LogActionBrush</c> / <c>LogWarnBrush</c> /
/// <c>LogErrorBrush</c> from <see cref="Application.Current"/> resources so
/// the colours stay theme-aware. Falls back to a hardcoded value if a
/// resource is missing (e.g. in the XAML designer).
/// </summary>
public sealed class LogSeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var sev = value is LogSeverity s ? s : LogSeverity.Info;
        var resources = Application.Current?.Resources;
        return sev switch
        {
            LogSeverity.Action => Resolve(resources, "LogActionBrush", Color.FromArgb(0xFF, 0x3F, 0xA3, 0x4D)),
            LogSeverity.Warn => Resolve(resources, "LogWarnBrush", Color.FromArgb(0xFF, 0xC2, 0x92, 0x00)),
            LogSeverity.Error => Resolve(resources, "LogErrorBrush", Color.FromArgb(0xFF, 0xE8, 0x11, 0x23)),
            _ => Resolve(resources, "TextFillColorPrimaryBrush", Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0)),
        };
    }

    private static Brush Resolve(Microsoft.UI.Xaml.ResourceDictionary? resources, string key, Color fallback)
    {
        if (resources is not null && resources.TryGetValue(key, out var v) && v is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Bool-to-opacity mapping used to dim the inactive card in the Twin-cards
/// elevation shift. <c>true</c> (focal) returns full opacity, <c>false</c>
/// returns a softly washed-out value that keeps text legible.
/// </summary>
public sealed class BoolToFocalOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? 1.0 : 0.62;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps focal state to a Vector3 Translation used with ThemeShadow to lift
/// the focal card off the page. Mirrors the design board's
/// <c>--shadow-focus: 0 22px 46px rgba(0,0,0,0.18)</c> by translating Z=32
/// so WinUI's composition shadow draws a soft drop shadow underneath. The
/// small -3 on Y matches the design's <c>--y: -5px</c> for "lifted".
/// </summary>
public sealed class BoolToFocalTranslationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? new Vector3(0, -3, 32) : Vector3.Zero;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Border brush picker for the focal card: focal cards get a tinted accent
/// stroke, inactive cards get the neutral card stroke. Drives the
/// "this card is in front" reading.
/// </summary>
public sealed class BoolToFocalBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var resources = Application.Current?.Resources;
        if (value is bool focal && focal)
        {
            // Soft accent tint (mirrors design's color-mix(accent 34%, border)).
            // SystemAccentColor at low alpha reads as "slightly accent-tinted
            // border" rather than a hard accent stroke.
            return new SolidColorBrush(GetAccent(resources)) { Opacity = 0.55 };
        }

        if (resources is not null && resources.TryGetValue("CardStrokeColorDefaultBrush", out var c) && c is Brush cb)
        {
            return cb;
        }
        return new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    }

    private static Color GetAccent(Microsoft.UI.Xaml.ResourceDictionary? resources)
    {
        if (resources is not null && resources.TryGetValue("SystemAccentColor", out var v) && v is Color c)
        {
            return c;
        }
        return Color.FromArgb(0xFF, 0x00, 0x67, 0xC0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Background brush picker for the focal card. Focal: standard card fill.
/// Inactive: a slightly washed-out secondary fill so the dimming reads
/// without making the card look broken.
/// </summary>
public sealed class BoolToFocalBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var resources = Application.Current?.Resources;
        var key = (value is bool focal && focal) ? "CardBackgroundFillColorDefaultBrush" : "CardBackgroundFillColorSecondaryBrush";
        if (resources is not null && resources.TryGetValue(key, out var v) && v is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Multi-role brush picker for the phase rail node inside the Work card.
/// The converter parameter selects which slot to colour (<c>node-background</c>,
/// <c>node-border</c>, <c>node-foreground</c>, or <c>label-foreground</c>),
/// and the value (<see cref="PhaseStatus"/>) selects the state.
/// </summary>
public sealed class PhaseStatusToRailBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var s = value is PhaseStatus ps ? ps : PhaseStatus.Pending;
        var role = (parameter as string ?? "node-foreground").ToLowerInvariant();
        return role switch
        {
            "node-background" => NodeBackground(s),
            "node-border" => NodeBorder(s),
            "label-foreground" => LabelForeground(s),
            _ => NodeForeground(s),
        };
    }

    private static SolidColorBrush NodeBackground(PhaseStatus s) => s switch
    {
        PhaseStatus.Running => new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD4)),
        PhaseStatus.Done => new SolidColorBrush(Color.FromArgb(0x1A, 0x10, 0x80, 0x3C)),
        PhaseStatus.Failed => new SolidColorBrush(Color.FromArgb(0x1F, 0xC4, 0x2B, 0x1C)),
        _ => new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
    };

    private static SolidColorBrush NodeBorder(PhaseStatus s) => s switch
    {
        PhaseStatus.Running => new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x78, 0xD4)),
        PhaseStatus.Done => new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x80, 0x3C)),
        PhaseStatus.Failed => new SolidColorBrush(Color.FromArgb(0xA0, 0xC4, 0x2B, 0x1C)),
        PhaseStatus.Skipped => new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
        _ => new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80)),
    };

    private static SolidColorBrush NodeForeground(PhaseStatus s) => s switch
    {
        PhaseStatus.Running => new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)),
        PhaseStatus.Done => new SolidColorBrush(Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F)),
        PhaseStatus.Failed => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x99, 0x9A)),
        PhaseStatus.Skipped => new SolidColorBrush(Color.FromArgb(0x80, 0xBB, 0xBB, 0xBB)),
        _ => new SolidColorBrush(Color.FromArgb(0xCC, 0xBB, 0xBB, 0xBB)),
    };

    private static SolidColorBrush LabelForeground(PhaseStatus s) => s switch
    {
        PhaseStatus.Running => new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF)),
        PhaseStatus.Done => new SolidColorBrush(Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F)),
        PhaseStatus.Failed => new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x99, 0x9A)),
        PhaseStatus.Skipped => new SolidColorBrush(Color.FromArgb(0x80, 0xBB, 0xBB, 0xBB)),
        _ => new SolidColorBrush(Color.FromArgb(0xB0, 0xBB, 0xBB, 0xBB)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
