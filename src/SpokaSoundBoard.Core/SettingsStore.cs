using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? AppEnvironment.DataDirectory;
        FilePath = Path.Combine(Directory, "settings.json");
    }

    public string Directory { get; }
    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings != null) return Sanitize(settings);
            }
        }
        catch
        {
            // Corrupted file: keep a copy and start fresh.
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { /* ignore */ }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(settings, Options);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, true);
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static AppSettings Deserialize(string json) =>
        Sanitize(JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings());

    private static AppSettings Sanitize(AppSettings s)
    {
        s.Voice ??= new VoiceEffectSettings();
        s.Mix ??= new MixSettings();
        s.Appearance ??= new AppearanceSettings();
        s.Appearance.UiScale = Math.Clamp(s.Appearance.UiScale, 0.8, 1.4);
        s.Hotkeys ??= new HotkeySettings();
        s.Hotkeys.ToggleDiscordShare ??= new HotkeyBinding();
        s.Hotkeys.NextPreset ??= new HotkeyBinding();
        s.Hotkeys.PreviousPreset ??= new HotkeyBinding();
        s.Pads ??= [];
        s.Playlist ??= [];
        s.Music ??= new MusicSettings();
        s.Music.Effects ??= new MusicEffectSettings();
        s.Music.CrossfadeSeconds = Math.Clamp(s.Music.CrossfadeSeconds, 0, 12);
        s.CustomPresets ??= [];
        foreach (var pad in s.Pads) pad.Hotkey ??= new HotkeyBinding();
        return s;
    }
}
