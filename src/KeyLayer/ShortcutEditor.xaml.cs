using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace KeyLayer;

/// <summary>Add / edit one shortcut: capture the key, pick the keyboard, choose what it does.</summary>
internal partial class ShortcutEditor : Window
{
    sealed record KeyItem(int Vk, string Text) { public override string ToString() => Text; }
    sealed record BuiltInItem(BuiltInAction Action, string Text) { public override string ToString() => Text; }
    sealed record KeyboardChoice(string? Id, string Name, string Display) { public override string ToString() => Display; }

    readonly App app;
    readonly Mapping? existing;
    readonly IReadOnlyList<Mapping> others;
    readonly Dictionary<RadioButton, (ActionType Type, FrameworkElement Panel)> tiles;
    Trigger? trigger;
    bool capturing;
    bool syncingShortcut;
    bool loadingKeyboards;

    public Mapping? Result { get; private set; }

    public ShortcutEditor(App app, Mapping? existing, IReadOnlyList<Mapping> others, Trigger? preset)
    {
        this.app = app;
        this.existing = existing;
        this.others = others;
        InitializeComponent();

        Title = Heading.Text = existing is null ? "Add shortcut" : "Edit shortcut";
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        tiles = new()
        {
            [TileOpen] = (ActionType.OpenTarget, PanelOpen),
            [TileShortcut] = (ActionType.SendShortcut, PanelShortcut),
            [TileText] = (ActionType.TypeText, PanelText),
            [TileMedia] = (ActionType.BuiltIn, PanelMedia),
            [TileCommand] = (ActionType.RunCommand, PanelCommand),
            [TileOff] = (ActionType.Disable, PanelOff),
        };
        BuiltInBox.ItemsSource = ActionRunner.BuiltIns.Select(b => new BuiltInItem(b.Action, b.Name)).ToList();
        foreach (var key in ShortcutKeys()) KeyBox.Items.Add(key);
        RecordBox.Text = "Click here, then press the keys";

        if (existing is not null)
        {
            LoadExisting(existing);
        }
        else
        {
            SelectAction(ActionType.OpenTarget);
            BuiltInBox.SelectedIndex = 0;
            PopulateKeyboards(null, null);
            if (preset is not null) SetTrigger(preset);
            else ShowKey();
        }
        UpdateNamePlaceholder();
        Loaded += (_, _) => { if (trigger is null) CaptureButton.Focus(); };

        // While waiting for a key, keep it from also pressing buttons here (Enter = Save, Esc = Cancel, ...).
        PreviewKeyDown += (_, e) => { if (capturing) e.Handled = true; };
        PreviewKeyUp += (_, e) => { if (capturing) e.Handled = true; };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (capturing) app.Engine.CancelCapture();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ key capture

    void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (capturing)
        {
            app.Engine.CancelCapture();
            capturing = false;
            ShowKey();
            return;
        }

        capturing = true;
        ShowCapturing();
        app.Engine.BeginCapture(captured =>
        {
            if (!capturing || !IsLoaded) return;
            capturing = false;
            if (captured is not null)
            {
                SetTrigger(captured);
                return;
            }
            ShowKey();
            ShowWarning("Nothing arrived from the keyboard. If this Fn combination also shows nothing on the Test keys " +
                        "page, the keyboard handles it internally and it can't be changed.");
        });
    }

