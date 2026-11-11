using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Services;
using SpokaSoundBoard.ViewModels.Music;

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
        Engine.VolumeCorrected += _ => _dispatcher.BeginInvoke(() =>
        {
            if (_volumeWarningShown) return;
            _volumeWarningShown = true;
            ShowToast("Discord ou Windows baissait le son envoyé dans le micro virtuel : corrigé automatiquement. " +
                      "Dans Discord, mettez « Atténuation » à 0 %.");
        });
        Loudness = new LoudnessService(_dispatcher);
        Downloader = new MediaDownloader(_store.Directory, System.IO.Path.Combine(AppContext.BaseDirectory, "tools"));
        Mix.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MixSettings.DiscordMusic) or nameof(MixSettings.DiscordSounds))
                OnPropertyChanged(nameof(ShareMediaWithDiscord));
        };
        Hotkeys = new HotkeyService(_dispatcher);

        Soundboard = new SoundboardViewModel(this);
        Music = new MusicViewModel(this);
        BlindTest = new BlindTestViewModel(this);
        Voice = new VoiceViewModel(this);
        Devices = new DevicesViewModel(this);
        Setup = new SetupWizardViewModel(this);
        Appearance = new AppearanceViewModel(this);

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
    public LoudnessService Loudness { get; }

    /// <summary>Linear gain for a loudness correction, honouring the "normalize" switch.</summary>
    public float NormalizationGain(double? db) =>
        Mix.NormalizeLoudness && db is { } value ? DspUtil.DbToLinear((float)value) : 1f;

    [ObservableProperty] private string _diagnosticsText = "";
    [ObservableProperty] private UpdateInfo? _availableUpdate;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;

    public string CurrentVersion => (typeof(MainViewModel).Assembly.GetName().Version ?? new Version(1, 0)).ToString(3);

    /// <summary>Microsoft Store (MSIX) edition: updates come from the Store, autostart is a StartupTask.</summary>
    public bool IsStoreEdition => AppEnvironment.IsPackaged;

    public string EditionName => IsStoreEdition ? "Microsoft Store" : "Installeur";

    public const string PrivacyPolicyUrl = "https://github.com/Spokator/SpokaSoundBoard/blob/main/docs/PRIVACY.md";

    public string SettingsLocationText => IsStoreEdition
        ? "Vos réglages et votre bibliothèque sont enregistrés automatiquement sur ce PC, dans le dossier privé de l'application (supprimé si vous la désinstallez)."
        : @"Vos réglages sont enregistrés automatiquement dans %AppData%\SpokaSoundBoard.";

    [RelayCommand]
    private static void OpenPrivacyPolicy() => WindowsIntegration.Open(PrivacyPolicyUrl);

    public bool CheckForUpdates
    {
        get => Settings.CheckForUpdates;
        set
        {
            Settings.CheckForUpdates = value;
            OnPropertyChanged();
            Save();
        }
    }
    public MediaSearch Search => _search ??= new MediaSearch(Downloader);
    private MediaSearch? _search;
    private bool _volumeWarningShown;

    /// <summary>Set by the tray "Quitter" item so the close button does not hide the window again.</summary>
    public bool ExitRequested { get; set; }

    public bool CloseToTray
    {
        get => Settings.CloseToTray;
        set
        {
            Settings.CloseToTray = value;
            OnPropertyChanged();
            Save();
        }
    }

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
    public BlindTestViewModel BlindTest { get; }

    /// <summary>Writable data folder (%AppData%\SpokaSoundBoard, or the package's private copy).</summary>
    public string DataDirectory => _store.Directory;
    public VoiceViewModel Voice { get; }
    public DevicesViewModel Devices { get; }
    public SetupWizardViewModel Setup { get; }
    public AppearanceViewModel Appearance { get; }

    private bool _autostart;

    /// <summary>"Lancer au démarrage de Windows": Run key (installer edition) or StartupTask (Store edition).</summary>
    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (!SetProperty(ref _autostart, value)) return;
            _ = ApplyAutostartAsync(value);
        }
    }

    /// <summary>Reads the real state (the user can change it in Windows settings / Task Manager at any time).</summary>
    public async Task RefreshAutostartAsync()
    {
        try
        {
            SetProperty(ref _autostart, await WindowsIntegration.IsAutostartEnabledAsync(), nameof(Autostart));
        }
        catch
        {
            // Keep the displayed value.
        }
    }

    private async Task ApplyAutostartAsync(bool enabled)
    {
        try
        {
            switch (await WindowsIntegration.SetAutostartAsync(enabled))
            {
                case AutostartResult.DisabledByUser:
                    ShowToast("Windows a désactivé le lancement au démarrage de SpokaSoundBoard. Activez-le dans " +
                              "Paramètres Windows → Applications → Démarrage (la page vient de s'ouvrir).", 10);
                    WindowsIntegration.Open(WindowsIntegration.StartupAppsSettings);
                    break;
                case AutostartResult.ControlledByPolicy:
                    ShowToast("Le lancement au démarrage est imposé par votre administrateur : voyez " +
                              "Paramètres Windows → Applications → Démarrage.", 10);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowToast("Impossible de modifier le démarrage automatique : " + Core.FriendlyErrors.Describe(ex));
        }
        await RefreshAutostartAsync();
    }

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

    /// <param name="setupRequested">Started with --setup (e.g. after the restart requested by the assistant).</param>
    public void Initialize(bool setupRequested = false)
    {
        try
        {
            Hotkeys.Start();
        }
        catch (Exception ex)
        {
            ShowToast("Raccourcis globaux indisponibles : " + Core.FriendlyErrors.Describe(ex));
        }

        LinkImporter.CleanPreviewCache(Downloader);
        Devices.InitializeSelection();
        RestartEngine();
        Music.RestoreState();
        Soundboard.AnalyzeMissingLoudness();
        Music.AnalyzeMissingLoudness();
        Music.ReadMissingMetadata();
        // Store edition: the Store updates the app, never check GitHub nor run an installer.
        if (Settings.CheckForUpdates && !IsStoreEdition) _ = CheckForUpdateAsync(silent: true);
        _ = RefreshAutostartAsync();
        _meterTimer.Start();
        _saveTimer.Start();
        if (setupRequested || !Settings.FirstRunDone || Settings.SetupResumeStep != null)
            Setup.Open(Settings.SetupResumeStep ?? SetupWizardViewModel.StepWelcome);
        else if (!Devices.HasVirtualCable)
            CurrentPage = "settings";
    }

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page;

    /// <summary>Shrinks the main sidebar to icons only (or expands it back), remembered across launches.</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        Settings.Appearance.SidebarCollapsed = !Settings.Appearance.SidebarCollapsed;
        Save();
    }

    [RelayCommand]
    public void RestartEngine() =>
        Engine.Start(Settings.MicDeviceId, Settings.VirtualOutputDeviceId, Settings.DiscordOutputDeviceId, Settings.MonitorDeviceId);

    [RelayCommand]
    private void OpenSetup() => Setup.Open();

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

    public void ShowToast(string message, double seconds = 4)
    {
        ToastMessage = message;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
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
        yield return (h.NextPreset, () => Voice.CyclePreset(1));
        yield return (h.PreviousPreset, () => Voice.CyclePreset(-1));
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
        if (++_diagnosticTicks % 30 == 0) UpdateDiagnostics();
    }

    private int _diagnosticTicks;

    private void UpdateDiagnostics()
    {
        var d = Engine.GetDiagnostics();
        DiagnosticsText =
            $"Coupures micro : {d.MicDropouts}   ·   Coupures sortie Discord : {d.DiscordDropouts}   ·   Coupures casque : {d.HeadphoneDropouts}\n" +
            $"Baisses de volume corrigées : {d.VolumeCorrections}   ·   Compensation d'horloge Discord : {(d.DiscordClockRatio - 1) * 100:+0.000;-0.000;0.000} %";
    }

    [RelayCommand]
    private async Task CheckForUpdateNow() => await CheckForUpdateAsync(silent: false);

    private async Task CheckForUpdateAsync(bool silent)
    {
        if (IsStoreEdition) return;
        try
        {
            if (silent) await Task.Delay(TimeSpan.FromSeconds(5));
            var current = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(1, 0);
            var update = await UpdateChecker.CheckAsync(current);
            if (update != null && silent && update.Tag == Settings.SkippedUpdate) update = null;
            AvailableUpdate = update;
            if (!silent && update == null) ShowToast($"SpokaSoundBoard est à jour (version {CurrentVersion}).");
        }
        catch (Exception ex)
        {
            if (!silent) ShowToast("Vérification impossible : " + Core.FriendlyErrors.Describe(ex));
        }
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } update || IsUpdating || IsStoreEdition) return;
        IsUpdating = true;
        try
        {
            var setup = await UpdateChecker.DownloadAsync(update, new Progress<double>(p => UpdateProgress = p));
            Save();
            // The installer closes this instance, updates it and starts it again.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/SILENT /SP- /NOCANCEL /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowToast("Mise à jour impossible : " + Core.FriendlyErrors.Describe(ex));
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        Settings.SkippedUpdate = AvailableUpdate?.Tag;
        AvailableUpdate = null;
        Save();
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        if (AvailableUpdate != null) WindowsIntegration.Open(AvailableUpdate.ReleaseUrl);
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
            ShowToast("Impossible d'enregistrer les réglages : " + Core.FriendlyErrors.Describe(ex));
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _saveTimer.Stop();
        Save();
        BlindTest.Shutdown();
        Music.Shutdown();
        Hotkeys.Dispose();
        Loudness.Dispose();
        Engine.Dispose();
    }
}
