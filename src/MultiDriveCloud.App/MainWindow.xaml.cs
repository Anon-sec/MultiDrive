using System.ComponentModel;
using System.Windows;

namespace MultiDriveCloud.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        _ = ((App)Application.Current).RequestExitAsync(force: false);
    }
}