    void ShowCapturing()
    {
        KeyEmpty.Visibility = Visibility.Visible;
        KeyCaptured.Visibility = Visibility.Collapsed;
        KeyEmptyIcon.Text = "";
        KeyPrompt.Text = "Press the key or Fn combination now…";
        KeyPromptDetail.Text = "Then let go of it, so KeyLayer can tell which keyboard it came from. Waiting up to 15 seconds.";
        CaptureAction.Text = "Cancel";
        CaptureAction.Visibility = Visibility.Visible;
        WarningBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows the chosen key, or the prompt when there isn't one yet.</summary>
    void ShowKey()
    {
        if (trigger is null)
        {
            KeyEmpty.Visibility = Visibility.Visible;
            KeyCaptured.Visibility = Visibility.Collapsed;
            KeyEmptyIcon.Text = "";
            KeyPrompt.Text = "Click here, then press a key";
            KeyPromptDetail.Text = "Any key or Fn combination. To use your custom Fn key, hold it while you press the other key.";
            CaptureAction.Visibility = Visibility.Collapsed;
        }
        else
        {
            KeyEmpty.Visibility = Visibility.Collapsed;
            KeyCaptured.Visibility = Visibility.Visible;
            KeyCaps.ItemsSource = Keycaps.For(trigger);
            string? from = trigger.KeyboardName ?? trigger.Device;
            KeyDetail.Text = string.Join("   ·   ", new[] { trigger.Signal, from is null ? "" : $"from {from}" }.Where(s => s.Length > 0));
            CaptureAction.Text = "Capture again";
            CaptureAction.Visibility = Visibility.Visible;
        }
    }

    void SetTrigger(Trigger t, bool fromSaved = false)
    {
        trigger = t;
        ShowKey();
        // A new capture is limited to the keyboard it came from only when there's more than one to choose between.
        string? keyboard = fromSaved || app.Engine.Keyboards.Count(k => !k.IsReceiver) > 1 ? t.Keyboard : null;
        PopulateKeyboards(t, keyboard);
        UpdateWarning();
        UpdateBlockOption();
        UpdateNamePlaceholder();
    }

    void ShowWarning(string text)
    {
        WarningText.Text = text;
        WarningBar.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ keyboard

    void PopulateKeyboards(Trigger? t, string? select)
    {
        loadingKeyboards = true;
        var engine = app.Engine;
        var items = new List<KeyboardChoice>();
        bool hid = t?.Kind == TriggerKind.Hid;
        bool customFn = t is { Kind: TriggerKind.Key } && (t.Modifiers & KeyNames.Fn) != 0;

        if (hid)
        {
            string name = t!.Keyboard is { } id ? engine.NameOf(id, t.KeyboardName ?? t.Device) : t.Device ?? "This device";
            items.Add(new KeyboardChoice(t.Keyboard, name, name));
        }
        else
        {
            items.Add(new KeyboardChoice(null, "Any keyboard", "Any keyboard"));
            if (!customFn)
            {
                items.AddRange(engine.Keyboards.Select(k => new KeyboardChoice(k.Id, engine.NameOf(k), $"{engine.NameOf(k)}   ({k.Connection})")));
                if (select is not null && !items.Any(c => SameId(c.Id, select)))
                {
                    string name = engine.NameOf(select, t?.KeyboardName);
                    items.Add(new KeyboardChoice(select, name, $"{name}   (not connected)"));
                }
            }
        }

        KeyboardBox.ItemsSource = items;
        KeyboardBox.SelectedItem = items.FirstOrDefault(c => SameId(c.Id, select)) ?? items[0];
        KeyboardBox.IsEnabled = !hid && !customFn;
        KeyboardHint.Text = hid ? "This signal only comes from this device."
            : customFn ? "Custom Fn shortcuts work on every keyboard."
            : "";
        KeyboardHint.Visibility = KeyboardHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        loadingKeyboards = false;
    }

    static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void KeyboardBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingKeyboards || trigger is null) return;
        UpdateWarning();
        UpdateBlockOption();
    }

    /// <summary>The keyboard a key shortcut is limited to (null = any). HID signals always belong to their device.</summary>
    string? SelectedKeyboard => trigger?.Kind == TriggerKind.Key ? (KeyboardBox.SelectedItem as KeyboardChoice)?.Id : null;

    /// <summary>The captured trigger, limited to the chosen keyboard.</summary>
    Trigger EffectiveTrigger()
    {
        var t = trigger!;
        if (t.Kind == TriggerKind.Hid) return t;
        var choice = KeyboardBox.SelectedItem as KeyboardChoice;
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
            ? $"This key already has a shortcut for this keyboard (“{clash.Action.Describe()}”). Saving will replace it."
            : WarningFor(trigger);
        if (message is null) WarningBar.Visibility = Visibility.Collapsed;
        else ShowWarning(message);
    }

