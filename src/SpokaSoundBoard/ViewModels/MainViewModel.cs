using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const float MeterFloorDb = -60f;

    private readonly SettingsStore _store = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public MainViewModel()
    {
        Settings = _store.Load();
        Engine = new AudioEngine(Settings.Voice, Settings.Mix);
        Engine.StateChanged += () => _dispatcher.BeginInvoke(UpdateEngineStatus);
        Downloader = new MediaDownloader(_store.Directory);
        Mix.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MixSettings.DiscordMusic) or nameof(MixSettings.DiscordSounds))
                OnPropertyChanged(nameof(ShareMediaWithDiscord));
        };
        Hotkeys = new HotkeyService(_dispatcher);

        Soundboard = new SoundboardViewModel(this);
        Music = new MusicViewModel(this);
        Voice = new VoiceViewModel(this);
        Devices = new DevicesViewModel(this);

        Hotkeys.BindingsProvider = GetHotkeyBindings;
        Hotkeys.RegisterPushToTalk(() => Settings.Hotkeys.PushToTalk, held =>
        {
            Engine.Core.PushToTalkHeld = held;
        });

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick(), _dispatcher);
        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Save(), _dispatcher);
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => ToastMessage = null, _dispatcher);
        _toastTimer.Stop();
        _restartTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _restartTimer!.Stop();
            Devices.Refresh();
        }, _dispatcher);
        _restartTimer.Stop();
    }

    private readonly DispatcherTimer _restartTimer;

    public AppSettings Settings { get; }
    public MixSettings Mix => Settings.Mix;
    public VoiceEffectSettings VoiceSettings => Settings.Voice;
    public HotkeySettings HotkeySettings => Settings.Hotkeys;
    public AudioEngine Engine { get; }
    public HotkeyService Hotkeys { get; }
    public MediaDownloader Downloader { get; }

    /// <summary>Quick switch: do Discord friends also hear the music and the sounds?</summary>
    public bool ShareMediaWithDiscord
    {
        get => Mix.DiscordMusic || Mix.DiscordSounds;
        set
        {
            Mix.DiscordMusic = value;
            Mix.DiscordSounds = value;
            OnPropertyChanged();
        }
    }

    public SoundboardViewModel Soundboard { get; }
    public MusicViewModel Music { get; }
    public VoiceViewModel Voice { get; }
    public DevicesViewModel Devices { get; }

    [ObservableProperty] private string _currentPage = "soundboard";

    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private double _micLevelDb = MeterFloorDb;
    [ObservableProperty] private double _voiceLevel;
    [ObservableProperty] private double _mediaLevel;
    [ObservableProperty] private double _outputLevel;
    [ObservableProperty] private double _discordLevel;
    [ObservableProperty] private double _duckReductionDb;
    [ObservableProperty] private bool _voiceActive;
    [ObservableProperty] private bool _transmitting;

    [ObservableProperty] private string _engineStatus = "Démarrage…";
    [ObservableProperty] private bool _engineRunning;
    [ObservableProperty] private bool _engineError;

    [ObservableProperty] private bool _isRecordingHotkey;
    [ObservableProperty] private string _recordingTarget = "";

    [ObservableProperty] private string? _toastMessage;

    public double MeterFloor => MeterFloorDb;

    public void Initialize()
    {
        try
        {
            Hotkeys.Start();
        }
        catch (Exception ex)
        {
            ShowToast("Raccourcis globaux indisponibles : " + ex.Message);
        }

        Devices.InitializeSelection();
        RestartEngine();
        Music.RestoreState();
        _meterTimer.Start();
        _saveTimer.Start();
        if (!Devices.HasVirtualCable) CurrentPage = "settings";
    }

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page;

    [RelayCommand]
    public void RestartEngine() =>
        Engine.Start(Settings.MicDeviceId, Settings.VirtualOutputDeviceId, Settings.DiscordOutputDeviceId, Settings.MonitorDeviceId);

    [RelayCommand]
    private void ToggleMic()
    {
        Mix.MicEnabled = !Mix.MicEnabled;
        ShowToast(Mix.MicEnabled ? "Micro activé" : "Micro coupé (la musique continue)");
    }

    [RelayCommand]
    private void ToggleMediaToMic()
    {
        Mix.MediaToMicEnabled = !Mix.MediaToMicEnabled;
        ShowToast(Mix.MediaToMicEnabled ? "Musique envoyée dans le micro" : "Musique uniquement pour vous");
    }

    [RelayCommand]
    private void ToggleDiscordShare()
    {
        ShareMediaWithDiscord = !ShareMediaWithDiscord;
        ShowToast(ShareMediaWithDiscord ? "Discord entend la musique et les sons" : "Musique et sons uniquement en jeu (pas sur Discord)");
    }

    [RelayCommand]
    private void ToggleEffects()
    {
        VoiceSettings.EffectsEnabled = !VoiceSettings.EffectsEnabled;
        ShowToast(VoiceSettings.EffectsEnabled ? "Effets de voix activés" : "Effets de voix désactivés");
    }

    [RelayCommand]
    private void RecordHotkey(object? parameter)
    {
        if (parameter is not HotkeyBinding binding) return;
        RecordingTarget = binding.IsSet ? $"Actuel : {binding.Display}" : "Aucun raccourci défini";
        IsRecordingHotkey = true;
        Hotkeys.BeginRecording(binding, _ =>
        {
            IsRecordingHotkey = false;
            Save();
        });
    }

    [RelayCommand]
    private void CancelRecording()
    {
        Hotkeys.CancelRecording();
        IsRecordingHotkey = false;
    }

    [RelayCommand]
    private void ClearHotkey(object? parameter)
    {
        if (parameter is HotkeyBinding binding) binding.Clear();
    }

    public void ShowToast(string message)
    {
        ToastMessage = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private IEnumerable<(HotkeyBinding, Action)> GetHotkeyBindings()
    {
        var h = Settings.Hotkeys;
        yield return (h.PlayPause, () => Music.PlayPauseCommand.Execute(null));
        yield return (h.NextTrack, () => Music.NextCommand.Execute(null));
        yield return (h.StopAll, () => Soundboard.StopAllCommand.Execute(null));
        yield return (h.ToggleMic, ToggleMic);
        yield return (h.ToggleEffects, ToggleEffects);
        yield return (h.ToggleMediaToMic, ToggleMediaToMic);
        yield return (h.ToggleDiscordShare, ToggleDiscordShare);
        foreach (var pad in Settings.Pads.ToList())
            yield return (pad.Hotkey, () => Soundboard.PlayPadCommand.Execute(pad));
    }

    private void Tick()
    {
        var m = Engine.Core.Meters;

        // Fast attack, slow release for readable meters.
        MicLevelDb = Math.Max(m.MicLevelDb, MicLevelDb - 1.2);
        MicLevel = Normalize((float)MicLevelDb, true);
        VoiceLevel = Smooth(VoiceLevel, Normalize(m.VoicePeak));
        MediaLevel = Smooth(MediaLevel, Normalize(m.MediaPeak));
        OutputLevel = Smooth(OutputLevel, Normalize(m.OutputPeak));
        DiscordLevel = Smooth(DiscordLevel, Normalize(m.DiscordPeak));
        var duckDb = Math.Round(DspUtil.LinearToDb(m.DuckGain));
        DuckReductionDb = duckDb > -0.5 ? 0 : duckDb;
        VoiceActive = m.VoiceActive;
        // "On air": key held in radio mode, otherwise voice detected while the mic is sent.
        Transmitting = m.Transmitting && (Mix.PushToTalkEnabled || m.VoiceActive);

        Soundboard.RefreshPlayingState();
        Music.RefreshPosition();
    }

    private static double Smooth(double previous, double next) => next > previous ? next : previous * 0.85 + next * 0.15;

    private static double Normalize(float value, bool isDb = false)
    {
        var db = isDb ? value : DspUtil.LinearToDb(value);
        return Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1);
    }

    private void UpdateEngineStatus()
    {
        EngineRunning = Engine.State == EngineState.Running;
        EngineError = Engine.State == EngineState.Error;
        EngineStatus = Engine.StatusMessage;

        // A device was unplugged or reset: retry a few times (e.g. wireless headset reconnecting).
        if (EngineRunning)
        {
            _restartAttempts = 0;
        }
        else if (EngineError && _restartAttempts < 5)
        {
            _restartAttempts++;
            _restartTimer.Stop();
            _restartTimer.Start();
        }
    }

    private int _restartAttempts;

    public void Save()
    {
        try
        {
            Settings.SelectedPresetName = Voice.SelectedPresetName;
            Settings.Repeat = Music.Repeat;
            Settings.Shuffle = Music.Shuffle;
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            ShowToast("Impossible d'enregistrer les réglages : " + ex.Message);
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _saveTimer.Stop();
        Save();
        Hotkeys.Dispose();
        Engine.Dispose();
    }
}
