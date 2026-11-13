using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

public sealed record HotkeyRow(string Label, string Description, HotkeyBinding Binding);

public partial class DevicesViewModel : ObservableObject
{
    /// <summary>The Store edition cannot run driver installers: it opens the official download pages instead.</summary>
    public string InstallVbCableLabel => Core.AppEnvironment.IsPackaged ? "Télécharger VB-CABLE (site officiel)" : "Installer VB-CABLE automatiquement";

    public string InstallVacLabel => Core.AppEnvironment.IsPackaged ? "Télécharger Virtual Audio Cable Lite" : "Installer Virtual Audio Cable Lite";

    private readonly MainViewModel _main;
    private bool _suppressApply;

    public DevicesViewModel(MainViewModel main)
    {
        _main = main;
        var h = main.Settings.Hotkeys;
        HotkeyRows =
        [
            new("Mode radio (appuyer pour parler)", "Maintenir pour parler quand le mode radio est actif", h.PushToTalk),
            new("Lecture / pause musique", "Démarre ou met en pause le lecteur", h.PlayPause),
            new("Musique suivante", "Passe au morceau suivant", h.NextTrack),
            new("Musique plus fort", "Monte la musique de 5 % pour vous, le jeu et Discord (maintenir pour continuer)", h.MusicVolumeUp),
            new("Musique moins fort", "Baisse la musique de 5 % pour vous, le jeu et Discord (maintenir pour continuer)", h.MusicVolumeDown),
            new("Arrêter tous les sons", "Coupe tous les sons de la soundboard", h.StopAll),
            new("Couper / activer le micro", "Votre voix uniquement, la musique continue", h.ToggleMic),
            new("Activer / désactiver les effets", "Bascule rapide entre voix normale et effet", h.ToggleEffects),
            new("Musique dans le micro on/off", "Écouter la musique seul ou la partager", h.ToggleMediaToMic),
            new("Discord : partager musique & sons", "Vos amis Discord entendent (ou non) la musique et les sons", h.ToggleDiscordShare),
            new("Effet de voix suivant", "Passe au preset suivant (ex. normale → pilote → robot…)", h.NextPreset),
            new("Effet de voix précédent", "Revient au preset précédent", h.PreviousPreset),
        ];
    }

