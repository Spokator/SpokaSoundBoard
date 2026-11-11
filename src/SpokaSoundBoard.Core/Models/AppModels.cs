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

/// <summary>What pressing a sound that is already playing does.</summary>
public enum PadPlayMode
{
    /// <summary>A second press stops it.</summary>
    Toggle,

    /// <summary>A second press starts it again from the beginning.</summary>
    Restart,

    /// <summary>Each press adds one more playback on top of the others.</summary>
    Overlap,
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

    /// <summary>Tab the sound is shown in ("" = no category, only under « Tous »).</summary>
    [ObservableProperty] private string _category = "";

    [ObservableProperty] private PadPlayMode _playMode;

    /// <summary>Starting this sound stops every other soundboard sound first.</summary>
    [ObservableProperty] private bool _stopOthers;

    /// <summary>Played in the headphones only, never in the virtual microphone (« Moi seulement »).</summary>
    [ObservableProperty] private bool _headphonesOnly;

    /// <summary>Web page the sound was imported from (YouTube, MyInstants…), if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string? _sourceUrl;

    /// <summary>Automatic loudness correction in dB (null = not analysed yet).</summary>
    [ObservableProperty] private double? _normalizationDb;

    /// <summary>Playback window inside the file, in seconds (EndSeconds 0 = until the end).</summary>
    [ObservableProperty] private double _startSeconds;
    [ObservableProperty] private double _endSeconds;

    [JsonIgnore]
    public bool HasSource => !string.IsNullOrEmpty(SourceUrl);

    [JsonIgnore]
    [ObservableProperty] private bool _isPlaying;
}

/// <summary>Availability of a track's audio file.</summary>
public enum TrackState
{
    /// <summary>The audio file is on disk and can be played.</summary>
    Ready,

    /// <summary>Known by its metadata only (imported playlist, recommendation): downloaded on first play.</summary>
    Remote,

    Downloading,

    /// <summary>The last download / resolution failed.</summary>
    Error,
}

/// <summary>One track of the music library. A track can exist without a file yet (see <see cref="TrackState.Remote"/>).</summary>
public partial class TrackItem : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Local audio file (empty while a remote track has not been downloaded yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Folder), nameof(IsDownloaded))]
    private string _filePath = "";

    /// <summary>Title given by the web source or the tags; local files fall back to their file name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _displayTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _artist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(Folder))]
    private string? _sourceUrl;

    /// <summary>Search used to find the audio of an imported track ("artist - title"), when no URL is known.</summary>
    public string? SearchQuery { get; set; }

    /// <summary>Cover / thumbnail (http URL or local file).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private string? _thumbnailUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double? _durationSeconds;

    /// <summary>Automatic loudness correction in dB (null = not analysed yet).</summary>
    public double? NormalizationDb { get; set; }

    [ObservableProperty] private bool _isFavorite;

    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Played without being added to the library (autoplay, a search result played directly): hidden from
    /// « Toutes les musiques » but kept for the history. Favouriting it or adding it to a playlist makes it permanent.
    /// </summary>
    [ObservableProperty] private bool _isTransient;

    public int PlayCount { get; set; }
    public DateTime? LastPlayedAt { get; set; }

    [JsonIgnore]
    [ObservableProperty] private bool _isCurrent;

    [JsonIgnore]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private TrackState _state;

    [JsonIgnore]
    [ObservableProperty] private string? _stateMessage;

    [JsonIgnore]
    public bool IsBusy => State == TrackState.Downloading;

    [JsonIgnore]
    public bool IsDownloaded => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    [JsonIgnore]
    public bool HasSource => !string.IsNullOrEmpty(SourceUrl);

    [JsonIgnore]
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    [JsonIgnore]
    public string Title => !string.IsNullOrWhiteSpace(DisplayTitle) ? DisplayTitle
        : !string.IsNullOrEmpty(FilePath) ? Path.GetFileNameWithoutExtension(FilePath)
        : SearchQuery ?? "Sans titre";

    /// <summary>Artist when known, otherwise the source (site or folder).</summary>
    [JsonIgnore]
    public string Subtitle => string.IsNullOrWhiteSpace(Artist) ? Folder : Artist;

    [JsonIgnore]
    public string DurationText => DurationSeconds is { } s and > 0
        ? (s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\:mm\:ss") : $"{(int)(s / 60)}:{(int)(s % 60):00}")
        : "";

    [JsonIgnore]
    public string Folder => HasSource && Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
        ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
        : string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? "";
}

