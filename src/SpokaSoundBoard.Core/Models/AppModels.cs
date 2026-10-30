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

    [JsonIgnore]
    [ObservableProperty] private bool _isPlaying;
}

public partial class TrackItem : ObservableObject
{
    [ObservableProperty] private string _filePath = "";

    [JsonIgnore]
    [ObservableProperty] private bool _isCurrent;

    [JsonIgnore]
    public string Title => Path.GetFileNameWithoutExtension(FilePath);

    [JsonIgnore]
    public string Folder => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? "";
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
}

public class AppSettings
{
    public string? MicDeviceId { get; set; }
    public string? VirtualOutputDeviceId { get; set; }
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