    public ObservableCollection<AudioDevice> Inputs { get; } = [];
    public ObservableCollection<AudioDevice> Outputs { get; } = [];
    public IReadOnlyList<HotkeyRow> HotkeyRows { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicIsVirtualWarning))]
    private AudioDevice? _selectedMic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VirtualOutputWarning))]
    private AudioDevice? _selectedVirtualOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscordSameCableWarning), nameof(HasDiscordOutput), nameof(DiscordMicName))]
    private AudioDevice? _selectedDiscordOutput;

    [ObservableProperty] private AudioDevice? _selectedMonitor;

    [ObservableProperty] private bool _hasSecondCable;
    [ObservableProperty] private bool _isInstallingSecond;
    [ObservableProperty] private string? _secondInstallStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VirtualOutputWarning))]
    private bool _hasVirtualCable;

    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string? _installStatus;

    public bool MicIsVirtualWarning => SelectedMic?.IsVirtualCable == true;

    public bool VirtualOutputWarning => HasVirtualCable && SelectedVirtualOutput is { IsVirtualCable: false };

    public bool HasDiscordOutput => SelectedDiscordOutput != null;

    /// <summary>Discord and the game share the same cable: they would hear exactly the same thing.</summary>
    public bool DiscordSameCableWarning => DeviceService.SameCable(SelectedDiscordOutput, SelectedVirtualOutput);

    public string DiscordMicName => SelectedDiscordOutput is not { IsVirtualCable: true }
        ? "votre vrai micro"
        : DeviceService.CableMicName(SelectedDiscordOutput, Inputs);

    /// <summary>The cable that carries the game mix: the selected one, or the one the app would pick.</summary>
    private AudioDevice? GameCable => SelectedVirtualOutput is { IsVirtualCable: true } selected
        ? selected
        : DeviceService.FindVirtualCableOutput(Outputs);

    /// <summary>Name to select in Discord / games.</summary>
    public string VirtualMicName => GameCable is { } cable
        ? DeviceService.CableMicName(cable, Inputs)
        : "CABLE Output (après installation du micro virtuel)";

    /// <summary>Microphone name quoted in the setup guide, short when no cable is installed yet.</summary>
    public string GuideMicName => GameCable is { } cable ? DeviceService.CableMicName(cable, Inputs) : "CABLE Output";

    public string GuideDiscordMicName => SelectedDiscordOutput is { IsVirtualCable: true } ? DiscordMicName : "Line 1";

    /// <summary>« Spoka Cable détecté et prêt. » / « VB-CABLE détecté et prêt. »</summary>
    public string VirtualCableStatus => GameCable is { } cable
        ? DeviceService.CableBrand(cable) + " détecté et prêt."
        : "Micro virtuel détecté et prêt.";

    public string VirtualOutputHint => DeviceService.FindSpokaCableOutput(Outputs) is { } spoka
        ? $"Recommandé : « {spoka.Name} »"
        : "Généralement « CABLE Input »";

    /// <summary>Our own driver is installed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpokaCableUnused), nameof(MainCableTitle), nameof(MainCableCaption))]
    private bool _hasSpokaCable;

    /// <summary>Spoka Cable is installed but neither the game nor Discord output uses it (set up before it existed).</summary>
    public bool SpokaCableUnused => HasSpokaCable
                                    && !DeviceService.IsSpokaCable(SelectedVirtualOutput)
                                    && !DeviceService.IsSpokaCable(SelectedDiscordOutput);

    public string MainCableTitle => HasSpokaCable ? "Micro virtuel principal (Spoka Cable)" : "Micro virtuel principal (VB-CABLE)";

    public string MainCableCaption => HasSpokaCable
        ? "Le pilote audio de SpokaSoundBoard, déjà installé sur ce PC."
        : "Indispensable. Gratuit (donationware), par VB-Audio.";

    public void InitializeSelection()
    {
        _suppressApply = true;
        LoadDevices();
        SelectFromSettings();
        StoreSelection();
        _suppressApply = false;
    }

    // A slot showing a stand-in for a saved device that is missing right now: its choice is not overwritten.
    private bool _micFallback, _virtualFallback, _discordFallback, _monitorFallback;

    /// <summary>Selects each device from the saved choice (not from the current selection, which may be a stand-in).</summary>
    private void SelectFromSettings()
    {
        var s = _main.Settings;
        (SelectedMic, _micFallback) = DeviceService.Resolve(Inputs, s.MicDeviceId, false,
            () => DeviceService.FindDefaultMicrophone(Inputs));
        (SelectedVirtualOutput, _virtualFallback) = DeviceService.Resolve(Outputs, s.VirtualOutputDeviceId, s.VirtualOutputDisabled,
            () => DeviceService.FindVirtualCableOutput(Outputs));
        (SelectedDiscordOutput, _discordFallback) = DeviceService.Resolve(Outputs, s.DiscordOutputDeviceId, s.DiscordOutputDisabled,
            () => DeviceService.FindSecondCableOutput(Outputs, SelectedVirtualOutput));
        (SelectedMonitor, _monitorFallback) = DeviceService.Resolve(Outputs, s.MonitorDeviceId, s.MonitorDisabled,
            () => DeviceService.FindDefaultMonitor(Outputs));
        WatchForReturn();
    }

    private System.Windows.Threading.DispatcherTimer? _returnWatch;

    /// <summary>While a stand-in is used, checks every few seconds whether the user's own device is back.</summary>
    private void WatchForReturn()
    {
        var waiting = _micFallback || _virtualFallback || _discordFallback || _monitorFallback;
        if (!waiting)
        {
            _returnWatch?.Stop();
            return;
        }
        if (_returnWatch is not { IsEnabled: true })
            _main.ShowToast("Un périphérique audio que vous avez choisi est débranché : un autre est utilisé en attendant son retour.", 6);
        _returnWatch ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(5),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => CheckReturn(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _returnWatch.Start();
    }

    private void CheckReturn()
    {
        try
        {
            var s = _main.Settings;
            var inputs = DeviceService.GetInputs();
            var outputs = DeviceService.GetOutputs();
            var back = _micFallback && inputs.Any(d => d.Id == s.MicDeviceId)
                       || _virtualFallback && outputs.Any(d => d.Id == s.VirtualOutputDeviceId)
                       || _discordFallback && outputs.Any(d => d.Id == s.DiscordOutputDeviceId)
                       || _monitorFallback && outputs.Any(d => d.Id == s.MonitorDeviceId);
            if (!back) return;
            Refresh();
            _main.ShowToast("Votre périphérique audio est revenu : il est de nouveau utilisé.");
        }
        catch (Exception)
        {
            // Device enumeration failing for a moment: try again on the next tick.
        }
    }

    [RelayCommand]
    public void Refresh() => Refresh(restartAlways: true);

    /// <summary>Windows reported a device change: reload the lists, restart the engine only if a device it uses changed.</summary>
    public void OnDevicesChanged() => Refresh(restartAlways: false);

    private (string?, string?, string?, string?) EffectiveIds() =>
        (SelectedMic?.Id, SelectedVirtualOutput?.Id, SelectedDiscordOutput?.Id, SelectedMonitor?.Id);

    private void Refresh(bool restartAlways)
    {
        var hadCable = HasVirtualCable;
        var hadSecond = HasSecondCable;
        var hadSpoka = HasSpokaCable;
        var before = EffectiveIds();
        _suppressApply = true;
        LoadDevices();
        SelectFromSettings();
        _suppressApply = false;
        if (!restartAlways && EffectiveIds() == before && _main.EngineRunning)
            StoreSelection();
        else
            Apply();
        if (!hadCable && HasVirtualCable) _main.ShowToast(VirtualCableStatus.Replace(" et prêt.", " !"));
        else if (!hadSpoka && SpokaCableUnused) _main.ShowToast("Spoka Cable détecté : activez-le dans Configuration.");
        else if (!hadSecond && HasSecondCable && SelectedDiscordOutput != null)
            _main.ShowToast("Deuxième micro virtuel détecté : sortie Discord configurée !");
    }

    [RelayCommand]
    private void UseSpokaCable()
    {
        if (DeviceService.FindSpokaCableOutput(Outputs) is not { } spoka) return;
        SelectedVirtualOutput = spoka;
        _main.ShowToast($"Spoka Cable activé : dans Discord et vos jeux, choisissez « {VirtualMicName} » comme micro.");
    }

    private void Apply()
    {
        StoreSelection();
        _main.RestartEngine();
        _main.Save();
    }

    [RelayCommand]
    private void ClearVirtualOutput()
    {
        _main.Settings.VirtualOutputDisabled = true;
        _virtualFallback = false;
        SelectedVirtualOutput = null;
    }

    [RelayCommand]
    private void ClearMonitor()
    {
        _main.Settings.MonitorDisabled = true;
        _monitorFallback = false;
        SelectedMonitor = null;
    }

    [RelayCommand]
    private void ClearDiscordOutput()
    {
        _main.Settings.DiscordOutputDisabled = true;
        _discordFallback = false;
        SelectedDiscordOutput = null;
    }

    [RelayCommand]
    private async Task InstallSecondCable()
    {
        if (IsInstallingSecond) return;
        IsInstallingSecond = true;
        try
        {
            await VbCableInstaller.DownloadAndRunVacLiteAsync(new Progress<string>(s => SecondInstallStatus = s));
            SecondInstallStatus = "Installation terminée ? Redémarrez le PC si demandé, puis cliquez sur « Actualiser ».";
            Refresh();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            SecondInstallStatus = "Installation annulée (autorisation administrateur refusée).";
        }
        catch (Exception ex)
        {
            SecondInstallStatus = "Échec : " + Core.FriendlyErrors.Describe(ex) + " — utilisez le lien vers le site officiel.";
        }
        finally
        {
            IsInstallingSecond = false;
        }
    }

    [RelayCommand]
    private void OpenVacSite() => VbCableInstaller.OpenUrl(VbCableInstaller.VacHomePage);

    [RelayCommand]
    private async Task InstallVbCable()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        try
        {
            await VbCableInstaller.DownloadAndRunAsync(new Progress<string>(s => InstallStatus = s));
            InstallStatus = "Installation terminée ? Redémarrez le PC si demandé, puis cliquez sur « Actualiser ».";
            Refresh();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            InstallStatus = "Installation annulée (autorisation administrateur refusée).";
        }
        catch (Exception ex)
        {
            InstallStatus = "Échec : " + Core.FriendlyErrors.Describe(ex) + " — utilisez le lien vers le site officiel.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void OpenVbCableSite() => VbCableInstaller.OpenHomePage();

    [RelayCommand]
    private void OpenWindowsSoundSettings() => VbCableInstaller.OpenUrl("ms-settings:sound");

    private void LoadDevices()
    {
        Inputs.Clear();
        Outputs.Clear();
        try
        {
            foreach (var d in DeviceService.GetInputs()) Inputs.Add(d);
            foreach (var d in DeviceService.GetOutputs()) Outputs.Add(d);
        }
        catch (Exception ex)
        {
            _main.ShowToast("Impossible de lister les périphériques audio : " + Core.FriendlyErrors.Describe(ex));
        }
        HasVirtualCable = Outputs.Any(o => o.IsVirtualCable);
        HasSecondCable = DeviceService.CountDistinctCables(Outputs) >= 2;
        HasSpokaCable = DeviceService.FindSpokaCableOutput(Outputs) != null;
    }

    private void StoreSelection()
    {
        var s = _main.Settings;
        // A stand-in for a device that is unplugged right now is used but not saved.
        if (!_micFallback) s.MicDeviceId = SelectedMic?.Id;
        if (!_virtualFallback) s.VirtualOutputDeviceId = SelectedVirtualOutput?.Id;
        if (!_discordFallback) s.DiscordOutputDeviceId = SelectedDiscordOutput?.Id;
        if (!_monitorFallback) s.MonitorDeviceId = SelectedMonitor?.Id;
        // Names derived from the device lists, which change without a property notification.
        foreach (var name in (string[])[nameof(VirtualMicName), nameof(DiscordMicName), nameof(GuideMicName), nameof(GuideDiscordMicName),
                     nameof(VirtualCableStatus), nameof(VirtualOutputHint), nameof(SpokaCableUnused), nameof(DiscordSameCableWarning)])
            OnPropertyChanged(name);
    }

    // Outside _suppressApply a change comes from the user: it is a real choice, saved as is.
    partial void OnSelectedMicChanged(AudioDevice? value)
    {
        if (!_suppressApply) _micFallback = false;
        AutoApply();
    }

    partial void OnSelectedVirtualOutputChanged(AudioDevice? value)
    {
        if (!_suppressApply) _virtualFallback = false;
        if (value != null && !_suppressApply) _main.Settings.VirtualOutputDisabled = false;
        AutoApply();
    }

    partial void OnSelectedMonitorChanged(AudioDevice? value)
    {
        if (!_suppressApply) _monitorFallback = false;
        if (value != null && !_suppressApply) _main.Settings.MonitorDisabled = false;
        AutoApply();
    }

    partial void OnSelectedDiscordOutputChanged(AudioDevice? value)
    {
        if (!_suppressApply) _discordFallback = false;
        if (value != null && !_suppressApply) _main.Settings.DiscordOutputDisabled = false;
        AutoApply();
    }

    private void AutoApply()
    {
        if (_suppressApply) return;
        Apply();
    }
}
