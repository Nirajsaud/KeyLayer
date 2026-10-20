using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace KeyLayer;

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name!);
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Segoe Fluent Icons glyphs (they also exist in Segoe MDL2 Assets on Windows 10).</summary>
internal static class Glyphs
{
    public const string Shortcuts = "";
    public const string Keyboard = "";
    public const string Laptop = "";
    public const string Receiver = "";
    public const string Test = "";
    public const string Settings = "";
    public const string Arrow = "";
    public const string Open = "";
    public const string Text = "";
    public const string Media = "";
    public const string Command = "";
    public const string Off = "";

    public static string ForAction(ActionType type) => type switch
    {
        ActionType.OpenTarget => Open,
        ActionType.SendShortcut => Keyboard,
        ActionType.TypeText => Text,
        ActionType.BuiltIn => Media,
        ActionType.RunCommand => Command,
        _ => Off,
    };

    public static string ForKeyboard(KeyboardInfo k) => !k.External ? Laptop : k.IsReceiver ? Receiver : Keyboard;
}

internal static class Keycaps
{
    /// <summary>A trigger's keys as keycap labels, e.g. ["Ctrl", "F9"], or one label for a HID signal.</summary>
    public static IReadOnlyList<string> For(Trigger t) =>
        t.Kind == TriggerKind.Hid ? [t.Description] : For(t.Modifiers, t.Vk);

    public static IReadOnlyList<string> For(int mods, int vk)
    {
        var keys = new List<string>(5);
        if ((mods & KeyNames.Fn) != 0) keys.Add(KeyNames.CustomFnName);
        if ((mods & KeyNames.Ctrl) != 0) keys.Add("Ctrl");
        if ((mods & KeyNames.Shift) != 0) keys.Add("Shift");
        if ((mods & KeyNames.Alt) != 0) keys.Add("Alt");
        if ((mods & KeyNames.Win) != 0) keys.Add("Win");
        keys.Add(KeyNames.Vk(vk));
        return keys;
    }
}

/// <summary>One shortcut card on the Shortcuts page.</summary>
internal sealed class ShortcutItem : Observable
{
    readonly App app;

    public ShortcutItem(App app, Mapping mapping, string keyboardText)
    {
        this.app = app;
        Mapping = mapping;
        Keys = Keycaps.For(mapping.Trigger);
        ActionGlyph = Glyphs.ForAction(mapping.Action.Type);
        ActionText = mapping.Action.Describe();
        KeyboardText = keyboardText;

        // The user's own name, if they gave one, and a note when the key also keeps doing its normal job.
        var parts = new List<string>(2);
        if (mapping.Name.Length > 0 && mapping.Name != mapping.Trigger.Description && mapping.Name != ActionText)
            parts.Add(mapping.Name);
        if (mapping.Trigger.Kind == TriggerKind.Key && !mapping.BlockOriginal)
            parts.Add("The key also does its normal job");
        if (mapping.ExcludedApps.Count > 0)
            parts.Add("Off in " + string.Join(", ", mapping.ExcludedApps.Select(a => a.Name.Length > 0 ? a.Name : a.Exe)));
        Details = string.Join("  ·  ", parts);
        if (mapping.Trigger.Kind == TriggerKind.Key && (mapping.Trigger.Modifiers & KeyNames.Fn) != 0 && KeyNames.CustomFnVk == 0)
            Warning = "Can't run: the custom Fn key is off. Pick one in Settings.";
    }

    public Mapping Mapping { get; }
    public IReadOnlyList<string> Keys { get; }
    public string ActionGlyph { get; }
    public string ActionText { get; }
    public string KeyboardText { get; }
    public string Details { get; }

    /// <summary>Why the shortcut can't run right now, or "".</summary>
    public string Warning { get; } = "";

    public bool Enabled
    {
        get => Mapping.Enabled;
        set
        {
            if (Mapping.Enabled == value) return;
            Mapping.Enabled = value;
            app.SaveConfig();
            Raise(nameof(Enabled));
        }
    }
}

/// <summary>One keyboard card on the Keyboards page.</summary>
internal sealed class KeyboardItem(KeyboardInfo info) : Observable
{
    string name = "";
    string details = "";
    string lastUsed = "";
    bool active;

    public KeyboardInfo Info { get; } = info;
    public string Glyph { get; } = Glyphs.ForKeyboard(info);
    public string Name { get => name; set => Set(ref name, value); }
    public string Details { get => details; set => Set(ref details, value); }
    public string LastUsed { get => lastUsed; set => Set(ref lastUsed, value); }

    /// <summary>True for a moment after the keyboard was typed on.</summary>
    public bool Active { get => active; set => Set(ref active, value); }
}

/// <summary>One row on the Test keys page.</summary>
internal sealed class EventItem(ObservedInput observed, string? mappedTo)
{
    public ObservedInput Observed { get; } = observed;
    public IReadOnlyList<string> Keys { get; } = Keycaps.For(observed.Trigger);
    public string Time { get; } = observed.Time.ToString("HH:mm:ss");
    public string From { get; } = observed.Device;
    public string Code { get; } = observed.Signal;
    public string Kind { get; } = observed.Source == "Signal" ? "Media / special signal" : "Key";
    public string? MappedTo { get; } = mappedTo;
    public bool IsMapped => MappedTo is not null;
}

/// <summary>A keyboard filter pill on the Shortcuts page (null id = all shortcuts).</summary>
internal sealed record FilterItem(string? Id, string Label);

/// <summary>One app in an app list or in the app picker.</summary>
internal sealed class AppRow(AppRef app, ImageSource? icon)
{
    public AppRef App { get; } = app;
    public string Name => App.Name.Length > 0 ? App.Name : App.Exe;
    public string Exe => App.Exe;
    public ImageSource? Icon { get; } = icon;
    public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string RemoveLabel => $"Remove {Name}";
}
