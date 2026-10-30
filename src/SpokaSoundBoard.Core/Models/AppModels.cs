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
    /// <summary>Automatic loudness correction in dB (null = not analysed yet).</summary>
    [ObservableProperty] private double? _normalizationDb;

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

    /// <summary>Automatic loudness correction in dB (null = not analysed yet).</summary>
    public double? NormalizationDb { get; set; }

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
    public HotkeyBinding NextPreset { get; set; } = new();
    public HotkeyBinding PreviousPreset { get; set; } = new();
}

public enum AppTheme
{
    Dark,
    Oled,
    Light,
}

public enum PadSize
{
    Small,
    Medium,
    Large,
}

public partial class AppearanceSettings : ObservableObject
{
    [ObservableProperty] private AppTheme _theme = AppTheme.Dark;

    /// <summary>Main accent color (#RRGGBB).</summary>
    [ObservableProperty] private string _accentColor = "#7C5CFF";

    /// <summary>Secondary accent used for values and icons (#RRGGBB).</summary>
    [ObservableProperty] private string _secondaryColor = "#22D3EE";

    /// <summary>Interface zoom (0.8 – 1.4).</summary>
    [ObservableProperty] private double _uiScale = 1.0;

    [ObservableProperty] private PadSize _padSize = PadSize.Medium;
    [ObservableProperty] private bool _showSidebarMeters = true;
    [ObservableProperty] private bool _showPadHotkeys = true;
}

public class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();

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

    /// <summary>The window close button hides the app in the notification area instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool TrayHintShown { get; set; }

    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Release tag the user chose to skip.</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>Assistant step to reopen after a restart (null = nothing pending).</summary>
    public int? SetupResumeStep { get; set; }

    /// <summary>The user explicitly removed the Discord output: do not auto-select a second cable.</summary>
    public bool DiscordOutputDisabled { get; set; }
}
