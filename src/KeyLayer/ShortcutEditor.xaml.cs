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
    bool justPressed; // the key was pressed in this editor (not loaded from a saved shortcut)
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
            [TileHold] = (ActionType.HoldKey, PanelShortcut),
            [TileText] = (ActionType.TypeText, PanelText),
            [TileMedia] = (ActionType.BuiltIn, PanelMedia),
            [TileCommand] = (ActionType.RunCommand, PanelCommand),
            [TileOff] = (ActionType.Disable, PanelOff),
        };
        BuiltInBox.ItemsSource = ActionRunner.BuiltIns.Select(b => new BuiltInItem(b.Action, b.Name)).ToList();
        foreach (var key in ShortcutKeys()) KeyBox.Items.Add(key);
        RecordBox.Text = "Click here, then press the keys";
        ExcludedList.Apps = [.. existing?.ExcludedApps ?? []];
        if (app.Config.ExcludedApps.Count > 0)
        {
            AllAppsHint.Text = "Every shortcut is already off in " +
                               string.Join(", ", app.Config.ExcludedApps.Select(a => a.Name.Length > 0 ? a.Name : a.Exe)) + " (see Settings).";
            AllAppsHint.Visibility = Visibility.Visible;
        }

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
        UpdateBlockOption();
        UpdateHoldOptions();
        TypeMethodBox_SelectionChanged(TypeMethodBox, null!); // its hint wasn't there yet when the box was filled
        Loaded += (_, _) => { if (trigger is null) CaptureButton.Focus(); };

        // While waiting for a key, keep it from also pressing buttons here (Enter = Save, Esc = Cancel, ...).
        PreviewKeyDown += (_, e) => { if (capturing) e.Handled = true; };
        PreviewKeyUp += (_, e) => { if (capturing) e.Handled = true; };

        // Typing here (say, recording Ctrl + S as the combination to press) shouldn't run your shortcuts.
        app.Engine.Editing = true;
        Closed += (_, _) => app.Engine.Editing = false;
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
            KeyPromptDetail.Text = app.Config.CustomFnVk != 0
                ? $"For Fn-style shortcuts, hold {KeyNames.CustomFnName} (your KeyLayer Fn key) while you press a key. " +
                  "Your keyboard's own Fn key only works with keys that have a special job, like volume or Mail."
                : "Your keyboard's own Fn key only works with keys that have a special job, like volume or Mail. " +
                  "For other Fn-style shortcuts, set up a KeyLayer Fn key in Settings.";
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
        justPressed = !fromSaved;
        ShowKey();
        // A new capture is limited to the keyboard it came from only when there's more than one to choose between.
        string? keyboard = fromSaved || app.Engine.Keyboards.Count(k => !k.IsReceiver) > 1 ? t.Keyboard : null;
        PopulateKeyboards(t, keyboard);
        UpdateWarning();
        UpdateClash();
        UpdateBlockOption();
        UpdateHoldOptions();
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
            : customFn ? "KeyLayer Fn shortcuts work on every keyboard."
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
        UpdateHoldOptions();
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
            : WarningFor(trigger, fnOff: app.Config.CustomFnVk == 0);
        if (message is null) WarningBar.Visibility = Visibility.Collapsed;
        else ShowWarning(message);
    }

    static string? WarningFor(Trigger t, bool fnOff) =>
        t is { Kind: TriggerKind.Hid, UsagePage: 0x01 }
            ? "This is a system power signal. Windows will still do its own thing (like going to sleep), and that part can't be switched off."
            : t.Kind == TriggerKind.Key && (t.Modifiers & KeyNames.Fn) != 0 && fnOff
                ? "The KeyLayer Fn key is off, so this shortcut can't run. Pick one in Settings, or capture a different key."
                : null;

    /// <summary>Says whether the key takes over a shortcut Windows or other apps use, and offers a Fn-key combination instead.</summary>
    void UpdateClash()
    {
        if (trigger is null)
        {
            ClashBar.Visibility = Visibility.Collapsed;
            return;
        }

        var clash = ClashCheck.For(trigger);
        // People hold their keyboard's Fn key and press a letter, but only the letter arrives: say so.
        if (justPressed && trigger is { Kind: TriggerKind.Key, Modifiers: 0 } plain
            && !KeyNames.IsSpecialKey(plain.Vk) && !KeyNames.IsModifier(plain.Vk))
        {
            string key = KeyNames.Vk(plain.Vk);
            bool offer = ClashCheck.FnAlternative(plain, app.Config.CustomFnVk) is not null;
            clash = new ClashInfo(ClashLevel.MightClash, "Did you hold Fn?",
                $"Keyboards keep their Fn key to themselves, so only {key} arrived, and this shortcut would run every time " +
                $"you press {key}." + (offer ? " For an Fn-style shortcut, use your KeyLayer Fn key instead:" : ""));
        }
        ClashTitle.Text = clash.Title;
        ClashText.Text = clash.Text;
        var (background, foreground, glyph) = clash.Level switch
        {
            ClashLevel.Safe => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush", ""),
            ClashLevel.Windows => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush", ""),
            _ => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush", ""),
        };
        ClashBar.SetResourceReference(Border.BackgroundProperty, background);
        ClashIcon.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        ClashIcon.Text = glyph;
        ClashBar.Visibility = Visibility.Visible;

        if (clash.Level != ClashLevel.Safe && ClashCheck.FnAlternative(trigger, app.Config.CustomFnVk) is { } vk)
        {
            bool setUp = app.Config.CustomFnVk != 0;
            string fn = setUp ? KeyNames.CustomFnName : KeyNames.Vk(KeyNames.SuggestedFnVk), key = KeyNames.Vk(vk);
            UseFnText.Text = $"Use {fn} + {key} instead";
            UseFnHint.Text = setUp
                ? $"Hold {fn}, your KeyLayer Fn key, and press {key}. No app uses that, so it never clashes."
                : $"This makes {fn} your KeyLayer Fn key: hold it and press {key}. No app uses that, so it never clashes. " +
                  $"Pressed on its own, {fn} still works as normal. You can change this in Settings.";
            FnSuggestion.Visibility = Visibility.Visible;
        }
        else
        {
            FnSuggestion.Visibility = Visibility.Collapsed;
        }
    }

    void UseFn_Click(object sender, RoutedEventArgs e)
    {
        if (trigger is null || ClashCheck.FnAlternative(trigger, app.Config.CustomFnVk) is not { } vk) return;
        if (app.Config.CustomFnVk == 0)
        {
            app.Config.CustomFnVk = KeyNames.SuggestedFnVk;
            app.SaveConfig();
        }
        SetTrigger(Trigger.ForKey(vk, KeyNames.Fn));
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

    /// <summary>
    /// A shortcut on an ordinary key limited to one keyboard only runs when the key is released (that's when Windows
    /// says which keyboard it was), so it can neither repeat nor be held.
    /// </summary>
    bool RunsOnRelease => trigger is { Kind: TriggerKind.Key } t && SelectedKeyboard is not null && !KeyNames.HasMediaSignal(t.Vk);

    /// <summary>The "Repeat while held" switch, and the hint under "Act as another key".</summary>
    void UpdateHoldOptions()
    {
        if (RepeatBox is null || tiles is null) return;
        bool hold = SelectedAction == ActionType.HoldKey;
        RepeatBox.IsEnabled = !hold && !RunsOnRelease;
        if (!RepeatBox.IsEnabled) RepeatBox.IsChecked = false;
        RepeatHint.Text = hold ? "Not needed: the key is held down for as long as you hold yours."
            : RunsOnRelease ? "On one keyboard only, an ordinary key runs when you let go, so it can't repeat."
            : "Holding the key runs it again and again, like holding a volume key.";

        ShortcutLabel.Text = hold ? "Key to act as" : "Key combination to press";
        RecordBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, ShortcutLabel.Text);
        ShortcutHint.Text = !hold ? "Click the box and press the keys, or build it below."
            : RunsOnRelease ? "On one keyboard only, an ordinary key can't be held: it presses this key once when you let go. Pick Any keyboard to hold it."
            : "It's held down for as long as you hold your key. To make it act as Ctrl, Shift, Alt or Win, press just that key, or pick it from the list.";
    }

    void TypeMethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeMethodHint is null) return; // during InitializeComponent
        TypeMethodHint.Text = TypeMethodBox.SelectedIndex switch
        {
            1 => "Presses real keys, a little slower. For games and remote desktop windows that ignore typed text. " +
                 "Characters your keyboard can't type are typed directly.",
            2 => "Puts the text on the clipboard, presses Ctrl + V, then puts your clipboard back. " +
                 "The text isn't kept in clipboard history.",
            _ => "Types the characters directly. Works in almost every app.",
        };
    }

    // ------------------------------------------------------------------ action

    void ActionTile_Checked(object sender, RoutedEventArgs e)
    {
        if (tiles is null) return; // during InitializeComponent
        var shown = tiles.FirstOrDefault(t => t.Key.IsChecked == true).Value.Panel; // two tiles share a panel
        foreach (var (_, (_, panel)) in tiles) panel.Visibility = panel == shown ? Visibility.Visible : Visibility.Collapsed;
        UpdateHoldOptions();
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
        foreach (int vk in (int[])[0xA2, 0xA3, 0xA0, 0xA1, 0xA4, 0xA5, 0x5B, 0x5C]) yield return new KeyItem(vk, KeyNames.Vk(vk));
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
            loneModifier = vk;
            return;
        }
        loneModifier = 0;
        RecordKey(vk, mods);
    }

    void RecordBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        int vk = VirtualKey(e);
        // Print Screen never sends a key-down to apps.
        if (vk == 0x2C) RecordKey(0x2C, CurrentModifiers());
        // A modifier pressed and let go on its own is the key itself (e.g. act as Ctrl).
        else if (vk != 0 && vk == loneModifier)
        {
            loneModifier = 0;
            RecordKey(vk, CurrentModifiers() & ~KeyNames.ModFlag(vk));
        }
    }

    int loneModifier;

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
        TypeMethodBox.SelectedIndex = (int)a.TypeMethod;
        CommandBox.Text = a.Command ?? "";
        HideBox.IsChecked = a.HideWindow;
        BuiltInBox.SelectedItem = BuiltInBox.Items.Cast<BuiltInItem>().FirstOrDefault(b => b.Action == a.BuiltIn) ?? BuiltInBox.Items[0];
        if (a.ShortcutVk != 0) RecordKey(a.ShortcutVk, a.ShortcutMods);
        SetTrigger(m.Trigger, fromSaved: true);
        if (BlockBox.IsEnabled) BlockBox.IsChecked = m.BlockOriginal;
        if (RepeatBox.IsEnabled) RepeatBox.IsChecked = m.RepeatWhileHeld;
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
            case ActionType.SendShortcut or ActionType.HoldKey:
                if (KeyBox.SelectedItem is not KeyItem key)
                {
                    Fail(action.Type == ActionType.HoldKey ? "Choose the key to act as." : "Choose the key combination to press.", RecordBox);
                    return;
                }
                action.ShortcutVk = key.Vk;
                action.ShortcutMods = ShortcutMods & ~KeyNames.ModFlag(key.Vk);
                break;
            case ActionType.TypeText:
                if (TypedTextBox.Text.Length == 0) { Fail("Enter the text to type.", TypedTextBox); return; }
                action.Text = TypedTextBox.Text;
                action.TypeMethod = (TypeMethod)Math.Max(0, TypeMethodBox.SelectedIndex);
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
            RepeatWhileHeld = RepeatBox.IsEnabled && RepeatBox.IsChecked == true,
            Trigger = effective,
            Action = action,
            ExcludedApps = ExcludedList.Apps,
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
