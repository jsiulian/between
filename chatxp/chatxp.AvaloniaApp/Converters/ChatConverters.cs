using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Layout;

namespace chatxp.AvaloniaApp.Converters;

/// <summary>Message bubble: theme-aware colors per direction (NFR-5). Brushes come
/// from the theme dictionaries — mine green, theirs neutral — resolved against the
/// current app theme, so both light and dark modes are readable.</summary>
public sealed class MineToBackgroundConverter : IValueConverter
{
    public static readonly MineToBackgroundConverter Instance = new();
    public static readonly MineToBackgroundConverter Reverse = new() { _reverse = true };
    private bool _reverse;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mine = value is true;
        if (_reverse) mine = !mine;
        var key = mine ? "BubbleMineBrush" : "BubbleTheirsBrush";
        if (App.Current?.TryGetResource(key, null, out var brush) == true && brush is IBrush b)
            return b;
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bubble text color: theme-aware per direction (contrasts with the
/// bubble background brushes).</summary>
public sealed class MineToForegroundConverter : IValueConverter
{
    public static readonly MineToForegroundConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "BubbleMineText" : "BubbleTheirsText";
        if (App.Current?.TryGetResource(key, null, out var brush) == true && brush is IBrush b)
            return b;
        return Brushes.Black;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MineToAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Footer status dot: green when connected, red when offline.</summary>
public sealed class BoolToBrushConverter : IMultiValueConverter
{
    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x4c, 0xaf, 0x50));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xf4, 0x43, 0x36));

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.OfType<bool>().FirstOrDefault() ? (object)Green : Red;
}

/// <summary>Collection emptiness: parameter "empty" → true when 0 items; "notempty" → true when ≥1.</summary>
public sealed class CountIsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : (value as System.Collections.ICollection)?.Count ?? 0;
        var wantEmpty = string.Equals(parameter?.ToString(), "empty", StringComparison.OrdinalIgnoreCase);
        return wantEmpty ? count == 0 : count > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
