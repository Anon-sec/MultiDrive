using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MultiDriveCloud.Core.Storage;

namespace MultiDriveCloud.App.Services;

public static class Fmt
{
    public static string Bytes(long bytes) => Formatting.Bytes(bytes);

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond < 1 ? "—" : Formatting.Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is null) return "—";
        var s = span.Value;
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays} j {s.Hours} h";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours} h {s.Minutes:00} min";
        if (s.TotalMinutes >= 1) return $"{(int)s.TotalMinutes} min {s.Seconds:00} s";
        return $"{Math.Max(1, (int)s.TotalSeconds)} s";
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? Fmt.Bytes(l) : value is int i ? Fmt.Bytes(i) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Fmt.Speed(d) : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{Math.Round(d * 100):0} %" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime d ? Fmt.Date(d) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value is not null && !(value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FractionWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double fraction || values[1] is not double total || double.IsNaN(total)) return 0d;
        return Math.Max(0, Math.Min(1, fraction)) * total;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
