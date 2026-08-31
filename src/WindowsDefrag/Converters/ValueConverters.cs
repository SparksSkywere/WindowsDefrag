using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WindowsDefrag.Helpers;
using WindowsDefrag.Models;

namespace WindowsDefrag.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long l) return ByteSizeFormatter.Format(l);
        if (value is uint u) return ByteSizeFormatter.Format(u);
        if (value is int i) return ByteSizeFormatter.Format(i);
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return $"{d:0.#}%";
        if (value is int i) return $"{i}%";
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MediaBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is MediaKind m ? m : MediaKind.Unknown;
        var color = kind switch
        {
            MediaKind.Ssd => Color.FromRgb(0x00, 0x78, 0xD4),
            MediaKind.Nvme => Color.FromRgb(0x5C, 0x2D, 0x91),
            MediaKind.Usb => Color.FromRgb(0x10, 0x7C, 0x10),
            MediaKind.Hdd => Color.FromRgb(0x60, 0x60, 0x60),
            MediaKind.Virtual => Color.FromRgb(0x87, 0x6B, 0x1D),
            _ => Color.FromRgb(0x80, 0x80, 0x80)
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class MethodNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DefragMethod m ? DefragMethodInfo.DisplayName(m) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = value is double d ? d : 0;
        if (double.IsNaN(pct) || pct < 0) pct = 0;
        return new GridLength(Math.Max(0.1, pct), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