    static string? WarningFor(Trigger t)
    {
        if (t.Kind == TriggerKind.Hid)
            return t.UsagePage == 0x01
                ? "This is a system power signal. Windows will still do its own thing (like going to sleep), and that part can't be switched off."
                : null;
        if (KeyNames.IsSpecialKey(t.Vk) || (t.Modifiers & KeyNames.Fn) != 0) return null;
        string key = KeyNames.Vk(t.Vk);
        return t.Modifiers == 0
            ? $"This sends an ordinary {key} key, so the shortcut also runs when you press {key} without Fn. " +
              "If your keyboard has an Fn-lock (often Fn + Esc), switching it may make this key send a media signal instead."
            : $"This sends the ordinary combination {t.Description}, so the shortcut also runs whenever you press {t.Description} yourself.";
    }

    void UpdateBlockOption()
    {
        bool isKey = trigger is null || trigger.Kind == TriggerKind.Key;
        // Windows names the keyboard of an ordinary key only when it's released (media keys carry their own signal).
        bool onRelease = trigger is { Kind: TriggerKind.Key } t && SelectedKeyboard is not null && !KeyNames.HasMediaSignal(t.Vk);
        bool wasEnabled = BlockBox.IsEnabled;
        BlockBox.IsEnabled = isKey;
        if (!isKey) BlockBox.IsChecked = false;
        else if (!wasEnabled) BlockBox.IsChecked = true;
        BlockHint.Text = !isKey
            ? "Not needed: Windows doesn't use this signal, so there's nothing to switch off."
            : onRelease
                ? "On one keyboard only, the shortcut runs when you let go of the key. Other keyboards keep the key's normal job."
                : "Recommended. Otherwise the key does its usual thing as well as your shortcut.";
    }

    // ------------------------------------------------------------------ action

    void ActionTile_Checked(object sender, RoutedEventArgs e)
    {
        if (tiles is null) return; // during InitializeComponent
        foreach (var (tile, (_, panel)) in tiles) panel.Visibility = tile.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateNamePlaceholder();
    }

    void SelectAction(ActionType type)
    {
        foreach (var (tile, (tileType, _)) in tiles)
            if (tileType == type) tile.IsChecked = true;
    }

    ActionType SelectedAction => tiles.FirstOrDefault(t => t.Key.IsChecked == true).Value.Type;

