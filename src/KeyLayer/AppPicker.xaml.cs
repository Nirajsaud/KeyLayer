using System.Windows;
using Microsoft.Win32;

namespace KeyLayer;

/// <summary>Picks an app: one with a window open, or any program file.</summary>
internal partial class AppPicker : Window
{
    AppRef? result;

    AppPicker(IEnumerable<AppRef> exclude)
    {
        InitializeComponent();
        var skip = exclude.ToList();
        var rows = Apps.Running()
            .Where(r => !skip.Exists(a => a.Is(r.App.Exe)))
            .Select(r => new AppRow(r.App, r.Icon))
            .ToList();
        AppItems.ItemsSource = rows;
        NoneText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the picker. Returns the chosen app, or null if cancelled.</summary>
    public static AppRef? Pick(Window? owner, IEnumerable<AppRef> exclude)
    {
        var picker = new AppPicker(exclude) { Owner = owner };
        return picker.ShowDialog() == true ? picker.result : null;
    }

    void App_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRow row) return;
        result = row.App;
        DialogResult = true;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the app's program file",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;
        result = Apps.FromPath(dialog.FileName);
        DialogResult = true;
    }
}
