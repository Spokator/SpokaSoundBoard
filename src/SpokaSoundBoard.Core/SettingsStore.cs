using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>French message when the last <see cref="Load"/> could not read everything (null when all went well).</summary>
    public string? LoadProblem { get; private set; }

    public AppSettings Load()
    {
        LoadProblem = null;
        string json;
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex)
        {
            LoadProblem = "Vos réglages n'ont pas pu être lus (" + FriendlyErrors.Describe(ex) + "). Réglages par défaut utilisés.";
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings != null) return Sanitize(settings);
        }
        catch (Exception)
        {
            // Fall through: one bad value (an unknown enum after a downgrade…) must not cost the whole soundboard.
        }

        var backup = KeepBackup();
        var recovered = Recover(json, out var lost);
        LoadProblem = recovered == null
            ? "Vos réglages étaient illisibles : réglages par défaut utilisés."
            : lost.Count == 0 ? null
            : "Une partie de vos réglages était abîmée (" + string.Join(", ", lost) + ") et a été remise par défaut.";
        if (LoadProblem != null && backup != null) LoadProblem += " Copie de l'ancien fichier : " + Path.GetFileName(backup) + ".";
        return recovered ?? new AppSettings();
    }

    /// <summary>
    /// Reads every top-level section on its own and keeps those that are valid, so a broken voice setting does not
    /// erase the pads. Returns null when the file is not even a JSON object.
    /// </summary>
    public static AppSettings? Recover(string json, out List<string> lostSections)
    {
        lostSections = [];
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
        if (root == null) return null;

        var settings = new AppSettings();
        foreach (var (name, node) in root)
        {
            var property = typeof(AppSettings).GetProperty(name);
            if (property is not { CanWrite: true }) continue;
            try
            {
                property.SetValue(settings, node?.Deserialize(property.PropertyType, Options));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                lostSections.Add(SectionName(name));
            }
        }
        return Sanitize(settings);
    }

    private static string SectionName(string property) => property switch
    {
        nameof(AppSettings.Voice) => "effets de voix",
        nameof(AppSettings.Mix) => "mixage",
        nameof(AppSettings.Pads) => "sons de la soundboard",
        nameof(AppSettings.Hotkeys) => "raccourcis",
        nameof(AppSettings.Music) => "musique",
        nameof(AppSettings.Appearance) => "apparence",
        nameof(AppSettings.BlindTest) => "blind test",
        nameof(AppSettings.TextToSpeech) => "synthèse vocale",
        nameof(AppSettings.CustomPresets) => "presets personnalisés",
        nameof(AppSettings.Remote) => "télécommande",
        nameof(AppSettings.GameProfiles) => "profils par jeu",
        _ => "autres réglages",
    };

    /// <summary>Copies the unreadable file next to it with the date, without overwriting earlier copies.</summary>
    private string? KeepBackup()
    {
        try
        {
            var backup = Path.Combine(Directory, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.bak.json");
            File.Copy(FilePath, backup, false);
            return backup;
        }
        catch (Exception)
        {
            return null;
        }
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
        s.PadCategories ??= [];
        s.Playlist ??= [];
        s.Music ??= new MusicSettings();
        s.Music.Effects ??= new MusicEffectSettings();
        s.Music.Effects.Sanitize();
        if (!Enum.IsDefined(s.Music.TrackSort)) s.Music.TrackSort = TrackSort.Default;
        s.Music.CrossfadeSeconds = Math.Clamp(s.Music.CrossfadeSeconds, 0, 12);
        s.BlindTest ??= new BlindTestSettings();
        s.BlindTest.Sanitize();
        s.TextToSpeech ??= new TextToSpeechSettings();
        s.TextToSpeech.Sanitize();
        s.Remote ??= new RemoteSettings();
        s.Remote.Sanitize();
        s.GameProfiles ??= [];
        foreach (var p in s.GameProfiles.Where(p => p == null).ToList()) s.GameProfiles.Remove(p);
        foreach (var p in s.GameProfiles) p.Sanitize();
        if (s.SelectedPresetName == BuiltInPresets.OldDarkLordName) s.SelectedPresetName = BuiltInPresets.DarkLordName;
        s.CustomPresets ??= [];
        s.CustomPresets.RemoveAll(p => p is null);
        foreach (var preset in s.CustomPresets)
        {
            preset.Settings ??= new VoiceEffectSettings();
            preset.Settings.Sanitize();
        }
        for (var i = s.Pads.Count - 1; i >= 0; i--)
            if (s.Pads[i] is null) s.Pads.RemoveAt(i);
        foreach (var pad in s.Pads)
        {
            pad.Hotkey ??= new HotkeyBinding();
            pad.Category = PadCategories.Clean(pad.Category);
            if (!Enum.IsDefined(pad.PlayMode)) pad.PlayMode = PadPlayMode.Toggle;
            pad.Icon = PadIcons.Clean(pad.Icon);
        }
        PadCategories.Merge(s.PadCategories, s.Pads);
        return s;
    }
}
