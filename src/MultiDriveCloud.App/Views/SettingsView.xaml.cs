using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MultiDriveCloud.App.ViewModels;

namespace MultiDriveCloud.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ClientSecret = SecretBox.Password;
    }
}

public sealed class OkToKind : IValueConverter
{
    public static readonly OkToKind Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "Ok" : "Warn";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
