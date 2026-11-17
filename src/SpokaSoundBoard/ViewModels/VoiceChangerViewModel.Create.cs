using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.ViewModels;

/// <summary>A recording or an imported file going into a new voice.</summary>
public sealed record VoiceClip(string Path, string Label, double Seconds, bool IsRecording);

/// <summary>« Mes voix »: making a voice from your own recordings or audio files.</summary>
public sealed partial class VoiceChangerViewModel
{
    private VoiceRecorder? _recorder;
    private DispatcherTimer? _recordTimer;
    private string? _draftDirectory;

    public static VoiceModelPack CreatorPack => VoiceChangerCatalog.Creator;

    public ObservableCollection<VoiceClip> Clips { get; } = [];

    [ObservableProperty] private bool _isCreating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceCommand))]
    private string _newVoiceName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceCommand))]
    private bool _consent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceCommand))]
    private bool _isRecording;

    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private double _recordingLevel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateVoiceCommand))]
    private bool _isBuilding;

    [ObservableProperty] private string _buildStatus = "";

    /// <summary>Total length of the clips, and what it means.</summary>
    public string ClipsSummary
    {
        get
        {
            var total = Clips.Sum(c => c.Seconds);
            if (Clips.Count == 0) return $"Ajoutez au moins {SpeechSelector.MinimumSeconds:0} secondes de voix ({SpeechSelector.RecommendedSeconds:0} à 60 s, c'est mieux).";
            var advice = total < SpeechSelector.MinimumSeconds ? " : encore un peu, il en faut au moins 5 s."
                : total < SpeechSelector.RecommendedSeconds ? " : c'est assez, mais 20 s ou plus donneront une voix plus fidèle."
                : " : parfait.";
            return $"{Clips.Count} extrait(s), {total:0} s au total{advice}";
        }
    }

    [RelayCommand]
    private void OpenCreator()
    {
        IsCreating = true;
        if (NewVoiceName.Length == 0) NewVoiceName = $"Ma voix {Library.Load().Count + 1}";
    }

    [RelayCommand]
    private async Task CancelCreator()
    {
        if (IsRecording) await StopRecordingAsync(keep: false);
        IsCreating = false;
        ClearDraft();
    }

    private string DraftDirectory => _draftDirectory ??= Path.Combine(Library.Directory, "_brouillon");

    private void ClearDraft()
    {
        Clips.Clear();
        OnPropertyChanged(nameof(ClipsSummary));
        Consent = false;
        try
        {
            if (Directory.Exists(DraftDirectory)) Directory.Delete(DraftDirectory, true);
        }
        catch (IOException)
        {
            // Left for next time.
        }
    }

    // ---- Recording with the microphone ----------------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleRecording()
    {
        if (IsRecording)
        {
            await StopRecordingAsync(keep: true);
            return;
        }
        if (_main.Engine.Core.Recorder != null)
        {
            _main.ShowToast("Un enregistrement est déjà en cours dans la soundboard.");
            return;
        }
        _recorder = new VoiceRecorder(MixerCore.SampleRate);
        _main.Engine.Core.Recorder = _recorder;
        IsRecording = true;
        RecordingSeconds = 0;
        _recordTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            if (_recorder == null) return;
            RecordingSeconds = _recorder.Elapsed.TotalSeconds;
            RecordingLevel = _recorder.RecentPeak;
            if (_recorder.IsFull) _ = StopRecordingAsync(keep: true);
        }, _dispatcher);
        _recordTimer.Start();
    }

    private async Task StopRecordingAsync(bool keep)
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _recorder = null;
        _recordTimer?.Stop();
        _main.Engine.Core.Recorder = null;
        IsRecording = false;
        RecordingLevel = 0;
        if (!keep)
        {
            await recorder.CancelAsync();
            return;
        }
        Directory.CreateDirectory(DraftDirectory);
        var path = Path.Combine(DraftDirectory, $"enregistrement-{Clips.Count(c => c.IsRecording) + 1}.wav");
        var length = await recorder.StopAsync(path);
        if (length < TimeSpan.FromSeconds(0.5))
        {
            _main.ShowToast("Rien n'a été entendu : vérifiez le micro choisi dans Configuration.");
            return;
        }
        Clips.Add(new VoiceClip(path, $"Enregistrement {Clips.Count(c => c.IsRecording) + 1}", length.TotalSeconds, true));
        OnPropertyChanged(nameof(ClipsSummary));
    }

    // ---- Importing files --------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ImportClips()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Fichiers de la voix (dialogues, répliques…)",
            Filter = AudioFileLoader.DialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        var failed = new List<(string Name, Exception Error)>();
        foreach (var file in dialog.FileNames)
        {
            if (Clips.Any(c => string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase))) continue; // already in
            try
            {
                var seconds = await Task.Run(() => DurationOf(file));
                Clips.Add(new VoiceClip(file, Path.GetFileName(file), seconds, false));
            }
            catch (Exception ex)
            {
                App.Log(new InvalidOperationException($"Mes voix : « {file} » illisible.", ex));
                failed.Add((Path.GetFileName(file), ex));
            }
        }
        OnPropertyChanged(nameof(ClipsSummary));
        // One message for all of them (one toast per file would hide all but the last).
        if (failed.Count == 1)
            _main.ShowToast($"« {failed[0].Name} » ne peut pas être lu : " + Core.FriendlyErrors.Describe(failed[0].Error), 10);
        else if (failed.Count > 1)
            _main.ShowToast($"{failed.Count} fichiers sur {dialog.FileNames.Length} ne peuvent pas être lus ({string.Join(", ", failed.Take(3).Select(f => $"« {f.Name} »"))}" +
                            $"{(failed.Count > 3 ? "…" : "")}) : " + Core.FriendlyErrors.Describe(failed[0].Error), 12);
    }

    private static double DurationOf(string file)
    {
        var (reader, _) = AudioFileLoader.Open(file, MixerCore.SampleRate);
        using (reader) return reader.TotalTime.TotalSeconds;
    }

    [RelayCommand]
    private void RemoveClip(VoiceClip? clip)
    {
        if (clip == null) return;
        Clips.Remove(clip);
        OnPropertyChanged(nameof(ClipsSummary));
    }

    // ---- Building the voice -----------------------------------------------------------------------------------

    private bool CanCreateVoice() => Consent && !IsBuilding && !IsRecording && Clips.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCreateVoice))]
    private async Task CreateVoice()
    {
        IsBuilding = true;
        try
        {
            if (!IsEngineAvailable) throw new InvalidOperationException("le moteur du changeur de voix n'est pas installé avec cette version.");
            if (!IsInstalled)
            {
                BuildStatus = "Téléchargement des modèles du changeur de voix…";
                if (!await InstallAsync()) return;
            }
            if (!_store.IsInstalled(CreatorPack))
            {
                BuildStatus = $"Téléchargement de l'outil de création de voix ({VoiceModelStore.FormatSize(CreatorPack.TotalSize)})…";
                await _store.InstallAsync(CreatorPack, new Progress<VoiceDownloadProgress>(p =>
                    BuildStatus = $"Téléchargement de l'outil de création de voix… {p.Percent:0} %"), CancellationToken.None);
            }

            BuildStatus = "Écoute des enregistrements…";
            var files = Clips.Select(c => c.Path).ToList();
            var speech = await Task.Run(() => SpeechSelector.Select(VoiceImprintMaker.Decode(files), MixerCore.SampleRate));
            if (speech.Seconds < SpeechSelector.MinimumSeconds)
            {
                _main.ShowToast($"Pas assez de voix : {speech.Seconds:0.0} s entendues (il en faut au moins {SpeechSelector.MinimumSeconds:0}). " +
                                "Ajoutez des enregistrements, ou des fichiers où l'on entend surtout la voix.", 10);
                return;
            }

            BuildStatus = $"Création de la voix à partir de {speech.Seconds:0} s de parole…";
            var imprint = await VoiceImprintMaker.BuildAsync(EnginePath, _store.PackDirectory(Pack), _store.PackDirectory(CreatorPack), speech);
            var voice = Library.Add(NewVoiceName, imprint, speech.Seconds);
            RefreshVoices();
            SelectVoice(Voices.FirstOrDefault(v => v.Key == voice.Key));

            var hint = speech.Clipped > 0.002 ? " Certains passages saturaient : parlez un peu moins fort ou plus loin du micro pour une voix plus propre." : "";
            _main.ShowToast($"Voix « {voice.Name} » créée à partir de {speech.Seconds:0} s de parole.{hint}", 8);
            IsCreating = false;
            NewVoiceName = "";
            ClearDraft();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _main.ShowToast("La voix n'a pas pu être créée : " + Core.FriendlyErrors.Describe(ex), 10);
        }
        finally
        {
            IsBuilding = false;
            BuildStatus = "";
        }
    }

    [RelayCommand]
    private void DeleteVoice(VoiceChangerOption? option)
    {
        if (option?.Custom is not { } custom) return;
        if (System.Windows.MessageBox.Show($"Supprimer la voix « {custom.Name} » ? Ses enregistrements d'origine ne sont pas touchés.",
                "Mes voix", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;
        var wasCurrent = option.IsSelected;
        if (wasCurrent && IsOn) Stop(notify: false);
        Library.Delete(custom);
        if (wasCurrent)
        {
            Settings.VoiceKey = VoiceChangerCatalog.DefaultVoiceKey;
            _main.Save();
        }
        RefreshVoices();
    }
}
