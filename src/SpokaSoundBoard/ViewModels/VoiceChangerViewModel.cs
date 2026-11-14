using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.ViewModels;

/// <summary>One voice card of the AI voice changer: a built-in voice or one of « Mes voix ».</summary>
public sealed partial class VoiceChangerOption : ObservableObject
{
    public VoiceChangerOption(VoiceChangerVoice voice)
    {
        BuiltIn = voice;
        Key = voice.Key;
        Title = voice.Name;
        Subtitle = voice.Description;
    }

    public VoiceChangerOption(CustomVoice voice)
    {
        Custom = voice;
        Key = voice.Key;
        Title = voice.Name;
        Subtitle = $"Ma voix · {voice.SpeechSeconds:0} s de voix";
    }

    public VoiceChangerVoice? BuiltIn { get; }
    public CustomVoice? Custom { get; }
    public bool IsCustom => Custom != null;
    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }

    [ObservableProperty] private bool _isSelected;

    public override string ToString() => Title;
}

/// <summary>
/// « Changeur de voix IA »: your voice, with your intonation, takes the timbre of a chosen voice in real time. The
/// conversion runs in SpokaVoiceEngine (a separate GPL-3.0 program, on the graphics card); the converted voice then
/// goes through the usual effects. Never starts by itself.
/// </summary>
public sealed partial class VoiceChangerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;
    private readonly VoiceModelStore _store;
    private VoiceChangerSession? _session;
    private CancellationTokenSource? _downloadCts;
    private double _msPerBlock;
    private bool _gpuProbed;

    public VoiceChangerViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _store = new VoiceModelStore(Path.Combine(main.DataDirectory, VoiceModelStore.FolderName));
        Library = new CustomVoiceLibrary(Path.Combine(main.DataDirectory, VoiceModelStore.FolderName));
        _isInstalled = _store.IsInstalled(Pack);
        RefreshVoices();
    }

    public static VoiceModelPack Pack => VoiceChangerCatalog.SeedVc;

    /// <summary>The engine shipped with the app (or the one named by SPOKA_VOICE_ENGINE, for development).</summary>
    public static string EnginePath =>
        Environment.GetEnvironmentVariable("SPOKA_VOICE_ENGINE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "voice-engine", "SpokaVoiceEngine.exe");

    public VoiceChangerSettings Settings => _main.Settings.VoiceChanger;
    public System.Collections.ObjectModel.ObservableCollection<VoiceChangerOption> Voices { get; } = [];
    public CustomVoiceLibrary Library { get; }

    /// <summary>Built-in voices, then « Mes voix » (oldest first).</summary>
    public void RefreshVoices()
    {
        Voices.Clear();
        foreach (var v in VoiceChangerCatalog.Voices) Voices.Add(new VoiceChangerOption(v));
        foreach (var v in Library.Load()) Voices.Add(new VoiceChangerOption(v));
        MarkSelected();
    }

    private string VoicePath(VoiceChangerOption option) =>
        option.Custom is { } custom ? Library.PathOf(custom) : _store.FilePath(Pack, option.BuiltIn!.FileName);

    /// <summary>The chosen voice, or the default one when it no longer exists (deleted « Ma voix »).</summary>
    private VoiceChangerOption CurrentVoice() =>
        Voices.FirstOrDefault(v => v.Key == Settings.VoiceKey) ?? Voices.First(v => v.Key == VoiceChangerCatalog.DefaultVoiceKey);

    /// <summary>« Réactif » chip (the other one is « Stable »).</summary>
    public bool IsReactive
    {
        get => Settings.Mode == VoiceChangerMode.Reactive;
        set => SetMode(value ? VoiceChangerMode.Reactive : VoiceChangerMode.Stable);
    }

    public bool IsStable
    {
        get => Settings.Mode == VoiceChangerMode.Stable;
        set => SetMode(value ? VoiceChangerMode.Stable : VoiceChangerMode.Reactive);
    }

    private void SetMode(VoiceChangerMode mode)
    {
        if (Settings.Mode == mode) return;
        Settings.Mode = mode;
        OnPropertyChanged(nameof(IsReactive));
        OnPropertyChanged(nameof(IsStable));
        _main.Save();
        // Running: restart with the new settings (a second of silence).
        if (IsOn) _ = RestartAsync();
    }

    private async Task RestartAsync()
    {
        Stop(notify: false);
        await StartAsync();
    }
    public string PackSizeText => VoiceModelStore.FormatSize(Pack.TotalSize);
    public bool IsEngineAvailable => File.Exists(EnginePath);

    public string LicenseText =>
        "Moteur SpokaVoiceEngine (licence GPL-3.0, programme séparé) et modèle Seed-VC (GPL-3.0), XLS-R et HiFT (Apache-2.0). " +
        "Les voix sont celles des voix naturelles (Supertonic, OpenRAIL-M). Tout se passe sur votre PC.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(ShowDownload), nameof(CanTurnOn))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDownload), nameof(CanTurnOn))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(IsSwitchOn))]
    private bool _isOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(IsSwitchOn), nameof(CanTurnOn))]
    private bool _isStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(CanTurnOn))]
    private string? _gpuName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _noGpu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _liveInfo = "";

    /// <summary>Shown under the title: what is going on, in one line.</summary>
    public string Status =>
        !IsEngineAvailable ? "Le moteur du changeur de voix n'est pas installé avec cette version."
        : NoGpu ? "Aucune carte graphique compatible (DirectX 12) : le changeur de voix IA ne peut pas fonctionner sur ce PC."
        : IsStarting ? $"Chargement sur la carte graphique{(GpuName is { } g ? $" ({g})" : "")}… quelques secondes."
        : IsOn ? $"Actif : {LiveInfo}"
        : !IsInstalled ? $"Modèles à télécharger une seule fois ({PackSizeText})."
        : GpuName is { } gpu ? $"Prêt : {gpu}. Votre vraie voix passe tant que c'est désactivé."
        : "Prêt.";

    public bool ShowDownload => !IsInstalled && !IsDownloading;
    public bool CanTurnOn => IsEngineAvailable && !NoGpu && !IsStarting && !IsDownloading;

    /// <summary>The switch: on while starting or running; flipping it starts or stops the changer.</summary>
    public bool IsSwitchOn
    {
        get => IsOn || IsStarting;
        set
        {
            if (value != IsSwitchOn) _ = ToggleAsync();
        }
    }

    /// <summary>Called when the page opens: finds the graphics card once (without loading anything).</summary>
    public void ProbeGpu()
    {
        if (_gpuProbed || !IsEngineAvailable) return;
        _gpuProbed = true;
        _ = Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(EnginePath, "--list-gpus")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi)!;
                var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                p.WaitForExit(5000);
                var hardware = lines.Where(l => !l.Contains("[logiciel]")).Select(l => l[(l.IndexOf(':') + 1)..].Trim()).ToList();
                _dispatcher.BeginInvoke(() =>
                {
                    NoGpu = hardware.Count == 0;
                    GpuName = hardware.FirstOrDefault() is { } first ? first[..first.LastIndexOf('(')].Trim() : null;
                });
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        });
    }

    [RelayCommand]
    private void SelectVoice(VoiceChangerOption? option)
    {
        if (option == null) return;
        Settings.VoiceKey = option.Key;
        MarkSelected();
        _main.Save();
        if (_session is { IsRunning: true } session && IsOn)
        {
            session.SetVoice(VoicePath(option));
            _main.ShowToast($"Votre voix devient « {option.Title} ».");
        }
    }

    private void MarkSelected()
    {
        var current = Voices.FirstOrDefault(v => v.Key == Settings.VoiceKey);
        foreach (var v in Voices) v.IsSelected = ReferenceEquals(v, current);
    }

    [RelayCommand]
    public async Task ToggleAsync()
    {
        if (IsStarting) return;
        if (IsOn) Stop(notify: true);
        else await StartAsync();
    }

    private async Task StartAsync()
    {
        if (!IsEngineAvailable)
        {
            _main.ShowToast("Le moteur du changeur de voix n'est pas installé avec cette version.");
            return;
        }
        if (!IsInstalled && !await InstallAsync()) return;

        // One voice replacement at a time.
        if (_main.Soundboard.Tts.IsLive) await _main.Soundboard.Tts.ToggleLiveCommand.ExecuteAsync(null);

        IsStarting = true;
        var session = new VoiceChangerSession(EnginePath, _store.PackDirectory(Pack));
        session.StatsReceived += s => _dispatcher.BeginInvoke(() => OnStats(s));
        session.Failed += e => _dispatcher.BeginInvoke(() => OnFailed(e));
        try
        {
            var voice = CurrentVoice();
            var ready = await session.StartAsync(VoicePath(voice), VoiceChangerProfile.For(Settings.Mode));
            GpuName = ready.Gpu;
            if (ready.TooSlow)
            {
                session.Dispose();
                _main.ShowToast($"Votre carte graphique ({ready.Gpu}) est trop lente pour le changeur de voix en temps réel : " +
                                $"{ready.MsPerBlock:0} ms de calcul pour {ready.BlockMs} ms de voix.", 10);
                return;
            }
            _session = session;
            _msPerBlock = ready.MsPerBlock;
            UpdateLiveInfo(ready.MsPerBlock / ready.BlockMs);
            _main.Engine.Core.VoiceChanger = session.Link;
            IsOn = true;
            _main.ShowToast($"Changeur de voix actif : vous parlez avec la voix « {voice.Title} » (délai d'environ {session.EstimatedDelayMs(ready.MsPerBlock) / 1000.0:0.0} s).", 6);
        }
        catch (Exception ex)
        {
            session.Dispose();
            App.Log(ex);
            if (ex is VoiceChangerException { Error.Code: "no-gpu" }) NoGpu = true;
            _main.ShowToast("Le changeur de voix n'a pas pu démarrer : " + Core.FriendlyErrors.Describe(ex), 10);
        }
        finally
        {
            IsStarting = false;
        }
    }

    public void Stop(bool notify)
    {
        _main.Engine.Core.VoiceChanger = null;
        var session = _session;
        _session = null;
        IsOn = false;
        if (session != null) _ = Task.Run(session.Dispose);
        if (notify) _main.ShowToast("Changeur de voix désactivé : votre vraie voix passe de nouveau.");
    }

    private void OnStats(VoiceChangerStats stats)
    {
        if (!IsOn || stats.Silent) return;
        if (stats.Ms > 0) _msPerBlock = _msPerBlock * 0.8 + stats.Ms * 0.2;
        UpdateLiveInfo(stats.Load);
        if (stats.Reconfigured)
            _main.ShowToast("La carte graphique est très occupée (le jeu ?) : le changeur de voix prend un peu plus de délai pour rester fluide.", 6);
    }

    private void UpdateLiveInfo(double load)
    {
        if (_session == null && !IsStarting) return;
        var delay = _session?.EstimatedDelayMs(_msPerBlock) ?? 0;
        LiveInfo = $"{GpuName} · délai d'environ {delay / 1000.0:0.0} s · carte graphique utilisée à {Math.Clamp(load, 0, 9.99) * 100:0} % du temps";
    }

    private void OnFailed(VoiceChangerError error)
    {
        if (!error.Fatal) return;
        App.Log(new InvalidOperationException($"Changeur de voix ({error.Code}) : {error.Message}"));
        if (!IsOn) return;
        Stop(notify: false);
        _main.ShowToast("Le changeur de voix s'est arrêté : " + error.Message + " Votre vraie voix passe de nouveau.", 10);
    }

    // ---- Models ------------------------------------------------------------------------------------------------

    private bool CanDownload() => !IsInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task Download() => InstallAsync();

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private async Task<bool> InstallAsync()
    {
        if (IsDownloading) return false;
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadStatus = $"Téléchargement… 0 / {PackSizeText}";
        var progress = new Progress<VoiceDownloadProgress>(p =>
        {
            DownloadPercent = p.Percent;
            DownloadStatus = $"Téléchargement… {VoiceModelStore.FormatSize(p.Done)} / {PackSizeText}";
        });
        try
        {
            await _store.InstallAsync(Pack, progress, _downloadCts.Token);
            IsInstalled = _store.IsInstalled(Pack);
            return IsInstalled;
        }
        catch (OperationCanceledException)
        {
            _main.ShowToast("Téléchargement annulé : il reprendra là où il s'est arrêté.");
        }
        catch (DownloadException ex)
        {
            App.Log(ex);
            _main.ShowToast(Core.FriendlyErrors.Describe(ex), 10);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _main.ShowToast("Téléchargement du changeur de voix impossible : " + Core.FriendlyErrors.Describe(ex), 10);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
        return false;
    }

    private bool CanDelete() => IsInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        if (System.Windows.MessageBox.Show($"Supprimer les modèles du changeur de voix ({PackSizeText}) ? Vous pourrez les télécharger à nouveau.",
                "Changeur de voix", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;
        Stop(notify: false);
        try
        {
            await Task.Run(() => _store.Delete(Pack));
        }
        catch (Exception ex)
        {
            _main.ShowToast("Suppression impossible : " + Core.FriendlyErrors.Describe(ex));
        }
        IsInstalled = _store.IsInstalled(Pack);
    }

    [RelayCommand]
    private void OpenLicense()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Pack.LicenseUrl) { UseShellExecute = true });
        }
        catch
        {
            // No browser.
        }
    }

    public void Shutdown()
    {
        _main.Engine.Core.VoiceChanger = null;
        _session?.Dispose();
        _session = null;
    }
}
