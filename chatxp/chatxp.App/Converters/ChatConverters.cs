using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Layout;

namespace chatxp.App.Converters;

/// <summary>Message bubble styling: my messages green+right, peer's dark+left.</summary>
public sealed class MineToBackgroundConverter : IValueConverter
{
    private static readonly IBrush Mine = new SolidColorBrush(Color.FromRgb(0x1e, 0x5c, 0x46));
    private static readonly IBrush Theirs = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x33));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Mine : Theirs;

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
