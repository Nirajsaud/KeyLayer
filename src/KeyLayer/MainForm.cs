using System.Diagnostics;

namespace KeyLayer;

internal sealed class MainForm : Form
{
    readonly TrayApp app;
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(16, 5) };
    readonly TabPage shortcutsPage = NewPage("Shortcuts");
    readonly TabPage keyboardsPage = NewPage("Keyboards");
    readonly TabPage monitorPage = NewPage("Key monitor");
    readonly TabPage helpPage = NewPage("Help");

    readonly ListView list = NewList(checkBoxes: true);
    readonly Label emptyState = new();
    readonly Button addButton = Ui.MakeButton("Add shortcut…", primary: true);
    readonly Button editButton = Ui.MakeButton("Edit…");
    readonly Button removeButton = Ui.MakeButton("Remove");
    readonly Button tryButton = Ui.MakeButton("Try it now");

    readonly ListView keyboardList = NewList(checkBoxes: false);
    readonly Button renameButton = Ui.MakeButton("Rename…");
    readonly ComboBox customFnBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly Dictionary<string, DateTime> lastUsed = new(StringComparer.OrdinalIgnoreCase);
    string? activeKeyboard;
    Font? boldListFont;

    readonly ListView monitor = NewList(checkBoxes: false);
    readonly Button fromSignalButton = Ui.MakeButton("Make shortcut…", primary: true);
    readonly Button clearButton = Ui.MakeButton("Clear");

    sealed record FnChoice(int Vk, string Name) { public override string ToString() => Name; }

    readonly Label deviceDot = new() { Text = "●", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
    readonly Label deviceText = new() { AutoSize = true, ForeColor = Color.White, Margin = Padding.Empty };
    readonly CheckBox pauseBox = new() { Text = "Pause all shortcuts", AutoSize = true, Margin = new Padding(0, 3, 18, 3) };
    readonly CheckBox startupBox = new() { Text = "Start with Windows", AutoSize = true, Margin = new Padding(0, 3, 18, 3) };
    readonly Label statusLabel = new() { AutoSize = true, ForeColor = Ui.Muted, Anchor = AnchorStyles.Right, Margin = new Padding(0, 5, 0, 3) };
    readonly ToolTip tips = new();
    bool populating;

    public MainForm(TrayApp app)
    {
        this.app = app;
        SuspendLayout();

        Text = "KeyLayer";
        Icon = AppIcon.Window();
        Font = Ui.BaseFont;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;

        // Dock order: the Fill control first, so the header and footer claim their space before it.
        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(780, 480);
        ResumeLayout(false);
        PerformLayout();

        startupBox.Checked = TrayApp.StartupEnabled;
        pauseBox.Checked = app.Paused;
        startupBox.CheckedChanged += (_, _) => ToggleStartup();
        pauseBox.CheckedChanged += (_, _) => app.SetPaused(pauseBox.Checked);
        app.PausedChanged += () => pauseBox.Checked = app.Paused;

        app.Engine.Observed += OnObserved;
        app.Engine.KeyboardsChanged += UpdateDevice;
        app.Engine.KeyboardActivity += OnKeyboardActivity;
        app.Engine.Fired += m => statusLabel.Text = $"Ran “{m.Name}” at {DateTime.Now:HH:mm:ss}";

        tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateMonitorState();
            if (tabs.SelectedTab == keyboardsPage) RefreshKeyboards();
        };
        VisibleChanged += (_, _) => UpdateMonitorState();

        RefreshList();
        UpdateDevice();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.ScaleList(list);
        Ui.ScaleList(keyboardList);
        Ui.ScaleList(monitor);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Ui.ScaleList(list);
        Ui.ScaleList(keyboardList);
        Ui.ScaleList(monitor);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!app.Quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!app.Config.TrayTipShown)
            {
                app.Notify("KeyLayer is still running",
                    "Your shortcuts keep working in the background. Right-click the tray icon to quit.", ToolTipIcon.Info);
                app.Config.TrayTipShown = true;
                app.SaveConfig();
            }
            return;
        }
        app.Engine.Observed -= OnObserved;
        app.Engine.KeyboardsChanged -= UpdateDevice;
        app.Engine.KeyboardActivity -= OnKeyboardActivity;
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ layout

    static TabPage NewPage(string text) => new(text) { Padding = new Padding(14), UseVisualStyleBackColor = true };

    static ListView NewList(bool checkBoxes) => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        CheckBoxes = checkBoxes,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Margin = Padding.Empty,
    };

    Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Ui.HeaderBack,
            Padding = new Padding(22, 16, 22, 16),
            ColumnCount = 2,
            RowCount = 2,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = Ui.MakeLabel("KeyLayer", Color.White, Ui.TitleFont);
        title.Margin = Padding.Empty;
        var subtitle = Ui.MakeLabel("Custom shortcuts for every keyboard on this PC", Ui.HeaderMuted);
        subtitle.Margin = new Padding(2, 2, 0, 0);

        var chip = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            BackColor = Ui.HeaderChip,
            Padding = new Padding(12, 7, 14, 7),
            Margin = Padding.Empty,
        };
        chip.Controls.Add(deviceDot);
        chip.Controls.Add(deviceText);

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(chip, 1, 0);
        header.SetRowSpan(chip, 2);
        return header;
    }

    Control BuildBody()
    {
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 4), BackColor = Color.White };
        BuildShortcutsPage();
        BuildKeyboardsPage();
        BuildMonitorPage();
        BuildHelpPage();
        tabs.TabPages.AddRange([shortcutsPage, keyboardsPage, monitorPage, helpPage]);
        host.Controls.Add(tabs);
        return host;
    }

    Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(18, 6, 18, 10),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusLabel.Text = "Closing this window keeps your shortcuts running in the tray";
        footer.Controls.Add(pauseBox, 0, 0);
        footer.Controls.Add(startupBox, 1, 0);
        footer.Controls.Add(statusLabel, 2, 0);
        return footer;
    }

    void BuildShortcutsPage()
    {
        Ui.AddColumn(list, "Shortcut", 140);
        Ui.AddColumn(list, "Key or signal", 130);
        Ui.AddColumn(list, "Keyboard", 140);
        Ui.AddColumn(list, "What it does", 200);
        Ui.AddColumn(list, "Key itself", 110);

        emptyState.Dock = DockStyle.Fill;
        emptyState.TextAlign = ContentAlignment.MiddleCenter;
        emptyState.BackColor = Ui.Subtle;
        emptyState.ForeColor = Ui.Muted;
        emptyState.Text = "No shortcuts yet\n\nClick “Add shortcut…”, then press any key or Fn combination.\n" +
                          "Not sure which combos work? Press them on the Key monitor tab first.";

        var listHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        listHost.Controls.Add(list);
        listHost.Controls.Add(emptyState);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(14, 0, 0, 0),
        };
        buttons.Controls.AddRange([addButton, editButton, removeButton, tryButton]);
        tips.SetToolTip(tryButton, "Run the selected shortcut's action once, without pressing the key");

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(listHost, 0, 0);
        grid.Controls.Add(buttons, 1, 0);
        shortcutsPage.Controls.Add(grid);

        addButton.Click += (_, _) => AddMapping(null);
        editButton.Click += (_, _) => EditSelected();
        removeButton.Click += (_, _) => RemoveSelected();
        tryButton.Click += (_, _) => { if (Selected is { } m) app.Engine.RunNow(m); };
        list.ItemActivate += (_, _) => EditSelected();
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        list.ItemChecked += (_, e) =>
        {
            if (populating || e.Item.Tag is not Mapping m) return;
            m.Enabled = e.Item.Checked;
            e.Item.ForeColor = m.Enabled ? Ui.Ink : Ui.Muted;
            app.SaveConfig();
        };
    }

    void BuildKeyboardsPage()
    {
        Ui.AddColumn(keyboardList, "Keyboard", 240);
        Ui.AddColumn(keyboardList, "Connection", 90);
        Ui.AddColumn(keyboardList, "ID", 150);
        Ui.AddColumn(keyboardList, "Last key press", 110);

        var intro = Ui.MakeLabel(
            "Every keyboard connected to this PC. Press a key on one and its row lights up.\n" +
            "A laptop's own keys (including its Fn hotkeys) count as one built-in keyboard. Receivers are mouse dongles that can also send keys.", Ui.Muted);
        intro.Margin = new Padding(0, 0, 0, 10);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(14, 0, 0, 0),
        };
        buttons.Controls.Add(renameButton);
        tips.SetToolTip(renameButton, "Give the selected keyboard a name you'll recognise");

        var fnHeading = Ui.MakeLabel("Custom Fn key", Ui.Ink, Ui.HeadingFont);
        fnHeading.Margin = new Padding(0, 16, 0, 3);
        var fnBody = Ui.MakeLabel(
            "No Fn key, or want more combos? Pick a key to hold as your own Fn. Hold it and press another key to run a shortcut;\n" +
            "tap it on its own and it does its normal job. To add one, click Capture key while holding your custom Fn.", Ui.Muted);
        var fnLabel = Ui.MakeLabel("Custom Fn key:");
        fnLabel.Margin = new Padding(0, 5, 8, 0);
        var fnRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        fnRow.Controls.Add(fnLabel);
        fnRow.Controls.Add(customFnBox);

        customFnBox.Items.AddRange([.. KeyNames.CustomFnChoices.Select(c => new FnChoice(c.Vk, c.Name))]);
        customFnBox.SelectedItem = customFnBox.Items.Cast<FnChoice>().FirstOrDefault(c => c.Vk == app.Config.CustomFnVk)
                                   ?? customFnBox.Items[0];
        customFnBox.SelectedIndexChanged += (_, _) =>
        {
            if (customFnBox.SelectedItem is not FnChoice choice || choice.Vk == app.Config.CustomFnVk) return;
            app.Config.CustomFnVk = choice.Vk;
            app.SaveConfig();
            statusLabel.Text = choice.Vk == 0 ? "Custom Fn key switched off" : $"Custom Fn key: {choice.Name}";
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(intro, 0, 0);
        grid.SetColumnSpan(intro, 2);
        grid.Controls.Add(keyboardList, 0, 1);
        grid.Controls.Add(buttons, 1, 1);
        grid.Controls.Add(fnHeading, 0, 2);
        grid.Controls.Add(fnBody, 0, 3);
        grid.Controls.Add(fnRow, 0, 4);
        grid.SetColumnSpan(fnBody, 2);
        keyboardsPage.Controls.Add(grid);

        renameButton.Enabled = false;
        keyboardList.SelectedIndexChanged += (_, _) => renameButton.Enabled = keyboardList.SelectedItems.Count > 0;
        keyboardList.ItemActivate += (_, _) => RenameSelectedKeyboard();
        renameButton.Click += (_, _) => RenameSelectedKeyboard();
    }

    void BuildMonitorPage()
    {
        Ui.AddColumn(monitor, "Time", 72);
        Ui.AddColumn(monitor, "Type", 62);
        Ui.AddColumn(monitor, "Signal", 220);
        Ui.AddColumn(monitor, "Meaning", 160);
        Ui.AddColumn(monitor, "From", 230);

        var intro = Ui.MakeLabel(
            "Press keys and Fn combinations: everything your keyboard sends shows up below.\n" +
            "If an Fn combo shows nothing here, the keyboard handles it internally and it can't be remapped.", Ui.Muted);
        intro.Margin = new Padding(0, 0, 0, 10);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(14, 0, 0, 0),
        };
        buttons.Controls.AddRange([fromSignalButton, clearButton]);
        tips.SetToolTip(fromSignalButton, "Create a shortcut for the selected signal");

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(intro, 0, 0);
        grid.SetColumnSpan(intro, 2);
        grid.Controls.Add(monitor, 0, 1);
        grid.Controls.Add(buttons, 1, 1);
        monitorPage.Controls.Add(grid);

        fromSignalButton.Enabled = false;
        monitor.SelectedIndexChanged += (_, _) => fromSignalButton.Enabled = monitor.SelectedItems.Count > 0;
        monitor.ItemActivate += (_, _) => MappingFromSignal();
        fromSignalButton.Click += (_, _) => MappingFromSignal();
        clearButton.Click += (_, _) => monitor.Items.Clear();
    }

    void BuildHelpPage()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(4, 0, 4, 4),
        };
        var bodies = new List<Label>();

        void Section(string heading, string body)
        {
            var h = Ui.MakeLabel(heading, Ui.Ink, Ui.HeadingFont);
            h.Margin = new Padding(0, flow.Controls.Count == 0 ? 0 : 12, 0, 3);
            var b = Ui.MakeLabel(body, Ui.Muted);
            flow.Controls.Add(h);
            flow.Controls.Add(b);
            bodies.Add(b);
        }

        Section("How the Fn key works",
            "Windows never sees the Fn key: the keyboard handles it internally. When you hold Fn and press another key, " +
            "the keyboard sends a different signal instead, such as Volume Up, Mail or Calculator. KeyLayer " +
            "listens for those signals and runs your own action instead.");
        Section("Which Fn combos can I change?",
            "Any combo that shows up on the Key monitor tab. Combos that show nothing there (usually backlight, lighting " +
            "effects or the Windows-key lock) are handled entirely inside the keyboard, so no app can change them.");
        Section("Why do some Fn keys seem to do nothing?",
            "Many keyboards send signals like Mail, Media Player or Browser Home. Windows only reacts to them if a default " +
            "app is set up, so they often look broken. They're the best keys to remap.");
        Section("A combo sends an ordinary key like F5",
            "Then the remap also changes that key when pressed without Fn. Many keyboards have an Fn-lock " +
            "(often Fn + Esc) that swaps the top row between F1–F12 and the media functions.");
        Section("Several keyboards",
            "Each shortcut can work on every keyboard or on just one (pick it in the shortcut's Keyboard box), so the same " +
            "key can do different things on different keyboards. Windows only says which keyboard a key came from once it's " +
            "released, so a one-keyboard shortcut on an ordinary key runs when you let go. On the other keyboards that key " +
            "still works, also on release (holding it won't repeat). Media keys like volume or Mail aren't affected by this.");
        Section("No Fn key?",
            "Pick a custom Fn key on the Keyboards tab, such as Caps Lock. Then add shortcuts by holding it while you " +
            "capture a key. Tapped on its own, the key keeps doing its normal job.");
        Section("Administrator windows",
            "Windows doesn't let ordinary apps react to keys while an app running as administrator (like Task Manager) " +
            "is in front. If you need that, run KeyLayer as administrator.");
        Section("Your shortcuts", "Saved in " + app.ConfigPath);

        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
        var copyDiag = Ui.MakeButton("Copy diagnostics");
        var openFolder = Ui.MakeButton("Open settings folder");
        copyDiag.Margin = openFolder.Margin = new Padding(0, 0, 10, 0);
        row.Controls.AddRange([copyDiag, openFolder]);
        flow.Controls.Add(row);

        copyDiag.Click += async (_, _) =>
        {
            string text = await app.Engine.DiagnosticsAsync();
            Clipboard.SetText(text);
            statusLabel.Text = "Diagnostics copied to the clipboard";
        };
        openFolder.Click += (_, _) =>
        {
            string folder = Path.GetDirectoryName(app.ConfigPath)!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        };

        // Wrap the paragraphs to the visible width.
        flow.SizeChanged += (_, _) =>
        {
            int width = Math.Max(200, flow.ClientSize.Width - flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            foreach (var b in bodies) b.MaximumSize = new Size(width, 0);
        };
        helpPage.Controls.Add(flow);
    }

    // ------------------------------------------------------------------ shortcuts

    Mapping? Selected => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Mapping : null;

    void RefreshList(Mapping? select = null)
    {
        populating = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var m in app.Config.Mappings)
        {
            var item = new ListViewItem(m.Name) { Checked = m.Enabled, Tag = m, ForeColor = m.Enabled ? Ui.Ink : Ui.Muted };
            item.SubItems.Add(m.Trigger.Description);
            item.SubItems.Add(KeyboardText(m.Trigger));
            item.SubItems.Add(m.Action.Describe());
            item.SubItems.Add(KeyItselfText(m));
            item.ToolTipText = m.Trigger.Device is { } device ? $"{m.Trigger.Signal} from {device}" : m.Trigger.Signal;
            list.Items.Add(item);
            if (m == select) item.Selected = true;
        }
        list.ShowItemToolTips = true;
        list.EndUpdate();
        populating = false;

        bool empty = app.Config.Mappings.Count == 0;
        list.Visible = !empty;
        emptyState.Visible = empty;
        Ui.FitColumns(list); // a vertical scrollbar may have appeared
        UpdateButtons();
    }

    /// <summary>What happens to the key's normal function (mirrors what the engine can actually do).</summary>
    static string KeyItselfText(Mapping m)
    {
        var t = m.Trigger;
        if (t.Kind == TriggerKind.Hid) return "Not affected";
        return m.BlockOriginal ? "Switched off" : "Still works";
    }

    string KeyboardText(Trigger t) => t.Keyboard is { } id
        ? app.Engine.NameOf(id, t.KeyboardName ?? t.Device)
        : t.Kind == TriggerKind.Hid ? t.Device ?? "—" : "Any keyboard";

    /// <summary>The shortcut a key press or signal would run: one for its keyboard, else one for any keyboard.</summary>
    Mapping? FindMapping(Trigger t) =>
        app.Config.Mappings.FirstOrDefault(m => m.Trigger.MatchKey == t.MatchKey)
        ?? (t.Kind == TriggerKind.Key
            ? app.Config.Mappings.FirstOrDefault(m => m.Trigger.MatchKey == Trigger.KeyMatch(t.Vk, t.Modifiers))
            : null);

    void UpdateButtons()
    {
        bool any = Selected is not null;
        editButton.Enabled = removeButton.Enabled = tryButton.Enabled = any;
    }

    void AddMapping(Trigger? preset)
    {
        using var dialog = new MappingDialog(app.Engine, null, [.. app.Config.Mappings], preset);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } mapping) return;
        Store(null, mapping);
        tabs.SelectedTab = shortcutsPage;
    }

    void EditSelected()
    {
        if (Selected is not { } current) return;
        using var dialog = new MappingDialog(app.Engine, current, [.. app.Config.Mappings.Where(m => m != current)], null);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } updated) return;
        Store(current, updated);
    }

    /// <summary>Adds or replaces a shortcut. Another shortcut on the same key is replaced (the dialog warned about it).</summary>
    void Store(Mapping? current, Mapping updated)
    {
        var mappings = app.Config.Mappings;
        int index = current is null ? -1 : mappings.IndexOf(current);
        if (index >= 0) mappings[index] = updated;
        else mappings.Add(updated);
        mappings.RemoveAll(m => m != updated && m.Trigger.MatchKey == updated.Trigger.MatchKey);
        app.SaveConfig();
        RefreshList(updated);
    }

    void RemoveSelected()
    {
        if (Selected is not { } current) return;
        if (MessageBox.Show(this, $"Remove the shortcut “{current.Name}”?", "KeyLayer",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        app.Config.Mappings.Remove(current);
        app.SaveConfig();
        RefreshList();
    }

    void ToggleStartup()
    {
        try
        {
            TrayApp.SetStartup(startupBox.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't change the startup setting", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------ monitor

    void UpdateMonitorState() => app.Engine.MonitorEnabled = Visible && tabs.SelectedTab == monitorPage;

    void OnObserved(ObservedInput o)
    {
        var item = new ListViewItem(o.Time.ToString("HH:mm:ss")) { Tag = o };
        item.SubItems.Add(o.Source);
        item.SubItems.Add(o.Signal);
        item.SubItems.Add(o.Meaning);
        item.SubItems.Add(o.Device);
        if (FindMapping(o.Trigger) is { } mapped)
        {
            item.SubItems[3].Text += $"  → {mapped.Name}";
            item.ForeColor = Ui.Accent;
        }
        monitor.Items.Insert(0, item);
        while (monitor.Items.Count > 200) monitor.Items.RemoveAt(monitor.Items.Count - 1);
    }

    void MappingFromSignal()
    {
        if (monitor.SelectedItems.Count == 0 || monitor.SelectedItems[0].Tag is not ObservedInput o) return;
        var existing = FindMapping(o.Trigger);
        if (existing is not null)
        {
            tabs.SelectedTab = shortcutsPage;
            RefreshList(existing);
            EditSelected();
            return;
        }
        AddMapping(o.Trigger);
    }

    void UpdateDevice()
    {
        var engine = app.Engine;
        var keyboards = engine.Keyboards.Where(k => !k.IsReceiver).ToList();
        deviceDot.ForeColor = keyboards.Count > 0 ? Ui.Good : Ui.HeaderMuted;
        deviceText.Text = keyboards.Count switch
        {
            0 => "No keyboard found",
            1 => $"{engine.NameOf(keyboards[0])} connected",
            _ => $"{keyboards.Count} keyboards connected",
        };
        tips.SetToolTip(deviceText, engine.Keyboards.Count == 0
            ? "Plug in a keyboard."
            : string.Join("\n", engine.Keyboards.Select(k => $"{engine.NameOf(k)}  ({k.Connection})")));
        RefreshKeyboards();
        RefreshList(Selected);
    }

    // ------------------------------------------------------------------ keyboards

    void OnKeyboardActivity(string id)
    {
        lastUsed[id] = DateTime.Now;
        activeKeyboard = id;
        if (Visible && tabs.SelectedTab == keyboardsPage) RefreshKeyboards();
    }

    void RefreshKeyboards()
    {
        string? selected = keyboardList.SelectedItems.Count > 0 ? (keyboardList.SelectedItems[0].Tag as KeyboardInfo)?.Id : null;
        keyboardList.BeginUpdate();
        keyboardList.Items.Clear();
        foreach (var keyboard in app.Engine.Keyboards)
        {
            var item = new ListViewItem(app.Engine.NameOf(keyboard)) { Tag = keyboard };
            item.SubItems.Add(keyboard.Connection);
            item.SubItems.Add(keyboard.Id);
            item.SubItems.Add(lastUsed.TryGetValue(keyboard.Id, out var time) ? time.ToString("HH:mm:ss") : "—");
            if (keyboard.Id == activeKeyboard)
            {
                item.ForeColor = Ui.Accent;
                item.Font = BoldListFont(); // derived from the list's font, which WinForms rescales per monitor
            }
            keyboardList.Items.Add(item);
            if (keyboard.Id == selected) item.Selected = true;
        }
        keyboardList.EndUpdate();
        Ui.FitColumns(keyboardList);
        renameButton.Enabled = keyboardList.SelectedItems.Count > 0;
    }

    Font BoldListFont()
    {
        if (boldListFont is null || boldListFont.Size != keyboardList.Font.Size)
        {
            boldListFont?.Dispose();
            boldListFont = new Font(keyboardList.Font, FontStyle.Bold);
        }
        return boldListFont;
    }

    void RenameSelectedKeyboard()
    {
        if (keyboardList.SelectedItems.Count == 0 || keyboardList.SelectedItems[0].Tag is not KeyboardInfo keyboard) return;
        string own = keyboard.Product.Length > 0 ? keyboard.Product : keyboard.Id;
        string? name = Ui.Prompt(this, "Rename keyboard",
            $"Name for “{own}” ({keyboard.Id}). Leave it empty to go back to the keyboard's own name.",
            app.Engine.NameOf(keyboard));
        if (name is null) return;

        if (name.Trim().Length == 0) app.Config.KeyboardNames.Remove(keyboard.Id);
        else app.Config.KeyboardNames[keyboard.Id] = name.Trim();
        app.SaveConfig();
        UpdateDevice();
    }
}
