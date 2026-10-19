namespace KeyLayer;

/// <summary>Add / edit one shortcut: capture the key, then choose what it does.</summary>
internal sealed class MappingDialog : Form
{
    const int ContentWidth = 540;

    sealed record TypeItem(ActionType Type, string Text) { public override string ToString() => Text; }
    sealed record KeyItem(int Vk, string Text) { public override string ToString() => Text; }
    sealed record BuiltInItem(BuiltInAction Action, string Text) { public override string ToString() => Text; }
    sealed record KeyboardChoice(string? Id, string Name, string Display) { public override string ToString() => Display; }

    readonly InputEngine engine;
    readonly Mapping? existing;
    readonly IReadOnlyList<Mapping> others;
    Trigger? trigger;
    bool capturing;
    bool syncingShortcut;
    bool loadingKeyboards;

    readonly Label keyTitle = new() { AutoSize = true, Font = Ui.HeadingFont, ForeColor = Ui.Ink, Margin = Padding.Empty };
    readonly Label keyDetail = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 3, 0, 0) };
    readonly Label warning = new() { AutoSize = true, ForeColor = Ui.Warning, BackColor = Ui.WarningBack, Padding = new Padding(10, 7, 10, 7), Margin = new Padding(0, 0, 0, 6), Visible = false };
    readonly Button captureButton = Ui.MakeButton("Capture key", primary: true);
    readonly TextBox nameBox = new() { Dock = DockStyle.Fill, PlaceholderText = "e.g.  Fn + F9  →  Spotify" };
    readonly ComboBox keyboardBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Label keyboardHint = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 2, 0, 6), Visible = false };
    readonly ComboBox typeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, Margin = new Padding(0, 4, 0, 0) };
    readonly Panel actionHost = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 168, Margin = new Padding(0, 10, 0, 0) };
    readonly Dictionary<ActionType, Control> panels = [];

    readonly TextBox targetBox = new() { Dock = DockStyle.Fill };
    readonly TextBox argsBox = new() { Dock = DockStyle.Fill };
    readonly TextBox recordBox = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, Cursor = Cursors.Hand, TextAlign = HorizontalAlignment.Center, Font = Ui.StrongFont };
    readonly CheckBox ctrlBox = new() { Text = "Ctrl", AutoSize = true };
    readonly CheckBox shiftBox = new() { Text = "Shift", AutoSize = true };
    readonly CheckBox altBox = new() { Text = "Alt", AutoSize = true };
    readonly CheckBox winBox = new() { Text = "Win", AutoSize = true };
    readonly ComboBox keyBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, MaxDropDownItems = 16 };
    readonly TextBox textBox = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Vertical };
    readonly ComboBox builtInBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340, MaxDropDownItems = 16 };
    readonly TextBox commandBox = new() { Dock = DockStyle.Fill };
    readonly CheckBox hideBox = new() { Text = "Hide the command window", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
    readonly CheckBox blockBox = new() { Text = "Switch off the key's normal function", AutoSize = true, Checked = true, Margin = new Padding(0, 10, 0, 0) };
    readonly Label blockHint = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(18, 0, 0, 0) };

    public Mapping? Result { get; private set; }

    public MappingDialog(InputEngine engine, Mapping? existing, IReadOnlyList<Mapping> others, Trigger? preset)
    {
        this.engine = engine;
        this.existing = existing;
        this.others = others;

        SuspendLayout();
        Text = existing is null ? "Add shortcut" : "Edit shortcut";
        Font = Ui.BaseFont;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(22, 18, 22, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ContentWidth));

        root.Controls.Add(Section("1   Which key?"));
        root.Controls.Add(BuildKeyCard());
        root.Controls.Add(warning);
        root.Controls.Add(BuildNameRow());
        root.Controls.Add(Section("2   What should it do?", top: 16));
        root.Controls.Add(typeBox);
        root.Controls.Add(actionHost);
        root.Controls.Add(blockBox);
        root.Controls.Add(blockHint);
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        BuildActionPanels();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        warning.MaximumSize = blockHint.MaximumSize = new Size(LogicalToDeviceUnits(ContentWidth - 20), 0);
        keyboardHint.MaximumSize = new Size(LogicalToDeviceUnits(ContentWidth - 90), 0);
        keyDetail.MaximumSize = new Size(LogicalToDeviceUnits(ContentWidth - 190), 0);
        keyboardBox.SelectedIndexChanged += (_, _) =>
        {
            if (loadingKeyboards || trigger is null) return;
            UpdateWarning();
            UpdateBlockOption();
        };

        typeBox.Items.AddRange(
        [
            new TypeItem(ActionType.OpenTarget, "Open an app, file, folder or website"),
            new TypeItem(ActionType.SendShortcut, "Press a keyboard shortcut"),
            new TypeItem(ActionType.TypeText, "Type some text"),
            new TypeItem(ActionType.BuiltIn, "Media, volume and system controls"),
            new TypeItem(ActionType.RunCommand, "Run a command"),
            new TypeItem(ActionType.Disable, "Nothing (switch the key off)"),
        ]);
        typeBox.SelectedIndexChanged += (_, _) => ShowActionPanel();

        captureButton.Click += (_, _) => ToggleCapture();

        if (existing is not null) LoadExisting(existing);
        else
        {
            typeBox.SelectedIndex = 0;
            builtInBox.SelectedIndex = 0;
            PopulateKeyboards(null, null);
            if (preset is not null) SetTrigger(preset);
            else ShowNoKey();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (trigger is null) captureButton.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (capturing) engine.CancelCapture();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------------ layout

    static Label Section(string text, int top = 0)
    {
        var label = Ui.MakeLabel(text, Ui.Ink, Ui.HeadingFont);
        label.Margin = new Padding(0, top, 0, 6);
        return label;
    }

    Control BuildKeyCard()
    {
        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Ui.Subtle,
            Padding = new Padding(14, 12, 12, 12),
            Margin = new Padding(0, 0, 0, 8),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        captureButton.Anchor = AnchorStyles.Right;
        captureButton.Margin = new Padding(10, 0, 0, 0);
        card.Controls.Add(keyTitle, 0, 0);
        card.Controls.Add(keyDetail, 0, 1);
        card.Controls.Add(captureButton, 1, 0);
        card.SetRowSpan(captureButton, 2);
        return card;
    }

    Control BuildNameRow()
    {
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 2, 0, 0) };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label RowLabel(string text)
        {
            var label = Ui.MakeLabel(text);
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 0, 10, 0);
            return label;
        }

        rows.Controls.Add(RowLabel("Keyboard"), 0, 0);
        rows.Controls.Add(keyboardBox, 1, 0);
        rows.Controls.Add(keyboardHint, 1, 1);
        rows.Controls.Add(RowLabel("Name"), 0, 2);
        rows.Controls.Add(nameBox, 1, 2);
        return rows;
    }

    Control BuildButtons()
    {
        var save = Ui.MakeButton("Save", primary: true);
        var cancel = Ui.MakeButton("Cancel");
        save.Size = cancel.Size = new Size(110, 34);
        save.Margin = new Padding(10, 0, 0, 0);
        cancel.Margin = Padding.Empty;
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) => Save();
        AcceptButton = save;
        CancelButton = cancel;

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 18, 0, 0),
        };
        row.Controls.Add(save);
        row.Controls.Add(cancel);
        return row;
    }

    static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, Visible = false, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 1; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    static Label Hint(string text, int top = 6)
    {
        var label = Ui.MakeLabel(text, Ui.Muted);
        label.Margin = new Padding(0, top, 0, 0);
        return label;
    }

    void AddPanel(ActionType type, Control panel)
    {
        panels[type] = panel;
        actionHost.Controls.Add(panel);
    }

    void BuildActionPanels()
    {
        // Open an app / file / folder / website
        var open = Grid(3);
        var fileButton = new Button { Text = "File…", Size = new Size(78, 27), Margin = new Padding(6, 0, 0, 0), UseVisualStyleBackColor = true };
        var folderButton = new Button { Text = "Folder…", Size = new Size(78, 27), Margin = new Padding(6, 0, 0, 0), UseVisualStyleBackColor = true };
        var targetLabel = Ui.MakeLabel("Program, file, folder or web address");
        open.Controls.Add(targetLabel, 0, 0);
        open.SetColumnSpan(targetLabel, 3);
        open.Controls.Add(targetBox, 0, 1);
        open.Controls.Add(fileButton, 1, 1);
        open.Controls.Add(folderButton, 2, 1);
        var argsLabel = Ui.MakeLabel("Arguments (optional)");
        argsLabel.Margin = new Padding(0, 8, 0, 4);
        open.Controls.Add(argsLabel, 0, 2);
        open.SetColumnSpan(argsLabel, 3);
        open.Controls.Add(argsBox, 0, 3);
        open.SetColumnSpan(argsBox, 3);
        var openHint = Hint("Examples:  notepad.exe    https://youtube.com    C:\\Users\\you\\Music    ms-settings:display");
        open.Controls.Add(openHint, 0, 4);
        open.SetColumnSpan(openHint, 3);
        fileButton.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "Choose a program or file", Filter = "Programs (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) targetBox.Text = dlg.FileName;
        };
        folderButton.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose a folder to open", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) targetBox.Text = dlg.SelectedPath;
        };
        AddPanel(ActionType.OpenTarget, open);

        // Press a keyboard shortcut
        var shortcut = Grid(1);
        shortcut.Controls.Add(Ui.MakeLabel("Click the box, then press the shortcut you want it to send"));
        shortcut.Controls.Add(recordBox);
        shortcut.Controls.Add(Hint("…or build it yourself:", top: 12));
        var parts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        foreach (var box in new[] { ctrlBox, shiftBox, altBox, winBox })
        {
            box.Margin = new Padding(0, 4, 10, 0);
            box.CheckedChanged += (_, _) => SyncShortcutText();
            parts.Controls.Add(box);
        }
        var plus = Ui.MakeLabel("+", Ui.Muted);
        plus.Margin = new Padding(0, 5, 8, 0);
        parts.Controls.Add(plus);
        parts.Controls.Add(keyBox);
        shortcut.Controls.Add(parts);
        keyBox.Items.AddRange([.. ShortcutKeys()]);
        keyBox.SelectedIndexChanged += (_, _) => SyncShortcutText();
        recordBox.Text = "Click here, then press keys";
        recordBox.PreviewKeyDown += (_, e) => e.IsInputKey = true;
        recordBox.KeyDown += OnRecordKey;
        recordBox.KeyUp += (_, e) => { if (e.KeyCode == Keys.PrintScreen) RecordKey((int)e.KeyCode, ModifiersFrom(e)); };
        AddPanel(ActionType.SendShortcut, shortcut);

        // Type text
        var text = Grid(1);
        text.Controls.Add(Ui.MakeLabel("Text to type (new lines press Enter)"));
        textBox.Height = 120;
        textBox.Dock = DockStyle.Top;
        text.Controls.Add(textBox);
        AddPanel(ActionType.TypeText, text);

        // Built-in controls
        var builtIn = Grid(1);
        builtIn.Controls.Add(Ui.MakeLabel("Control"));
        builtIn.Controls.Add(builtInBox);
        builtIn.Controls.Add(Hint("Brightness works on laptop screens only; most external monitors ignore it."));
        builtInBox.Items.AddRange([.. ActionRunner.BuiltIns.Select(b => new BuiltInItem(b.Action, b.Name))]);
        AddPanel(ActionType.BuiltIn, builtIn);

        // Run a command
        var command = Grid(1);
        command.Controls.Add(Ui.MakeLabel("Command (runs through cmd.exe)"));
        command.Controls.Add(commandBox);
        command.Controls.Add(hideBox);
        command.Controls.Add(Hint("Examples:  start \"\" \"C:\\Games\\game.exe\"     powershell -NoProfile -Command \"…\""));
        AddPanel(ActionType.RunCommand, command);

        // Disable
        var disable = Grid(1);
        disable.Controls.Add(Hint("The key will be switched off and do nothing.", top: 0));
        AddPanel(ActionType.Disable, disable);
    }

    static IEnumerable<KeyItem> ShortcutKeys()
    {
        for (int vk = 'A'; vk <= 'Z'; vk++) yield return new KeyItem(vk, KeyNames.Vk(vk));
        for (int vk = '0'; vk <= '9'; vk++) yield return new KeyItem(vk, KeyNames.Vk(vk));
        for (int vk = 0x70; vk <= 0x87; vk++) yield return new KeyItem(vk, KeyNames.Vk(vk));
        foreach (int vk in (int[])[0x0D, 0x1B, 0x09, 0x20, 0x08, 0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28,
                     0x2C, 0x13, 0x14, 0x90, 0x91, 0x5D, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE])
            yield return new KeyItem(vk, KeyNames.Vk(vk));
        for (int vk = 0x60; vk <= 0x69; vk++) yield return new KeyItem(vk, KeyNames.Vk(vk));
        foreach (int vk in (int[])[0x6A, 0x6B, 0x6D, 0x6E, 0x6F]) yield return new KeyItem(vk, KeyNames.Vk(vk));
        for (int vk = 0xA6; vk <= 0xB7; vk++) yield return new KeyItem(vk, KeyNames.Vk(vk));
    }

    // ------------------------------------------------------------------ key capture

    void ToggleCapture()
    {
        if (capturing)
        {
            engine.CancelCapture();
            capturing = false;
            if (trigger is null) ShowNoKey(); else SetTrigger(trigger);
            return;
        }

        capturing = true;
        keyTitle.Text = "Press the key or Fn combination now…";
        keyDetail.Text = "Waiting up to 15 seconds. Using a custom Fn key? Hold it while you press the other key.";
        captureButton.Text = "Cancel";
        warning.Visible = false;

        engine.BeginCapture(captured =>
        {
            if (!capturing || IsDisposed) return;
            capturing = false;
            if (captured is not null)
            {
                SetTrigger(captured);
                return;
            }
            if (trigger is null) ShowNoKey(); else SetTrigger(trigger);
            ShowWarning("Nothing arrived from the keyboard. If this Fn combo also shows nothing on the Key monitor tab, " +
                        "the keyboard handles it internally and it can't be remapped.");
        });
    }

    void ShowNoKey()
    {
        keyTitle.Text = "No key chosen yet";
        keyDetail.Text = "Click \u201CCapture key\u201D, then press the key or Fn combination you want to use.";
        captureButton.Text = "Capture key";
        UpdateBlockOption();
    }

    void SetTrigger(Trigger t, bool fromSaved = false)
    {
        trigger = t;
        keyTitle.Text = t.Description;
        string? from = t.KeyboardName ?? t.Device;
        keyDetail.Text = string.Join("  ·  ", new[] { t.Signal, from is null ? "" : $"from {from}" }.Where(s => s.Length > 0));
        captureButton.Text = "Capture again";
        nameBox.PlaceholderText = t.Description;

        // A new capture is limited to the keyboard it came from only when there's more than one to choose between.
        string? keyboard = fromSaved || engine.Keyboards.Count > 1 ? t.Keyboard : null;
        PopulateKeyboards(t, keyboard);
        UpdateWarning();
        UpdateBlockOption();
    }

    void PopulateKeyboards(Trigger? t, string? select)
    {
        loadingKeyboards = true;
        keyboardBox.Items.Clear();
        bool hid = t?.Kind == TriggerKind.Hid;
        bool customFn = t is { Kind: TriggerKind.Key } && (t.Modifiers & KeyNames.Fn) != 0;

        if (hid)
        {
            string name = t!.Keyboard is { } id ? engine.NameOf(id, t.KeyboardName ?? t.Device) : t.Device ?? "This device";
            keyboardBox.Items.Add(new KeyboardChoice(t.Keyboard, name, name));
        }
        else
        {
            keyboardBox.Items.Add(new KeyboardChoice(null, "Any keyboard", "Any keyboard"));
            if (!customFn)
            {
                foreach (var k in engine.Keyboards)
                    keyboardBox.Items.Add(new KeyboardChoice(k.Id, engine.NameOf(k), $"{engine.NameOf(k)}  ({k.Connection})"));
                if (select is not null && !keyboardBox.Items.Cast<KeyboardChoice>().Any(c => SameId(c.Id, select)))
                {
                    string name = engine.NameOf(select, t?.KeyboardName);
                    keyboardBox.Items.Add(new KeyboardChoice(select, name, $"{name}  (not connected)"));
                }
            }
        }
        keyboardBox.SelectedItem = keyboardBox.Items.Cast<KeyboardChoice>().FirstOrDefault(c => SameId(c.Id, select))
                                   ?? keyboardBox.Items[0];
        keyboardBox.Enabled = !hid && !customFn;
        keyboardHint.Text = hid ? "This signal only comes from this device."
            : customFn ? "Custom Fn shortcuts work on every keyboard."
            : "";
        keyboardHint.Visible = keyboardHint.Text.Length > 0;
        loadingKeyboards = false;
    }

    static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The keyboard a key shortcut is limited to (null = any). HID signals always belong to their device.</summary>
    string? SelectedKeyboard => trigger?.Kind == TriggerKind.Key ? (keyboardBox.SelectedItem as KeyboardChoice)?.Id : null;

    /// <summary>The captured trigger, limited to the chosen keyboard.</summary>
    Trigger EffectiveTrigger()
    {
        var t = trigger!;
        if (t.Kind == TriggerKind.Hid) return t;
        var choice = keyboardBox.SelectedItem as KeyboardChoice;
        return new Trigger
        {
            Kind = t.Kind,
            Vk = t.Vk,
            Modifiers = t.Modifiers,
            Description = t.Description,
            Signal = t.Signal,
            Device = t.Device,
            Keyboard = choice?.Id,
            KeyboardName = choice?.Id is null ? null : choice.Name,
        };
    }

    void UpdateWarning()
    {
        if (trigger is null) return;
        string match = EffectiveTrigger().MatchKey;
        var clash = others.FirstOrDefault(m => m.Trigger.MatchKey == match);
        string? message = clash is not null
            ? $"This key already has a shortcut for this keyboard (“{clash.Name}”). Saving will replace it."
            : WarningFor(trigger);
        if (message is null) warning.Visible = false; else ShowWarning(message);
    }

    void ShowWarning(string text)
    {
        warning.Text = text;
        warning.Visible = true;
    }

    static string? WarningFor(Trigger t)
    {
        if (t.Kind == TriggerKind.Hid)
            return t.UsagePage == 0x01
                ? "This is a system power signal. Windows will still do its own thing (like going to sleep) and that part can't be switched off."
                : null;
        if (KeyNames.IsSpecialKey(t.Vk) || (t.Modifiers & KeyNames.Fn) != 0) return null;
        string key = KeyNames.Vk(t.Vk);
        return t.Modifiers == 0
            ? $"This combo sends an ordinary {key} key, so the shortcut will also run when you press {key} without Fn. " +
              "If your keyboard has an Fn-lock (often Fn + Esc), switching it may make this key send a media signal instead."
            : $"This combo sends the ordinary shortcut {t.Description}, so it will also run whenever you press {t.Description} yourself.";
    }

    void UpdateBlockOption()
    {
        bool isKey = trigger is null || trigger.Kind == TriggerKind.Key;
        // Windows names the keyboard only after an ordinary key has been delivered (media keys are held until it does).
        bool ordinaryOnOneKeyboard = trigger is { Kind: TriggerKind.Key } t && SelectedKeyboard is not null && !KeyNames.HasMediaSignal(t.Vk);
        bool canSwitchOff = isKey && !ordinaryOnOneKeyboard;
        bool wasEnabled = blockBox.Enabled;
        blockBox.Enabled = canSwitchOff;
        if (!canSwitchOff) blockBox.Checked = false;
        else if (!wasEnabled) blockBox.Checked = true;
        blockHint.Text = !isKey
            ? "Not needed: Windows doesn't use this signal, so there's nothing to switch off."
            : ordinaryOnOneKeyboard
                ? "Not possible for one keyboard only: Windows says which keyboard a key came from only after the key has " +
                  "done its job. Choose “Any keyboard” to switch it off."
                : "Recommended. Otherwise the key does its usual thing as well as your shortcut.";
    }

    // ------------------------------------------------------------------ shortcut recorder

    static int ModifiersFrom(KeyEventArgs e)
    {
        int mods = (e.Control ? KeyNames.Ctrl : 0) | (e.Shift ? KeyNames.Shift : 0) | (e.Alt ? KeyNames.Alt : 0);
        if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0) mods |= KeyNames.Win;
        return mods;
    }

    void OnRecordKey(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        int vk = (int)e.KeyCode;
        int mods = ModifiersFrom(e);
        if (KeyNames.IsModifier(vk))
        {
            recordBox.Text = KeyNames.ModText(mods | KeyNames.ModFlag(vk)) + "…";
            return;
        }
        RecordKey(vk, mods);
    }

    void RecordKey(int vk, int mods)
    {
        syncingShortcut = true;
        ctrlBox.Checked = (mods & KeyNames.Ctrl) != 0;
        shiftBox.Checked = (mods & KeyNames.Shift) != 0;
        altBox.Checked = (mods & KeyNames.Alt) != 0;
        winBox.Checked = (mods & KeyNames.Win) != 0;
        var item = keyBox.Items.Cast<KeyItem>().FirstOrDefault(k => k.Vk == vk);
        if (item is null)
        {
            item = new KeyItem(vk, KeyNames.Vk(vk));
            keyBox.Items.Add(item);
        }
        keyBox.SelectedItem = item;
        syncingShortcut = false;
        SyncShortcutText();
    }

    int ShortcutMods =>
        (ctrlBox.Checked ? KeyNames.Ctrl : 0) | (shiftBox.Checked ? KeyNames.Shift : 0) |
        (altBox.Checked ? KeyNames.Alt : 0) | (winBox.Checked ? KeyNames.Win : 0);

    void SyncShortcutText()
    {
        if (syncingShortcut) return;
        recordBox.Text = keyBox.SelectedItem is KeyItem key ? KeyNames.Combo(ShortcutMods, key.Vk) : "Click here, then press keys";
    }

    // ------------------------------------------------------------------ load / save

    void ShowActionPanel()
    {
        var type = (typeBox.SelectedItem as TypeItem)?.Type ?? ActionType.OpenTarget;
        foreach (var (panelType, panel) in panels) panel.Visible = panelType == type;
    }

    void LoadExisting(Mapping m)
    {
        nameBox.Text = m.Name;
        var a = m.Action;
        typeBox.SelectedItem = typeBox.Items.Cast<TypeItem>().First(t => t.Type == a.Type);
        targetBox.Text = a.Target ?? "";
        argsBox.Text = a.Arguments ?? "";
        textBox.Text = a.Text ?? "";
        commandBox.Text = a.Command ?? "";
        hideBox.Checked = a.HideWindow;
        builtInBox.SelectedItem = builtInBox.Items.Cast<BuiltInItem>().FirstOrDefault(b => b.Action == a.BuiltIn) ?? builtInBox.Items[0];
        if (a.ShortcutVk != 0) RecordKey(a.ShortcutVk, a.ShortcutMods);
        SetTrigger(m.Trigger, fromSaved: true);
        if (blockBox.Enabled) blockBox.Checked = m.BlockOriginal;
    }

    void Save()
    {
        if (capturing)
        {
            Fail("Finish capturing the key first (or click Cancel).");
            return;
        }
        if (trigger is null)
        {
            Fail("Choose a key first: click \u201CCapture key\u201D and press the key.");
            return;
        }

        var type = (typeBox.SelectedItem as TypeItem)?.Type ?? ActionType.OpenTarget;
        var action = new ActionSpec { Type = type };
        switch (type)
        {
            case ActionType.OpenTarget:
                if (string.IsNullOrWhiteSpace(targetBox.Text)) { Fail("Enter a program, file, folder or web address to open.", targetBox); return; }
                action.Target = targetBox.Text.Trim();
                action.Arguments = argsBox.Text.Trim();
                break;
            case ActionType.SendShortcut:
                if (keyBox.SelectedItem is not KeyItem key) { Fail("Choose the shortcut to send.", recordBox); return; }
                action.ShortcutVk = key.Vk;
                action.ShortcutMods = ShortcutMods;
                break;
            case ActionType.TypeText:
                if (textBox.Text.Length == 0) { Fail("Enter the text to type.", textBox); return; }
                action.Text = textBox.Text;
                break;
            case ActionType.BuiltIn:
                action.BuiltIn = (builtInBox.SelectedItem as BuiltInItem)?.Action ?? BuiltInAction.PlayPause;
                break;
            case ActionType.RunCommand:
                if (string.IsNullOrWhiteSpace(commandBox.Text)) { Fail("Enter a command to run.", commandBox); return; }
                action.Command = commandBox.Text.Trim();
                action.HideWindow = hideBox.Checked;
                break;
        }

        var effective = EffectiveTrigger();
        Result = new Mapping
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? effective.Description : nameBox.Text.Trim(),
            Enabled = existing?.Enabled ?? true,
            BlockOriginal = effective.Kind == TriggerKind.Key && blockBox.Enabled && blockBox.Checked,
            Trigger = effective,
            Action = action,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    void Fail(string message, Control? focus = null)
    {
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        focus?.Focus();
    }
}