/// <summary>A user playlist: an ordered list of library track ids.</summary>
public partial class MusicPlaylist : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _color = "#7C5CFF";

    /// <summary>Web playlist it was imported from (Spotify, Deezer, YouTube…), if any.</summary>
    public string? SourceUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ObservableCollection<Guid> TrackIds { get; set; } = [];
}

/// <summary>One listening, for the history, the statistics and the recommendations.</summary>
public sealed class HistoryEntry
{
    public Guid TrackId { get; set; }
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.Now;

    /// <summary>Seconds actually listened (updated when the track ends or is skipped).</summary>
    public double ListenedSeconds { get; set; }
}

/// <summary>Persisted content of library.json (kept apart from settings.json, which stays small).</summary>
public sealed class MusicLibraryData
{
    public const int CurrentVersion = 2;

    /// <summary>1 = written by an early 2.0 build (web metadata not yet derived); 2 = current.</summary>
    public int Version { get; set; } = CurrentVersion;
    public ObservableCollection<TrackItem> Tracks { get; set; } = [];
    public ObservableCollection<MusicPlaylist> Playlists { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];

    /// <summary>Artists / titles the user does not want recommended ("👎").</summary>
    public List<string> DislikedArtists { get; set; } = [];
    public List<string> DislikedUrls { get; set; } = [];

    /// <summary>Queue saved on exit, restored at the next start.</summary>
    public List<Guid> SavedQueue { get; set; } = [];
    public int SavedQueueIndex { get; set; } = -1;
    public double SavedPositionSeconds { get; set; }
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

    /// <summary>A binding written as null in settings.json (hand edit, older version) becomes "no shortcut".</summary>
    public void Sanitize()
    {
        PushToTalk ??= new HotkeyBinding();
        PlayPause ??= new HotkeyBinding();
        NextTrack ??= new HotkeyBinding();
        StopAll ??= new HotkeyBinding();
        ToggleMic ??= new HotkeyBinding();
        ToggleEffects ??= new HotkeyBinding();
        ToggleMediaToMic ??= new HotkeyBinding();
        ToggleDiscordShare ??= new HotkeyBinding();
        NextPreset ??= new HotkeyBinding();
        PreviousPreset ??= new HotkeyBinding();
    }
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

    /// <summary>Main sidebar reduced to icons, to leave more room to the pages.</summary>
    [ObservableProperty] private bool _sidebarCollapsed;

    /// <summary>« Trouver des sons » folded to one line on the Soundboard page.</summary>
    [ObservableProperty] private bool _soundSearchCollapsed;

    /// <summary>Soundboard tab shown ("" = « Tous »).</summary>
    [ObservableProperty] private string _padCategory = "";
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

    /// <summary>Soundboard tabs, in display order.</summary>
    public List<string> PadCategories { get; set; } = [];
    /// <summary>Pre-2.0 single playlist. Migrated once into library.json, then left empty.</summary>
    public ObservableCollection<TrackItem> Playlist { get; set; } = [];
    public List<EffectPreset> CustomPresets { get; set; } = [];

    public MusicSettings Music { get; set; } = new();

    public BlindTestSettings BlindTest { get; set; } = new();
    public TextToSpeechSettings TextToSpeech { get; set; } = new();

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

    /// <summary>The user chose « aucune » for the game output: do not pick a cable again by itself.</summary>
    public bool VirtualOutputDisabled { get; set; }

    /// <summary>The user chose « aucun » for the headphones: do not pick the default device again by itself.</summary>
    public bool MonitorDisabled { get; set; }
}
