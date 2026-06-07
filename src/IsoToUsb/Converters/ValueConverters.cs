using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using IsoToUsb.ViewModels;

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
