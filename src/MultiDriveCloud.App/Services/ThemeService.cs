using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MultiDriveCloud.App.Services;

public sealed class ThemeService
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public string Current { get; private set; } = "Dark";

    public void Apply(string theme)
    {
        Current = theme == "Light" ? "Light" : "Dark";
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/MultiDriveCloud;component/Resources/Theme.{Current}.xaml", UriKind.Absolute) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    public void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyTitleBar(window);
            return;
        }
        var useDark = Current == "Dark" ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
    }
}
