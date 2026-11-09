using System.Windows;
using System.Windows.Input;
using MultiDriveCloud.App.Services;

namespace MultiDriveCloud.App.Views;

public partial class DialogWindow : Window
{
    public string? InputText { get; private set; }

    public DialogWindow(string title, string message, string okText, bool showCancel, bool danger, string? inputLabel, string? inputValue)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (danger) OkButton.Style = (Style)FindResource("DangerButton");
        if (inputLabel is not null)
        {
            LabelText.Text = inputLabel;
            LabelText.Visibility = Visibility.Visible;
            InputBox.Visibility = Visibility.Visible;
            InputBox.Text = inputValue ?? "";
            Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
        }
        if (Application.Current is App app) app.Theme.ApplyTitleBar(this);
        if (Application.Current.MainWindow is { IsVisible: true } main && main != this) Owner = main;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        InputText = InputBox.Visibility == Visibility.Visible ? InputBox.Text : null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }
}