    void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an app or file",
            Filter = "Apps and shortcuts (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) TargetBox.Text = dialog.FileName;
    }

    void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to open" };
        if (dialog.ShowDialog(this) == true) TargetBox.Text = dialog.FolderName;
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

    // ------------------------------------------------------------------ key combination recorder

    static int CurrentModifiers()
    {
        var m = Keyboard.Modifiers;
        int mods = (m.HasFlag(ModifierKeys.Control) ? KeyNames.Ctrl : 0)
                   | (m.HasFlag(ModifierKeys.Shift) ? KeyNames.Shift : 0)
                   | (m.HasFlag(ModifierKeys.Alt) ? KeyNames.Alt : 0);
        if (m.HasFlag(ModifierKeys.Windows) || (Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0)
            mods |= KeyNames.Win;
        return mods;
    }

    static int VirtualKey(KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        return KeyInterop.VirtualKeyFromKey(key);
    }

    void RecordBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int vk = VirtualKey(e);
        int mods = CurrentModifiers();
        if (vk == 0x09 && mods == 0) return; // plain Tab moves on; pick Tab from the list if you need it
        e.Handled = true;
        if (vk == 0) return;
        if (KeyNames.IsModifier(vk))
        {
            RecordBox.Text = KeyNames.ModText(mods | KeyNames.ModFlag(vk)) + "…";
            return;
        }
        RecordKey(vk, mods);
    }

    void RecordBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Print Screen never sends a key-down to apps.
        if (VirtualKey(e) == 0x2C) RecordKey(0x2C, CurrentModifiers());
    }

    void RecordKey(int vk, int mods)
    {
        syncingShortcut = true;
        CtrlBox.IsChecked = (mods & KeyNames.Ctrl) != 0;
        ShiftBox.IsChecked = (mods & KeyNames.Shift) != 0;
        AltBox.IsChecked = (mods & KeyNames.Alt) != 0;
        WinBox.IsChecked = (mods & KeyNames.Win) != 0;
        var item = KeyBox.Items.Cast<KeyItem>().FirstOrDefault(k => k.Vk == vk);
        if (item is null)
        {
            item = new KeyItem(vk, KeyNames.Vk(vk));
            KeyBox.Items.Add(item);
        }
        KeyBox.SelectedItem = item;
        syncingShortcut = false;
        SyncShortcutText();
    }

    int ShortcutMods =>
        (CtrlBox.IsChecked == true ? KeyNames.Ctrl : 0) | (ShiftBox.IsChecked == true ? KeyNames.Shift : 0) |
        (AltBox.IsChecked == true ? KeyNames.Alt : 0) | (WinBox.IsChecked == true ? KeyNames.Win : 0);

    void ShortcutPart_Changed(object sender, RoutedEventArgs e) => SyncShortcutText();

    void SyncShortcutText()
    {
        if (syncingShortcut || RecordBox is null) return;
        RecordBox.Text = KeyBox.SelectedItem is KeyItem key ? KeyNames.Combo(ShortcutMods, key.Vk) : "Click here, then press the keys";
        UpdateNamePlaceholder();
    }

    // ------------------------------------------------------------------ name

    void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NamePlaceholder.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdateNamePlaceholder()
    {
        if (NamePlaceholder is null) return;
        NamePlaceholder.Text = trigger is null
            ? "Shown on the shortcut's card"
            : $"For example: {trigger.Description} for work";
    }

    // ------------------------------------------------------------------ load / save

    void LoadExisting(Mapping m)
    {
        NameBox.Text = m.Name == m.Trigger.Description ? "" : m.Name;
        var a = m.Action;
        SelectAction(a.Type);
        TargetBox.Text = a.Target ?? "";
        ArgsBox.Text = a.Arguments ?? "";
        TypedTextBox.Text = a.Text ?? "";
        CommandBox.Text = a.Command ?? "";
        HideBox.IsChecked = a.HideWindow;
        BuiltInBox.SelectedItem = BuiltInBox.Items.Cast<BuiltInItem>().FirstOrDefault(b => b.Action == a.BuiltIn) ?? BuiltInBox.Items[0];
        if (a.ShortcutVk != 0) RecordKey(a.ShortcutVk, a.ShortcutMods);
        SetTrigger(m.Trigger, fromSaved: true);
        if (BlockBox.IsEnabled) BlockBox.IsChecked = m.BlockOriginal;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (capturing)
        {
            Fail("Finish capturing the key first, or click Cancel in the key box.", CaptureButton);
            return;
        }
        if (trigger is null)
        {
            Fail("Pick a key first: click the key box and press the key.", CaptureButton);
            return;
        }

        var action = new ActionSpec { Type = SelectedAction };
        switch (action.Type)
        {
            case ActionType.OpenTarget:
                if (string.IsNullOrWhiteSpace(TargetBox.Text)) { Fail("Enter an app, file, folder or web address to open.", TargetBox); return; }
                action.Target = TargetBox.Text.Trim();
                action.Arguments = ArgsBox.Text.Trim();
                break;
            case ActionType.SendShortcut:
                if (KeyBox.SelectedItem is not KeyItem key) { Fail("Choose the key combination to press.", RecordBox); return; }
                action.ShortcutVk = key.Vk;
                action.ShortcutMods = ShortcutMods;
                break;
            case ActionType.TypeText:
                if (TypedTextBox.Text.Length == 0) { Fail("Enter the text to type.", TypedTextBox); return; }
                action.Text = TypedTextBox.Text;
                break;
            case ActionType.BuiltIn:
                action.BuiltIn = (BuiltInBox.SelectedItem as BuiltInItem)?.Action ?? BuiltInAction.PlayPause;
                break;
            case ActionType.RunCommand:
                if (string.IsNullOrWhiteSpace(CommandBox.Text)) { Fail("Enter a command to run.", CommandBox); return; }
                action.Command = CommandBox.Text.Trim();
                action.HideWindow = HideBox.IsChecked == true;
                break;
        }

        var effective = EffectiveTrigger();
        Result = new Mapping
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? effective.Description : NameBox.Text.Trim(),
            Enabled = existing?.Enabled ?? true,
            BlockOriginal = effective.Kind == TriggerKind.Key && BlockBox.IsEnabled && BlockBox.IsChecked == true,
            Trigger = effective,
            Action = action,
        };
        DialogResult = true;
    }

    void Fail(string message, FrameworkElement focus)
    {
        ErrorText.Text = message;
        focus.BringIntoView();
        focus.Focus();
    }
}
