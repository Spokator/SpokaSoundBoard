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
        // A NaN that slipped into a setting must neither make every save fail nor reset the whole file on load:
        // it is written as "NaN", read back, then replaced by Sanitize.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
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
        s.Voice.Sanitize();
        s.Mix ??= new MixSettings();
        s.Mix.Sanitize();
        s.Appearance ??= new AppearanceSettings();
        s.Appearance.UiScale = Math.Clamp(s.Appearance.UiScale, 0.8, 1.4);
        s.Hotkeys ??= new HotkeySettings();
        s.Hotkeys.Sanitize();
        s.Pads ??= [];
        s.Playlist ??= [];
        s.Music ??= new MusicSettings();
        s.Music.Effects ??= new MusicEffectSettings();
        s.Music.Effects.Sanitize();
        s.Music.CrossfadeSeconds = Math.Clamp(s.Music.CrossfadeSeconds, 0, 12);
        s.BlindTest ??= new BlindTestSettings();
        s.BlindTest.Sanitize();
        s.TextToSpeech ??= new TextToSpeechSettings();
        s.TextToSpeech.Sanitize();
        s.CustomPresets ??= [];
        s.CustomPresets.RemoveAll(p => p is null);
        foreach (var preset in s.CustomPresets)
        {
            preset.Settings ??= new VoiceEffectSettings();
            preset.Settings.Sanitize();
        }
        for (var i = s.Pads.Count - 1; i >= 0; i--)
            if (s.Pads[i] is null) s.Pads.RemoveAt(i);
        foreach (var pad in s.Pads) pad.Hotkey ??= new HotkeyBinding();
        return s;
    }
}
