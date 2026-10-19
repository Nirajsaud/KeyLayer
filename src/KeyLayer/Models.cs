using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyLayer;

internal enum TriggerKind { Key, Hid }

/// <summary>Something the keyboard sends that a shortcut reacts to.</summary>
internal sealed class Trigger
{
    public TriggerKind Kind { get; set; }

    // Kind == Key: a virtual-key code seen by the keyboard hook, plus the modifiers held at the time.
    public int Vk { get; set; }
    public int Modifiers { get; set; }

    // Kind == Hid: a raw HID signal (consumer/system/vendor report), identified by device + usages or report bytes.
    public string? HidKey { get; set; }
    public int UsagePage { get; set; }

    public string Description { get; set; } = "";
    public string Signal { get; set; } = "";
    public string? Device { get; set; }

    /// <summary>
    /// The keyboard this shortcut is limited to (e.g. "1A2C:90FE"), or null for any keyboard.
    /// HID signals always belong to the device that sends them; for those this is informational.
    /// </summary>
    public string? Keyboard { get; set; }
    public string? KeyboardName { get; set; }

    [JsonIgnore]
    public string MatchKey => Kind == TriggerKind.Key ? KeyMatch(Vk, Modifiers, Keyboard) : "H:" + HidKey;

    public static string KeyMatch(int vk, int mods, string? keyboard = null) =>
        keyboard is null ? $"K:{vk:X2}:{mods}" : $"K:{vk:X2}:{mods}@{keyboard}";

    public static Trigger ForKey(int vk, int mods) => new()
    {
        Kind = TriggerKind.Key,
        Vk = vk,
        Modifiers = mods,
        Description = KeyNames.Combo(mods, vk),
        Signal = $"Key code 0x{vk:X2}",
    };
}

internal enum ActionType { OpenTarget, SendShortcut, TypeText, BuiltIn, RunCommand, Disable }

internal sealed class ActionSpec
{
    public ActionType Type { get; set; }
    public string? Target { get; set; }
    public string? Arguments { get; set; }
    public int ShortcutVk { get; set; }
    public int ShortcutMods { get; set; }
    public string? Text { get; set; }
    public BuiltInAction BuiltIn { get; set; }
    public string? Command { get; set; }
    public bool HideWindow { get; set; } = true;

    public string Describe() => Type switch
    {
        ActionType.OpenTarget => "Open " + Short(TargetName(Target)),
        ActionType.SendShortcut => "Press " + KeyNames.Combo(ShortcutMods, ShortcutVk),
        ActionType.TypeText => "Type \"" + Short(Text?.ReplaceLineEndings(" "), 32) + "\"",
        ActionType.BuiltIn => ActionRunner.Name(BuiltIn),
        ActionType.RunCommand => "Run " + Short(Command),
        _ => "Nothing (key switched off)",
    };

    static string TargetName(string? target)
    {
        target = target?.Trim().Trim('"') ?? "";
        if (target.Contains("://") || !target.Contains('\\')) return target;
        var name = Path.GetFileName(target.TrimEnd('\\'));
        return name.Length > 0 ? name : target;
    }

    static string Short(string? s, int max = 48)
    {
        s = s?.Trim() ?? "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

internal sealed class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool BlockOriginal { get; set; } = true;
    public Trigger Trigger { get; set; } = new();
    public ActionSpec Action { get; set; } = new();
}

internal sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<Mapping> Mappings { get; set; } = [];
    public bool TrayTipShown { get; set; }

    /// <summary>Key held as a home-made Fn key (0 = off).</summary>
    public int CustomFnVk { get; set; }

    /// <summary>Names the user gave keyboards, by keyboard ID.</summary>
    public Dictionary<string, string> KeyboardNames { get; set; } = [];
}

/// <summary>Loads and saves the config as JSON under %APPDATA%\KeyLayer.</summary>
internal sealed class ConfigStore
{
    static readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } =
        Environment.GetEnvironmentVariable("KEYLAYER_CONFIG") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyLayer", "config.json");

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), options) ?? new AppConfig();
        }
        catch (Exception)
        {
            // Keep the unreadable file around instead of silently losing it.
            try { File.Copy(FilePath, FilePath + ".unreadable", overwrite: true); } catch { }
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, options));
        File.Move(temp, FilePath, overwrite: true);
    }
}
