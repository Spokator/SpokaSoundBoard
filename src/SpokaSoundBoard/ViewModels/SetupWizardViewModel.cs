using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

/// <summary>First-run assistant: microphone, virtual cables (with reboot + resume), Discord / game setup.</summary>
public partial class SetupWizardViewModel : ObservableObject
{
    public const int StepWelcome = 0;
    public const int StepMicrophone = 1;
    public const int StepVirtualMic = 2;
    public const int StepDiscord = 3;
    public const int StepDone = 4;
    private const int StepCount = 5;

    private readonly MainViewModel _main;

    public SetupWizardViewModel(MainViewModel main)
    {
        _main = main;
        main.Devices.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DevicesViewModel.SelectedDiscordOutput) or nameof(DevicesViewModel.SelectedVirtualOutput)
                or nameof(DevicesViewModel.HasVirtualCable) or nameof(DevicesViewModel.VirtualMicName) or nameof(DevicesViewModel.DiscordMicName))
                OnPropertyChanged(nameof(DiscordInputName));
        };
    }

    /// <summary>The device to pick as Discord's input, depending on what is installed.</summary>
    public string DiscordInputName =>
        Devices.SelectedDiscordOutput is { IsVirtualCable: true } ? Devices.DiscordMicName
        : Devices.SelectedVirtualOutput is { IsVirtualCable: true } ? Devices.VirtualMicName
        : "votre vrai micro (en attendant le micro virtuel)";

    public DevicesViewModel Devices => _main.Devices;

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(CanGoBack), nameof(NextLabel), nameof(StepNumber))]
    private int _step;

    /// <summary>A driver was installed during this session; Windows usually needs a restart to show it.</summary>
    [ObservableProperty] private bool _needsReboot;

    [ObservableProperty] private bool _microphoneBlocked;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private string? _copiedMessage;

    public int StepNumber => Step + 1;
    public int TotalSteps => StepCount;
    public bool CanGoBack => Step > StepWelcome && Step < StepDone;
    public string NextLabel => Step switch
    {
        StepWelcome => "Commencer",
        StepDone => "Terminer",
        _ => "Suivant",
    };

    public string StepTitle => Step switch
    {
        StepWelcome => "Bienvenue",
        StepMicrophone => "Votre micro",
        StepVirtualMic => "Micro virtuel",
        StepDiscord => "Discord et jeux",
        _ => "C'est prêt !",
    };

    public void Open(int step = StepWelcome)
    {
        Autostart = WindowsIntegration.IsAutostartEnabled();
        MicrophoneBlocked = WindowsIntegration.IsMicrophoneAccessDenied();
        Devices.RefreshCommand.Execute(null);
        Step = Math.Clamp(step, StepWelcome, StepDone);
        IsOpen = true;
    }

    [RelayCommand]
    private void Next()
    {
        if (Step >= StepDone)
        {
            Finish();
            return;
        }
        Step++;
        OnEnterStep();
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack) Step--;
        OnEnterStep();
    }

    [RelayCommand]
    private void Close() => Finish();

    [RelayCommand]
    private void RecheckMicrophone()
    {
        MicrophoneBlocked = WindowsIntegration.IsMicrophoneAccessDenied();
        Devices.RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private static void OpenMicrophonePrivacy() => WindowsIntegration.Open("ms-settings:privacy-microphone");

    [RelayCommand]
    private async Task InstallVirtualMic()
    {
        await Devices.InstallVbCableCommand.ExecuteAsync(null);
        if (!Devices.HasVirtualCable) NeedsReboot = true;
    }

    [RelayCommand]
    private async Task InstallSecondCable()
    {
        await Devices.InstallSecondCableCommand.ExecuteAsync(null);
        if (!Devices.HasSecondCable) NeedsReboot = true;
    }

    [RelayCommand]
    private void RestartNow()
    {
        var answer = MessageBox.Show(
            "Le PC va redémarrer dans 5 secondes. Enregistrez votre travail dans les autres applications.\n\nSpokaSoundBoard se rouvrira automatiquement pour terminer la configuration.",
            "Redémarrer maintenant ?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        _main.Settings.SetupResumeStep = Step;
        _main.Save();
        WindowsIntegration.RestartAndResumeSetup();
    }

    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            CopiedMessage = $"« {text} » copié";
        }
        catch
        {
            // Clipboard busy.
        }
    }

    [RelayCommand]
    private static void OpenDiscordVoiceSettings() => WindowsIntegration.Open("discord://-/settings/voice");

    partial void OnAutostartChanged(bool value)
    {
        try
        {
            WindowsIntegration.SetAutostart(value);
        }
        catch
        {
            // Registry not writable: keep the app usable.
        }
    }

    private void OnEnterStep()
    {
        CopiedMessage = null;
        if (Step == StepMicrophone) MicrophoneBlocked = WindowsIntegration.IsMicrophoneAccessDenied();
        if (Step is StepVirtualMic or StepDiscord) Devices.RefreshCommand.Execute(null);
    }

    private void Finish()
    {
        IsOpen = false;
        _main.Settings.FirstRunDone = true;
        _main.Settings.SetupResumeStep = null;
        _main.Save();
    }
}
