using System.Windows;
using System.Windows.Controls;
using MultiDriveCloud.App.Services;
using MultiDriveCloud.Core.Storage;

namespace MultiDriveCloud.App.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow(FileProperties p)
    {
        InitializeComponent();
        NameText.Text = p.File.Name;
        var accounts = p.Fragments.Select(f => f.AccountEmail).Distinct().Count();
        var rows = new (string, string)[]
        {
            ("Emplacement", p.VirtualPath),
            ("Taille", $"{Fmt.Bytes(p.File.TotalSize)} ({p.File.TotalSize:N0} octets)"),
            ("Ajouté le", Fmt.Date(p.File.CreatedAt)),
            ("Modifié le (original)", Fmt.Date(p.File.ModifiedAt)),
            ("État", p.File.Status.ToString()),
            ("Chiffrement", p.File.IsEncrypted ? "AES-256-GCM" : "Non"),
            ("Fragments", $"{p.Fragments.Count} fragment(s) sur {accounts} compte(s)"),
            ("SHA-256", p.File.Sha256),
            ("Chemin d'origine", p.File.OriginalPath ?? "—"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            Details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Item1, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 3, 0, 3) };
            var value = new TextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            Details.Children.Add(label);
            Details.Children.Add(value);
        }
        Fragments.ItemsSource = p.Fragments;
        if (Application.Current is App app) app.Theme.ApplyTitleBar(this);
        if (Application.Current.MainWindow is { IsVisible: true } main && main != this) Owner = main;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
