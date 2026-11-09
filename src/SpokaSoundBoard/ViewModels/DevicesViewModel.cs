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
        var s = _main.Settings;

        SelectedMic = Inputs.FirstOrDefault(d => d.Id == s.MicDeviceId) ?? DeviceService.FindDefaultMicrophone(Inputs);
        SelectedVirtualOutput = Outputs.FirstOrDefault(d => d.Id == s.VirtualOutputDeviceId) ?? DeviceService.FindVirtualCableOutput(Outputs);
        SelectedDiscordOutput = Outputs.FirstOrDefault(d => d.Id == s.DiscordOutputDeviceId)
                                ?? (s.DiscordOutputDisabled ? null : DeviceService.FindSecondCableOutput(Outputs, SelectedVirtualOutput));
        SelectedMonitor = Outputs.FirstOrDefault(d => d.Id == s.MonitorDeviceId) ?? DeviceService.FindDefaultMonitor(Outputs);
        StoreSelection();
        _suppressApply = false;
    }

    [RelayCommand]
    public void Refresh()
    {
        var hadCable = HasVirtualCable;
        var hadSecond = HasSecondCable;
        var hadSpoka = HasSpokaCable;
        _suppressApply = true;
        var mic = SelectedMic?.Id;
        var virt = SelectedVirtualOutput?.Id;
        var discord = SelectedDiscordOutput?.Id;
        var mon = SelectedMonitor?.Id;
        LoadDevices();
        SelectedMic = Inputs.FirstOrDefault(d => d.Id == mic) ?? DeviceService.FindDefaultMicrophone(Inputs);
        SelectedVirtualOutput = Outputs.FirstOrDefault(d => d.Id == virt) ?? DeviceService.FindVirtualCableOutput(Outputs);
        SelectedDiscordOutput = Outputs.FirstOrDefault(d => d.Id == discord);
        // A second cable just appeared: use it for Discord automatically.
        if (SelectedDiscordOutput == null && !_main.Settings.DiscordOutputDisabled && HasSecondCable)
            SelectedDiscordOutput = DeviceService.FindSecondCableOutput(Outputs, SelectedVirtualOutput);
        SelectedMonitor = Outputs.FirstOrDefault(d => d.Id == mon) ?? DeviceService.FindDefaultMonitor(Outputs);
        _suppressApply = false;
        Apply();
        if (!hadCable && HasVirtualCable) _main.ShowToast(VirtualCableStatus.Replace(" et prêt.", " !"));
        else if (!hadSpoka && SpokaCableUnused) _main.ShowToast("Spoka Cable détecté : activez-le dans Configuration.");
        else if (!hadSecond && HasSecondCable) _main.ShowToast("Deuxième micro virtuel détecté : sortie Discord configurée !");
    }

    /// <summary>Switches the game output to Spoka Cable; the other cable stays available for Discord.</summary>
    [RelayCommand]
    private void UseSpokaCable()
    {
        if (DeviceService.FindSpokaCableOutput(Outputs) is not { } spoka) return;
        SelectedVirtualOutput = spoka;
        _main.ShowToast($"Spoka Cable activé : dans Discord et vos jeux, choisissez « {VirtualMicName} » comme micro.");
    }

    [RelayCommand]
    private void Apply()
    {
        StoreSelection();
        _main.RestartEngine();
        _main.Save();
    }

    [RelayCommand]
    private void ClearVirtualOutput() => SelectedVirtualOutput = null;

    [RelayCommand]
    private void ClearMonitor() => SelectedMonitor = null;

    [RelayCommand]
    private void ClearDiscordOutput()
    {
        _main.Settings.DiscordOutputDisabled = true;
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
            SecondInstallStatus = "Échec : " + ex.Message + " — utilisez le lien vers le site officiel.";
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
            InstallStatus = "Échec : " + ex.Message + " — utilisez le lien vers le site officiel.";
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
            _main.ShowToast("Impossible de lister les périphériques audio : " + ex.Message);
        }
        HasVirtualCable = Outputs.Any(o => o.IsVirtualCable);
        HasSecondCable = DeviceService.CountDistinctCables(Outputs) >= 2;
        HasSpokaCable = DeviceService.FindSpokaCableOutput(Outputs) != null;
    }

    private void StoreSelection()
    {
        var s = _main.Settings;
        s.MicDeviceId = SelectedMic?.Id;
        s.VirtualOutputDeviceId = SelectedVirtualOutput?.Id;
        s.DiscordOutputDeviceId = SelectedDiscordOutput?.Id;
        s.MonitorDeviceId = SelectedMonitor?.Id;
        // Names derived from the device lists, which change without a property notification.
        foreach (var name in (string[])[nameof(VirtualMicName), nameof(DiscordMicName), nameof(GuideMicName), nameof(GuideDiscordMicName),
                     nameof(VirtualCableStatus), nameof(VirtualOutputHint), nameof(SpokaCableUnused), nameof(DiscordSameCableWarning)])
            OnPropertyChanged(name);
    }

    partial void OnSelectedMicChanged(AudioDevice? value) => AutoApply();
    partial void OnSelectedVirtualOutputChanged(AudioDevice? value) => AutoApply();
    partial void OnSelectedMonitorChanged(AudioDevice? value) => AutoApply();
    partial void OnSelectedDiscordOutputChanged(AudioDevice? value)
    {
        if (value != null) _main.Settings.DiscordOutputDisabled = false;
        AutoApply();
    }

    private void AutoApply()
    {
        if (_suppressApply) return;
        Apply();
    }
}
