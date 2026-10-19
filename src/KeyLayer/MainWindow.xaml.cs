using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KeyLayer;

internal partial class MainWindow : Window
{
    const string GitHubUrl = "https://github.com/Nirajsaud/KeyLayer";

    readonly App app;
    readonly ObservableCollection<ShortcutItem> shortcuts = [];
    readonly ObservableCollection<KeyboardItem> keyboards = [];
    readonly ObservableCollection<EventItem> events = [];
    readonly Dictionary<string, DateTime> lastUsed = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    string? filter; // keyboard ID, or null for all shortcuts
    bool loading;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();

        ShortcutList.ItemsSource = shortcuts;
        KeyboardList.ItemsSource = keyboards;
        EventList.ItemsSource = events;
        events.CollectionChanged += (_, _) => TestEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        app.Engine.Observed += OnObserved;
        app.Engine.KeyboardsChanged += OnKeyboardsChanged;
        app.Engine.KeyboardActivity += OnKeyboardActivity;
        app.Engine.Fired += OnFired;
        app.PausedChanged += UpdatePaused;
        activityTimer.Tick += (_, _) => FadeActivity();
        IsVisibleChanged += (_, _) => UpdateMonitor();

        LoadSettings();
        RefreshKeyboards();
        RefreshShortcuts();
        UpdatePaused();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!app.Quitting)
        {
            // Closing the window keeps KeyLayer running in the tray.
            e.Cancel = true;
            Hide();
            if (!app.Config.TrayTipShown)
            {
                app.Notify("KeyLayer is still running",
                    "Your shortcuts keep working in the background. Right-click the tray icon to quit.",
                    System.Windows.Forms.ToolTipIcon.Info);
                app.Config.TrayTipShown = true;
                app.SaveConfig();
            }
            return;
        }

        app.Engine.Observed -= OnObserved;
        app.Engine.KeyboardsChanged -= OnKeyboardsChanged;
        app.Engine.KeyboardActivity -= OnKeyboardActivity;
        app.Engine.Fired -= OnFired;
        app.PausedChanged -= UpdatePaused;
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ navigation

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShortcutsPage is null || Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        ShortcutsPage.Visibility = Show(page == "Shortcuts");
        KeyboardsPage.Visibility = Show(page == "Keyboards");
        TestPage.Visibility = Show(page == "Test");
        SettingsPage.Visibility = Show(page == "Settings");
        UpdateMonitor();
    }

    static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    void ShowPage(string page) =>
        Nav.SelectedItem = Nav.Items.Cast<ListBoxItem>().First(i => (string)i.Tag == page);

    void GoTest_Click(object sender, RoutedEventArgs e) => ShowPage("Test");

    void UpdateMonitor() => app.Engine.MonitorEnabled = IsVisible && TestPage.Visibility == Visibility.Visible;

    // ------------------------------------------------------------------ shortcuts

    string KeyboardText(Trigger t) =>
        t.Keyboard is { } id ? app.Engine.NameOf(id, t.KeyboardName ?? t.Device)
        : t.Kind == TriggerKind.Hid ? t.Device ?? "Unknown device"
        : "Any keyboard";

    /// <summary>A keyboard's filter shows its own shortcuts plus the ones that work on every keyboard.</summary>
    bool MatchesFilter(Mapping m) =>
        filter is null
        || (m.Trigger.Kind == TriggerKind.Key && m.Trigger.Keyboard is null)
        || string.Equals(m.Trigger.Keyboard, filter, StringComparison.OrdinalIgnoreCase);

    void RefreshShortcuts()
    {
        BuildFilters();
        shortcuts.Clear();
        foreach (var m in app.Config.Mappings.Where(MatchesFilter))
            shortcuts.Add(new ShortcutItem(app, m, KeyboardText(m.Trigger)));

        bool none = app.Config.Mappings.Count == 0;
        ShortcutsEmpty.Visibility = Show(none);
        FilterPanel.Visibility = Show(!none && FilterPanel.Children.Count > 2);
        FilterEmpty.Visibility = Show(!none && shortcuts.Count == 0);
    }

    void BuildFilters()
    {
        loading = true;
        FilterPanel.Children.Clear();
        var options = new List<FilterItem> { new(null, "All shortcuts") };
        options.AddRange(app.Engine.Keyboards
            .Where(k => !k.IsReceiver || app.Config.Mappings.Any(m => string.Equals(m.Trigger.Keyboard, k.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(k => new FilterItem(k.Id, app.Engine.NameOf(k))));
        if (filter is not null && !options.Any(o => o.Id == filter)) filter = null;

        foreach (var option in options)
        {
            var pill = new RadioButton
            {
                Style = (Style)FindResource("Pill"),
                Content = option.Label,
                GroupName = "KeyboardFilter",
                IsChecked = option.Id == filter,
            };
            pill.Checked += (_, _) =>
            {
                if (loading) return;
                filter = option.Id;
                Dispatcher.BeginInvoke(new Action(RefreshShortcuts)); // not while the pill is still handling its click
            };
            FilterPanel.Children.Add(pill);
        }
        loading = false;
    }

    void AddShortcut_Click(object sender, RoutedEventArgs e) => AddShortcut(null);

    void AddShortcut(Trigger? preset)
    {
        var editor = new ShortcutEditor(app, null, [.. app.Config.Mappings], preset) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } mapping) return;
        Store(null, mapping);
        ShowPage("Shortcuts");
    }

    void EditShortcut(Mapping current)
    {
        var editor = new ShortcutEditor(app, current, [.. app.Config.Mappings.Where(m => m != current)], null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } updated) return;
        Store(current, updated);
    }

    /// <summary>Adds or replaces a shortcut. Another shortcut on the same key is replaced (the editor warned about it).</summary>
    void Store(Mapping? current, Mapping updated)
    {
        var mappings = app.Config.Mappings;
        int index = current is null ? -1 : mappings.IndexOf(current);
        if (index >= 0) mappings[index] = updated;
        else mappings.Add(updated);
        mappings.RemoveAll(m => m != updated && m.Trigger.MatchKey == updated.Trigger.MatchKey);
        app.SaveConfig();
        RefreshShortcuts();
        RefreshKeyboards();
    }

    void DeleteShortcut(Mapping m)
    {
        if (MessageBox.Show(this, $"Delete the shortcut “{m.Action.Describe()}”?", "KeyLayer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        app.Config.Mappings.Remove(m);
        app.SaveConfig();
        RefreshShortcuts();
        RefreshKeyboards();
    }

    void ShortcutCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the switch or the "..." button belong to them, not to the card.
        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender)) return;
        if (((FrameworkElement)sender).DataContext is ShortcutItem item) EditShortcut(item.Mapping);
    }

    static bool IsInsideButton(DependencyObject? d, DependencyObject stop)
    {
        while (d is not null && d != stop)
        {
            if (d is ButtonBase) return true;
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void ShortcutMenu_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not ShortcutItem item) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuEntry("Edit", "", () => EditShortcut(item.Mapping)));
        menu.Items.Add(MenuEntry("Try it now", "", () => app.Engine.RunNow(item.Mapping)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Delete", "", () => DeleteShortcut(item.Mapping)));
        menu.IsOpen = true;
    }

    MenuItem MenuEntry(string header, string glyph, Action action)
    {
        var entry = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 14 },
        };
        entry.Click += (_, _) => action();
        return entry;
    }

    // ------------------------------------------------------------------ keyboards

    void OnKeyboardsChanged()
    {
        RefreshKeyboards();
        RefreshShortcuts();
    }

    void RefreshKeyboards()
    {
        keyboards.Clear();
        foreach (var k in app.Engine.Keyboards)
        {
            int own = app.Config.Mappings.Count(m => string.Equals(m.Trigger.Keyboard, k.Id, StringComparison.OrdinalIgnoreCase));
            var parts = new List<string> { k.Connection };
            if (k.External) parts.Add(k.Id);
            parts.Add(own switch { 0 => "no shortcuts of its own", 1 => "1 shortcut of its own", _ => $"{own} shortcuts of its own" });
            keyboards.Add(new KeyboardItem(k)
            {
                Name = app.Engine.NameOf(k),
                Details = string.Join("  ·  ", parts),
                LastUsed = lastUsed.TryGetValue(k.Id, out var time) ? $"Last key {time:HH:mm:ss}" : "",
                Active = lastUsed.TryGetValue(k.Id, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(1.5),
            });
        }
    }

    void OnKeyboardActivity(string id)
    {
        var now = DateTime.Now;
        lastUsed[id] = now;
        foreach (var k in keyboards.Where(k => string.Equals(k.Info.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            k.Active = true;
            k.LastUsed = $"Last key {now:HH:mm:ss}";
        }
        activityTimer.Start();
    }

    void FadeActivity()
    {
        var now = DateTime.Now;
        bool any = false;
        foreach (var k in keyboards)
        {
            if (k.Active && (!lastUsed.TryGetValue(k.Info.Id, out var t) || now - t > TimeSpan.FromSeconds(1.5)))
                k.Active = false;
            any |= k.Active;
        }
        if (!any) activityTimer.Stop();
    }

    void RenameKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not KeyboardItem item) return;
        var k = item.Info;
        string own = !k.External ? "Built-in keyboard" : k.Product.Length > 0 ? k.Product : k.Id;
        string? name = TextPrompt.Ask(this, "Rename keyboard",
            $"Give “{own}” a name you'll recognise. Leave it empty to use its own name.", app.Engine.NameOf(k));
        if (name is null) return;

        if (name.Trim().Length == 0) app.Config.KeyboardNames.Remove(k.Id);
        else app.Config.KeyboardNames[k.Id] = name.Trim();
        app.SaveConfig();
        RefreshKeyboards();
        RefreshShortcuts();
    }

    // ------------------------------------------------------------------ test keys

    /// <summary>The shortcut a key press or signal would run: one for its keyboard, else one for any keyboard.</summary>
    Mapping? FindMapping(Trigger t) =>
        app.Config.Mappings.FirstOrDefault(m => m.Trigger.MatchKey == t.MatchKey)
        ?? (t.Kind == TriggerKind.Key
            ? app.Config.Mappings.FirstOrDefault(m => m.Trigger.MatchKey == Trigger.KeyMatch(t.Vk, t.Modifiers))
            : null);

    void OnObserved(ObservedInput o)
    {
        events.Insert(0, new EventItem(o, FindMapping(o.Trigger)?.Action.Describe()));
        while (events.Count > 150) events.RemoveAt(events.Count - 1);
    }

    void ClearEvents_Click(object sender, RoutedEventArgs e) => events.Clear();

    void MakeShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EventItem item) return;
        if (FindMapping(item.Observed.Trigger) is { } existing) EditShortcut(existing);
        else AddShortcut(item.Observed.Trigger);
    }

    // ------------------------------------------------------------------ status

    void OnFired(Mapping m)
    {
        LastRunText.Text = $"Ran: {m.Action.Describe()}  ({DateTime.Now:HH:mm})";
        LastRunText.Visibility = Visibility.Visible;
    }

    void PauseButton_Click(object sender, RoutedEventArgs e) => app.SetPaused(!app.Paused);

    void UpdatePaused()
    {
        bool paused = app.Paused;
        StatusText.Text = paused ? "Shortcuts are paused" : "Shortcuts are on";
        StatusDot.SetResourceReference(Shape.FillProperty, paused ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        PauseButton.ToolTip = paused ? "Click to turn shortcuts back on" : "Click to pause all shortcuts";
        loading = true;
        PauseToggle.IsChecked = paused;
        loading = false;
    }

    // ------------------------------------------------------------------ settings

    void LoadSettings()
    {
        loading = true;
        StartupToggle.IsChecked = App.StartupEnabled;
        PauseToggle.IsChecked = app.Paused;
        ThemeBox.SelectedIndex = app.Config.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        FnBox.ItemsSource = KeyNames.CustomFnChoices.Select(c => c.Name).ToList();
        FnBox.SelectedIndex = Math.Max(0, Array.FindIndex(KeyNames.CustomFnChoices, c => c.Vk == app.Config.CustomFnVk));
        VersionText.Text = $"KeyLayer {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        ConfigPathText.Text = $"Your shortcuts are saved in {app.ConfigPath}";
        loading = false;
    }

    void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        try
        {
            App.SetStartup(StartupToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't change the startup setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            loading = true;
            StartupToggle.IsChecked = App.StartupEnabled;
            loading = false;
        }
    }

    void PauseToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading) app.SetPaused(PauseToggle.IsChecked == true);
    }

    void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        string theme = ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        app.Config.Theme = theme;
        app.SaveConfig();
        app.ApplyTheme(theme);
    }

    void FnBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || FnBox.SelectedIndex < 0) return;
        app.Config.CustomFnVk = KeyNames.CustomFnChoices[FnBox.SelectedIndex].Vk;
        app.SaveConfig();
    }

    void GitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true })?.Dispose();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = System.IO.Path.GetDirectoryName(app.ConfigPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string text = await app.Engine.DiagnosticsAsync();
        Clipboard.SetText(text);
        CopyDiagnosticsText.Text = "Copied";
        await Task.Delay(1500);
        CopyDiagnosticsText.Text = "Copy diagnostics";
    }
}

/// <summary>Asks for one line of text. Returns null if cancelled.</summary>
internal static class TextPrompt
{
    public static string? Ask(Window owner, string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 100,
            Style = (Style)Application.Current.FindResource("AccentButtonStyle"),
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        save.Click += (_, _) => window.DialogResult = true;
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = message, Style = (Style)Application.Current.FindResource("Body") },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 22, 0, 0),
                    Children = { save, cancel },
                },
            },
        };
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
