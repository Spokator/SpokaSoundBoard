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
        _deviceChangeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _deviceChangeTimer!.Stop();
            Devices.OnDevicesChanged();
        }, _dispatcher);
        _deviceChangeTimer.Stop();
    }

    private readonly DispatcherTimer _restartTimer;

    // Plugging a headset raises several notifications in a row: wait for them to settle.
    private readonly DispatcherTimer _deviceChangeTimer;
    private DeviceWatcher? _deviceWatcher;

    private void WatchDevices()
    {
        try
        {
            _deviceWatcher = new DeviceWatcher(() => _dispatcher.BeginInvoke(() =>
            {
                _deviceChangeTimer.Stop();
                _deviceChangeTimer.Start();
            }));
        }
        catch (Exception ex)
        {
            // Not critical: « Actualiser » still works.
            App.Log(ex);
        }
    }

    public AppSettings Settings { get; }
    public MixSettings Mix => Settings.Mix;

    /// <summary>rnnoise.dll loaded: the « Suppression du bruit » switch can be used.</summary>
    public bool NoiseSuppressionAvailable => Engine.Core.NoiseSuppressionAvailable;

    public string NoiseSuppressionCaption => NoiseSuppressionAvailable
        ? "Enlève le clavier, le ventilateur, le souffle et les bruits de fond avant les effets, sans couper votre voix (IA RNNoise, hors ligne). Ajoute 30 ms de délai."
        : "Indisponible : le composant de suppression du bruit (rnnoise.dll) n'a pas pu être chargé.";
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

    /// <summary>Something may have been heard as a gap or a crackle in the last minute (see the diagnostics).</summary>
    [ObservableProperty] private bool _audioGlitchesRecent;

    private long _lastGlitches;
    private DateTime _lastGlitchAt = DateTime.MinValue;
    [ObservableProperty] private UpdateInfo? _availableUpdate;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;

    public string CurrentVersion => (typeof(MainViewModel).Assembly.GetName().Version ?? new Version(1, 0)).ToString(3);

    /// <summary>Microsoft Store (MSIX) edition: updates come from the Store, autostart is a StartupTask.</summary>
    public bool IsStoreEdition => AppEnvironment.IsPackaged;

    public string EditionName => IsStoreEdition ? "Microsoft Store" : "Installeur";

    // ---- Help and diagnostics ---------------------------------------------------------------------------------

    /// <summary>Text to paste in a bug report: versions, devices, audio counters and the end of the error log.</summary>
    public string BuildDiagnosticsReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SpokaSoundBoard {CurrentVersion} ({EditionName})");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} {(Environment.Is64BitOperatingSystem ? "64 bits" : "32 bits")}, .NET {Environment.Version}");
        sb.AppendLine($"Audio : {(EngineRunning ? "en marche" : EngineError ? "en erreur" : "arrêté")}");
        sb.AppendLine($"  Micro : {Engine.MicName ?? "aucun"}");
        sb.AppendLine($"  Sortie jeu : {Engine.MainOutputName ?? "aucune"}");
        sb.AppendLine($"  Sortie Discord : {Engine.DiscordOutputName ?? "aucune"}");
        sb.AppendLine($"  Casque : {Engine.MonitorName ?? "aucun"}");
        sb.AppendLine($"  Micro virtuel détecté : {(Devices.HasVirtualCable ? "oui" : "non")}");
        UpdateDiagnostics();
        sb.AppendLine(DiagnosticsText.Replace("   ·   ", "\n"));
        sb.AppendLine($"Sons : {Settings.Pads.Count} · Musiques : {Music.Library.LibraryTracks.Count()} · Playlists : {Music.Library.Playlists.Count}");
        try
        {
            if (System.IO.File.Exists(App.LogPath))
            {
                var lines = System.IO.File.ReadAllLines(App.LogPath);
                sb.AppendLine();
                sb.AppendLine("Dernières erreurs enregistrées :");
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - 40))) sb.AppendLine(line);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            sb.AppendLine("(journal d'erreurs illisible)");
        }
        return sb.ToString();
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildDiagnosticsReport());
            ShowToast("Rapport copié : collez-le (Ctrl+V) dans votre signalement. Il ne contient ni vos sons ni vos réglages personnels.", 6);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowToast("Le presse-papiers est occupé par une autre application : réessayez dans un instant.");
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => WindowsIntegration.Open(DataDirectory);

    /// <summary>Opens the GitHub issue form (nothing is sent by the app: the user pastes the report if they want).</summary>
    [RelayCommand]
    private void ReportProblem() => WindowsIntegration.Open("https://github.com/Spokator/SpokaSoundBoard/issues/new/choose");

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

    partial void OnCurrentPageChanged(string value)
    {
        if (value == "settings")
        {
            _ = RefreshStorageAsync();
            _ = RefreshBackupEstimateAsync();
            RefreshProfileChoices();
        }
    }

    // ---- Storage (Configuration page) ---------------------------------------------------------------------

    [ObservableProperty] private string _storageText = "Calcul de l'espace utilisé…";
    [ObservableProperty] private string _releasableText = "";
    [ObservableProperty] private bool _canFreeMusicSpace;

    private string MusicDownloadFolder => System.IO.Path.Combine(Downloader.CacheDirectory, ViewModels.Music.MusicViewModel.DownloadFolder);

    private HashSet<Guid> TracksToKeep()
    {
        var keep = Music.Queue.Items.Select(t => t.Id).ToHashSet();
        if (Music.CurrentTrack != null) keep.Add(Music.CurrentTrack.Id);
        return keep;
    }

    public async Task RefreshStorageAsync()
    {
        var music = MusicDownloadFolder;
        var sounds = System.IO.Path.Combine(Downloader.CacheDirectory, TextToSpeechViewModel.SoundsFolder);
        var voices = System.IO.Path.Combine(DataDirectory, Core.Speech.VoiceModelStore.FolderName);
        var releasable = Music.LibraryLoaded ? Music.Library.ReleasableDownloads(music, TracksToKeep()) : [];
        var files = releasable.Select(t => t.FilePath).ToList();
        var (musicSize, soundsSize, voicesSize, releasableSize) = await Task.Run(() =>
        (
            Core.Online.DownloadCache.FolderSize(music),
            Core.Online.DownloadCache.FolderSize(sounds),
            Core.Online.DownloadCache.FolderSize(voices),
            files.Sum(f => System.IO.File.Exists(f) ? new System.IO.FileInfo(f).Length : 0L)
        ));
        StorageText = $"Musiques téléchargées : {Core.Online.DownloadCache.FormatSize(musicSize)}   ·   " +
                      $"Sons : {Core.Online.DownloadCache.FormatSize(soundsSize)}   ·   " +
                      $"Voix naturelles : {Core.Online.DownloadCache.FormatSize(voicesSize)}";
        CanFreeMusicSpace = releasable.Count > 0 && releasableSize > 0;
        ReleasableText = CanFreeMusicSpace
            ? $"{releasable.Count} musique(s) téléchargée(s) ne sont ni en favori ni dans une playlist : {Core.Online.DownloadCache.FormatSize(releasableSize)} récupérables."
            : "Rien à libérer : chaque musique téléchargée est en favori, dans une playlist ou dans la file d'attente.";
    }

    /// <summary>Deletes downloaded songs nobody keeps on purpose; they stay in the library and download again if played.</summary>
    [RelayCommand]
    private async Task FreeMusicSpace()
    {
        if (!Music.LibraryLoaded) return;
        var tracks = Music.Library.ReleasableDownloads(MusicDownloadFolder, TracksToKeep());
        if (tracks.Count == 0) return;
        var question = $"Supprimer les fichiers de {tracks.Count} musique(s) téléchargée(s) ?\n\n" +
                       "Elles restent dans votre bibliothèque et votre historique, et seront retéléchargées si vous les écoutez. " +
                       "Vos favoris, vos playlists et la file d'attente ne sont pas touchés.";
        if (System.Windows.MessageBox.Show(question, "SpokaSoundBoard", System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.OK) return;
        var paths = tracks.Select(t => t.FilePath).ToList();
        var deleted = await Task.Run(() => paths.Select(path =>
        {
            try
            {
                var length = new System.IO.FileInfo(path).Length;
                System.IO.File.Delete(path);
                return length;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                return -1L;
            }
        }).ToList());
        long freed = 0;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (deleted[i] < 0) continue;
            freed += deleted[i];
            Music.Library.ForgetDownload(tracks[i]);
        }
        Music.SaveLibrary();
        ShowToast($"{Core.Online.DownloadCache.FormatSize(freed)} libérés.");
        await RefreshStorageAsync();
    }

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
        WatchDevices();
        InitializeRemote();
        Music.RestoreState();
        Soundboard.AnalyzeMissingLoudness();
        Music.AnalyzeMissingLoudness();
        Music.ReadMissingMetadata();
        // Store edition: the Store updates the app, never check GitHub nor run an installer.
        if (Settings.CheckForUpdates && !IsStoreEdition) _ = CheckForUpdateAsync(silent: true);
        _ = RefreshAutostartAsync();
        _meterTimer.Start();
        _saveTimer.Start();
        StartGameProfiles();
        if (setupRequested || !Settings.FirstRunDone || Settings.SetupResumeStep != null)
            Setup.Open(Settings.SetupResumeStep ?? SetupWizardViewModel.StepWelcome);
        else if (!Devices.HasVirtualCable)
            CurrentPage = "settings";
        if (_store.LoadProblem is { } problem) ShowToast(problem, 12);
        else if (App.StartupNotice is { } notice) ShowToast(notice, 10);
        CleanDownloadCache();
    }

    /// <summary>
    /// Deletes downloaded sounds and songs no pad or library track uses any more. Never runs when the settings or the
    /// library could not be read: their file references would be incomplete.
    /// </summary>
    private void CleanDownloadCache()
    {
        if (_store.LoadProblem != null || !Music.LibraryLoaded || RestoredRecently()) return;
        var referenced = Settings.Pads.Select(p => (string?)p.FilePath)
            .Concat(Music.Library.Tracks.Select(t => t.FilePath))
            .ToList();
        var cache = Downloader.CacheDirectory;
        string[] folders = [MusicViewModel.DownloadFolder, TextToSpeechViewModel.SoundsFolder];
        _ = Task.Run(() =>
        {
            try
            {
                Core.Online.DownloadCache.DeleteOrphans(cache, folders, referenced, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        });
    }

    /// <summary>Hidden in the tray during a game: meters refresh twice a second instead of 30 times.</summary>
    public void SetWindowVisible(bool visible) =>
        _meterTimer.Interval = TimeSpan.FromMilliseconds(visible ? 33 : 500);

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
    public void RestartEngine()
    {
        // The selected devices, not the saved ones: while a device is unplugged, its stand-in must actually play.
        var d = Devices;
        Engine.Start(d.SelectedMic?.Id, d.SelectedVirtualOutput?.Id, d.SelectedDiscordOutput?.Id, d.SelectedMonitor?.Id);
    }

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
        Hotkeys.BeginRecording(binding, changed =>
        {
            IsRecordingHotkey = false;
            if (changed) WarnHotkeyConflicts(binding);
            Save();
        });
    }

    /// <summary>The same key does two things at once: say so right away rather than let the user find out in game.</summary>
    private void WarnHotkeyConflicts(HotkeyBinding binding)
    {
        var owners = HotkeyMatching.Conflicts(binding, NamedHotkeys()).Distinct().ToList();
        if (owners.Count == 0) return;
        ShowToast($"Attention : « {binding.Display} » sert déjà à {string.Join(", ", owners.Select(o => $"« {o} »"))}. " +
                  "Les deux se déclencheront ensemble.", 8);
    }

    private IEnumerable<(HotkeyBinding Binding, string Owner)> NamedHotkeys()
    {
        foreach (var row in Devices.HotkeyRows) yield return (row.Binding, row.Label);
        foreach (var pad in Settings.Pads) yield return (pad.Hotkey, pad.Name);
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

    private IEnumerable<HotkeyAction> GetHotkeyBindings()
    {
        var h = Settings.Hotkeys;
        yield return new(h.PlayPause, () => Music.PlayPauseCommand.Execute(null));
        yield return new(h.NextTrack, () => Music.NextCommand.Execute(null));
        yield return new(h.MusicVolumeUp, () => StepMusicVolume(1), Repeat: true);
        yield return new(h.MusicVolumeDown, () => StepMusicVolume(-1), Repeat: true);
        yield return new(h.StopAll, () => Soundboard.StopAllCommand.Execute(null));
        yield return new(h.ToggleMic, ToggleMic);
        yield return new(h.ToggleEffects, ToggleEffects);
        yield return new(h.ToggleMediaToMic, ToggleMediaToMic);
        yield return new(h.ToggleDiscordShare, ToggleDiscordShare);
        yield return new(h.NextPreset, () => Voice.CyclePreset(1));
        yield return new(h.PreviousPreset, () => Voice.CyclePreset(-1));
        yield return new(h.ToggleLiveVoice, () => Soundboard.Tts.ToggleLiveCommand.Execute(null));
        foreach (var pad in Settings.Pads.ToList())
            yield return new(pad.Hotkey, () => Soundboard.PlayPadCommand.Execute(pad));
    }

    /// <summary>Music louder or quieter by 5 %, for every output (headphones, game, Discord).</summary>
    public void StepMusicVolume(int direction)
    {
        var volume = MixSettings.StepVolume(Mix.MusicVolume, direction);
        Mix.MusicVolume = volume;
        ShowToast($"Volume de la musique : {volume * 100:0} %", 1.5);
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
        var h = Engine.GetRenderHealth();
        var cache = Engine.SoundCache;
        DiagnosticsText =
            $"Coupures micro : {d.MicDropouts}   ·   Coupures sortie Discord : {d.DiscordDropouts}   ·   Coupures casque : {d.HeadphoneDropouts}\n" +
            $"Baisses de volume corrigées : {d.VolumeCorrections}   ·   Compensation d'horloge Discord : {(d.DiscordClockRatio - 1) * 100:+0.000;-0.000;0.000} %\n" +
            $"Calcul du son : {h.AverageLoad * 100:0.0} % du temps disponible en moyenne, pic {h.PeakLoad * 100:0} %   ·   Blocs en retard : {h.LateBlocks}   ·   Blocs lents : {h.SlowBlocks}\n" +
            $"PC trop occupé : {h.Stalls}   ·   Lecture disque en retard : {h.DecodeUnderruns}   ·   Sons prêts en mémoire : {cache.Count} ({cache.Bytes / (1024.0 * 1024):0} Mo)";

        // Restarting the audio resets the counters.
        var glitches = h.Glitches;
        if (glitches < _lastGlitches) _lastGlitches = 0;
        if (glitches > _lastGlitches) _lastGlitchAt = DateTime.UtcNow;
        _lastGlitches = glitches;
        AudioGlitchesRecent = EngineRunning && DateTime.UtcNow - _lastGlitchAt < TimeSpan.FromMinutes(1);
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
        _deviceWatcher?.Dispose();
        DisposeRemote();
        // Put back what a game profile changed before saving, so it is not kept by mistake.
        StopGameProfiles();
        _saveTimer.Stop();
        Save();
        BlindTest.Shutdown();
        Soundboard.Tts.ShutdownLive();
        Music.Shutdown();
        Hotkeys.Dispose();
        Loudness.Dispose();
        Engine.Dispose();
    }
}
