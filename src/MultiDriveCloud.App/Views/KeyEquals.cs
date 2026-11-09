using System.Globalization;
using System.Windows.Data;

namespace MultiDriveCloud.App.Views;

public sealed class KeyEquals : IValueConverter
{
    public static readonly KeyEquals Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
