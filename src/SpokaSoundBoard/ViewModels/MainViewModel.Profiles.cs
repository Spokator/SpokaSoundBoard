using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

/// <summary>A choice of a profile combo box (« Ne pas changer » carries <see cref="ProfileChoice.Keep"/>).</summary>
public sealed record ProfileChoice(string Label, string Value)
{
    /// <summary>Stands for null (« ne pas changer ») in combo boxes, which cannot select a null value.</summary>
    public const string Keep = "\u0001";

    /// <summary>What screen readers announce for the choice.</summary>
    public override string ToString() => Label;
}

/// <summary>« Profils par jeu » (Configuration page): settings applied while a game runs.</summary>
public partial class MainViewModel
{
    private GameProfileSwitcher? _profiles;
    private GameWatcher? _gameWatcher;

    public ObservableCollection<GameProfile> GameProfiles => Settings.GameProfiles;

    public ObservableCollection<ProfileChoice> ProfileCategoryChoices { get; } = [];
    public ObservableCollection<ProfileChoice> ProfilePresetChoices { get; } = [];
    public ObservableCollection<ProfileChoice> RunningApps { get; } = [];

    [ObservableProperty] private string _activeProfileText = "Aucun profil actif.";

    public bool HasGameProfiles => GameProfiles.Count > 0;

    /// <summary>Name of the active profile (phone remote), null when none.</summary>
    public string? ActiveProfileName => _profiles?.Active?.Name;

    private void StartGameProfiles()
    {
        _profiles = new GameProfileSwitcher(new ProfileTarget(this));
        _profiles.ActiveChanged += () =>
        {
            ActiveProfileText = _profiles.Active is { } p ? $"Profil actif : {p.Name}" : "Aucun profil actif.";
            OnPropertyChanged(nameof(ActiveProfileName));
        };
        GameProfiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasGameProfiles));
        // Read on the timer thread: a plain flag, no collection enumeration there.
        _gameWatcher = new GameWatcher(() => Volatile.Read(ref _profilesEnabled),
            (running, front) => _dispatcher.BeginInvoke(() => _profiles?.Update(GameProfiles, running, front, DateTime.UtcNow)));
        RefreshProfilesEnabled();
        GameProfiles.CollectionChanged += (_, _) => RefreshProfilesEnabled();
    }

    private bool _profilesEnabled;

    /// <summary>Called when profiles are added, removed, switched on or off.</summary>
    public void RefreshProfilesEnabled()
    {
        foreach (var p in GameProfiles)
        {
            p.PropertyChanged -= OnProfileChanged;
            p.PropertyChanged += OnProfileChanged;
        }
        Volatile.Write(ref _profilesEnabled, GameProfiles.Any(p => p.Enabled && p.ProcessName.Length > 0));
        if (!_profilesEnabled) _profiles?.Stop();
    }

    private void OnProfileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameProfile.Enabled) or nameof(GameProfile.ProcessName)) RefreshProfilesEnabled();
    }

    /// <summary>Choices of the combo boxes, refreshed when the Configuration page opens.</summary>
    public void RefreshProfileChoices()
    {
        Fill(ProfileCategoryChoices, [new("Ne pas changer", ProfileChoice.Keep), new("Tous les sons", ""),
            .. Settings.PadCategories.Select(c => new ProfileChoice(c, c))]);
        Fill(ProfilePresetChoices, [new("Ne pas changer", ProfileChoice.Keep),
            .. Voice.Presets.Select(p => new ProfileChoice(p.Name, p.Name))]);

        static void Fill(ObservableCollection<ProfileChoice> list, List<ProfileChoice> items)
        {
            if (list.SequenceEqual(items)) return;
            list.Clear();
            foreach (var item in items) list.Add(item);
        }
    }

    [RelayCommand]
    private void RefreshRunningApps()
    {
        RunningApps.Clear();
        foreach (var (title, process) in GameWatcher.WindowedApps())
            if (!GameProfiles.Any(p => p.ProcessName == process))
                RunningApps.Add(new ProfileChoice($"{title}  ({process}.exe)", process));
    }

    [RelayCommand]
    private void AddRunningGame(ProfileChoice? app)
    {
        if (app == null) return;
        var title = app.Label[..app.Label.LastIndexOf("  (", StringComparison.Ordinal)].Trim();
        AddProfile(title, app.Value);
    }

    [RelayCommand]
    private void AddGameProgram()
    {
        var dialog = new OpenFileDialog { Filter = "Programmes (*.exe)|*.exe", Title = "Choisir le programme du jeu" };
        if (dialog.ShowDialog() != true) return;
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(dialog.FileName);
        var name = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName!.Trim() : System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        AddProfile(name, dialog.FileName);
    }

    private void AddProfile(string name, string program)
    {
        var process = GameProfile.NormalizeProcess(program);
        if (process.Length == 0) return;
        if (GameProfiles.FirstOrDefault(p => p.ProcessName == process) is { } existing)
        {
            ShowToast($"« {existing.Name} » a déjà un profil.");
            return;
        }
        // A useful start: the current voice effect and tab, ready to adjust.
        GameProfiles.Add(new GameProfile { Name = name, ProcessName = process });
        RefreshProfileChoices();
        Save();
        ShowToast($"Profil « {name} » ajouté : choisissez ce qui change pendant ce jeu.");
    }

    [RelayCommand]
    private void RemoveProfile(GameProfile? profile)
    {
        if (profile == null) return;
        if (ReferenceEquals(_profiles?.Active, profile)) _profiles!.Stop();
        GameProfiles.Remove(profile);
        Save();
    }

    private void StopGameProfiles()
    {
        _gameWatcher?.Dispose();
        _profiles?.Stop();
    }

    /// <summary>The app's settings as seen by the profile switcher (UI thread).</summary>
    private sealed class ProfileTarget(MainViewModel vm) : IProfileTarget
    {
        public string Category
        {
            get => vm.Settings.Appearance.PadCategory;
            set
            {
                if (value.Length == 0 || vm.Settings.PadCategories.Contains(value, StringComparer.CurrentCultureIgnoreCase)) vm.Settings.Appearance.PadCategory = value;
            }
        }

        public bool Effects
        {
            get => vm.VoiceSettings.EffectsEnabled;
            set => vm.VoiceSettings.EffectsEnabled = value;
        }

        public bool Radio
        {
            get => vm.Mix.PushToTalkEnabled;
            set => vm.Mix.PushToTalkEnabled = value;
        }

        public bool NoiseSuppression
        {
            get => vm.Mix.NoiseSuppressionEnabled;
            set
            {
                if (vm.NoiseSuppressionAvailable) vm.Mix.NoiseSuppressionEnabled = value;
            }
        }

        public bool MediaToMic
        {
            get => vm.Mix.MediaToMicEnabled;
            set => vm.Mix.MediaToMicEnabled = value;
        }

        public string? Preset => vm.Voice.SelectedPresetName;

        public bool ApplyPreset(string name) => vm.Voice.ApplyPresetByName(name);

        public object CaptureVoice() => (vm.Voice.SelectedPresetName, vm.VoiceSettings.Clone());

        public void RestoreVoice(object snapshot)
        {
            var (name, settings) = ((string?, VoiceEffectSettings))snapshot;
            vm.VoiceSettings.CopyEffectsFrom(settings);
            vm.VoiceSettings.EffectsEnabled = settings.EffectsEnabled;
            vm.Voice.SelectedPresetName = name;
        }

        public void Notify(string message) => vm.ShowToast(message, 5);
    }
}
