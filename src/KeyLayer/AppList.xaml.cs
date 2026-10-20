using System.Windows;
using System.Windows.Controls;

namespace KeyLayer;

/// <summary>An editable list of apps: each with a remove button, plus "Add app" (opens the app picker).</summary>
internal partial class AppList : UserControl
{
    List<AppRef> apps = [];

    public AppList() => InitializeComponent();

    /// <summary>Raised after an app was added or removed.</summary>
    public event Action? Changed;

    /// <summary>The list being edited. It's changed in place.</summary>
    public List<AppRef> Apps
    {
        get => apps;
        set
        {
            apps = value;
            Refresh();
        }
    }

    void Refresh() => Items.ItemsSource = apps.Select(a => new AppRow(a, KeyLayer.Apps.Icon(a.Path))).ToList();

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (AppPicker.Pick(Window.GetWindow(this), apps) is not { } picked || apps.Exists(a => a.Is(picked.Exe))) return;
        apps.Add(picked);
        Refresh();
        Changed?.Invoke();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRow row) return;
        apps.Remove(row.App);
        Refresh();
        Changed?.Invoke();
    }
}
