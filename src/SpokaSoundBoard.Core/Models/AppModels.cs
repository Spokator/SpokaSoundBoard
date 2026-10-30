using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SpokaSoundBoard.Core.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>A keyboard key or mouse button (VK_MBUTTON / VK_XBUTTON1 / VK_XBUTTON2) with optional modifiers.</summary>
public partial class HotkeyBinding : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display), nameof(IsSet))]
    private int _virtualKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private HotkeyModifiers _modifiers;

    [JsonIgnore]
    public bool IsSet => VirtualKey != 0;

    [JsonIgnore]
    public string Display => IsSet ? KeyNames.Format(VirtualKey, Modifiers) : "Aucun";

    public void Set(int virtualKey, HotkeyModifiers modifiers)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public void Clear() => Set(0, HotkeyModifiers.None);
}

public partial class SoundPad : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private float _volume = 1f;
    [ObservableProperty] private string _color = "#7C5CFF";
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private HotkeyBinding _hotkey = new();

    /// <summary>Web page the sound was imported from (YouTube, MyInstants…), if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string? _sourceUrl;

    /// <summary>Playback window inside the file, in seconds (EndSeconds 0 = until the end).</summary>
    [ObservableProperty] private double _startSeconds;
    [ObservableProperty] private double _endSeconds;

    [JsonIgnore]
    public bool HasSource => !string.IsNullOrEmpty(SourceUrl);

    [JsonIgnore]
    [ObservableProperty] private bool _isPlaying;
}

public partial class TrackItem : ObservableObject
{
    [ObservableProperty] private string _filePath = "";

    /// <summary>Title given by the web source; local files use their file name.</summary>
    public string? DisplayTitle { get; set; }

    public string? SourceUrl { get; set; }

    [JsonIgnore]
    [ObservableProperty] private bool _isCurrent;

    [JsonIgnore]
    public bool HasSource => !string.IsNullOrEmpty(SourceUrl);

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(DisplayTitle) ? Path.GetFileNameWithoutExtension(FilePath) : DisplayTitle;

    [JsonIgnore]
    public string Folder => HasSource && Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
        ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
        : Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? "";
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

public class HotkeySettings
{
    public HotkeyBinding PushToTalk { get; set; } = new();
    public HotkeyBinding PlayPause { get; set; } = new();
    public HotkeyBinding NextTrack { get; set; } = new();
    public HotkeyBinding StopAll { get; set; } = new();
    public HotkeyBinding ToggleMic { get; set; } = new();
    public HotkeyBinding ToggleEffects { get; set; } = new();
    public HotkeyBinding ToggleMediaToMic { get; set; } = new();
    public HotkeyBinding ToggleDiscordShare { get; set; } = new();
}

public class AppSettings
{
    public string? MicDeviceId { get; set; }
    public string? VirtualOutputDeviceId { get; set; }

    /// <summary>Second virtual cable used as Discord's microphone (optional).</summary>
    public string? DiscordOutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }

    public VoiceEffectSettings Voice { get; set; } = new();
    public MixSettings Mix { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    public ObservableCollection<SoundPad> Pads { get; set; } = [];
    public ObservableCollection<TrackItem> Playlist { get; set; } = [];
    public List<EffectPreset> CustomPresets { get; set; } = [];

    public string? SelectedPresetName { get; set; } = "Voix normale";
    public RepeatMode Repeat { get; set; } = RepeatMode.All;
    public bool Shuffle { get; set; }
    public bool FirstRunDone { get; set; }
}
