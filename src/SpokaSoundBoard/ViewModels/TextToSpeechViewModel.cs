using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

/// <summary>
/// « Synthèse vocale » panel of the soundboard: a Windows voice reads a phrase into the microphone. The phrase is
/// rendered to a WAV file and played by the soundboard player, so it follows the « Sons » routing like any sound.
/// </summary>
public sealed partial class TextToSpeechViewModel : ObservableObject
{
    /// <summary>Soundboard player id of the spoken phrases (one at a time).</summary>
    public static readonly Guid SpeechPadId = new("6b1f3c2e-9a57-4d1e-8e0b-5c7a2f9d4e11");

    public const string SoundsFolder = "Sons";

    private readonly MainViewModel _main;
    private readonly SoundboardViewModel _soundboard;
    private readonly DispatcherTimer _playingTimer;
    private string? _lastPath;
    private string? _lastText;

    public TextToSpeechViewModel(MainViewModel main, SoundboardViewModel soundboard)
    {
        _main = main;
        _soundboard = soundboard;
        Speech = new SpeechService(Path.Combine(main.DataDirectory, "Synthese"));
        Voices = Speech.Voices;
        _selectedVoice = Voices.FirstOrDefault(v => v.Id == Settings.VoiceId) ?? Speech.DefaultVoice;
        foreach (var h in Settings.History) History.Add(h);
        _playingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => RefreshPlaying(),
            Dispatcher.CurrentDispatcher);
        _playingTimer.Stop();
    }

    public SpeechService Speech { get; }
    public TextToSpeechSettings Settings => _main.Settings.TextToSpeech;
    public IReadOnlyList<SpeechVoice> Voices { get; }
    public bool IsAvailable => Speech.IsAvailable;
    public bool HasNoFrenchVoice => IsAvailable && !Voices.Any(v => v.IsFrench);

    public ObservableCollection<string> History { get; } = [];

    private static readonly string[] Phrases =
    [
        "GG !",
        "Bien joué l'équipe !",
        "On lâche rien !",
        "Attention, derrière toi !",
        "Bonne chance, amusez-vous bien !",
        "Je reviens dans deux minutes.",
        "Merci à tous, bonne soirée !",
    ];

    public IReadOnlyList<string> QuickPhrases => Phrases;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SpeakCommand))]
    private string _text = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSpeaking;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsPadCommand))]
    private bool _hasLastPhrase;

    [ObservableProperty] private SpeechVoice? _selectedVoice;

    partial void OnSelectedVoiceChanged(SpeechVoice? value) => Settings.VoiceId = value?.Id;

    [RelayCommand]
    private void ToggleExpanded() => Settings.IsExpanded = !Settings.IsExpanded;

    private bool CanSpeak() => !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanSpeak))]
    private async Task Speak()
    {
        if (await SayAsync(Text, remember: true) != null) Text = "";
    }

    [RelayCommand]
    private Task SpeakPhrase(string? phrase) => SayAsync(phrase, remember: true);

    [RelayCommand]
    private void Stop()
    {
        _main.Engine.Pads.Stop(SpeechPadId);
        IsSpeaking = false;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Settings.History.Clear();
        History.Clear();
        _main.Save();
    }

    /// <summary>Speaks <paramref name="text"/> into the microphone. Returns the rendered file, or null (after a toast) on failure.</summary>
    public async Task<string?> SayAsync(string? text, bool remember)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (!IsAvailable)
        {
            _main.ShowToast("Aucune voix Windows installée : ajoutez-en une dans Paramètres Windows → Heure et langue → Voix.");
            return null;
        }
        var path = await RenderAsync(text);
        if (path == null) return null;
        if (remember) Remember(text);
        try
        {
            _main.Engine.Pads.Stop(SpeechPadId);
            IsSpeaking = true;
            _speakStarted = DateTime.UtcNow;
            _playingTimer.Start();
            await _main.Engine.Pads.PlayAsync(SpeechPadId, path, (float)Settings.Volume, false);
            return path;
        }
        catch (Exception ex)
        {
            IsSpeaking = false;
            _main.ShowToast("Lecture impossible de la synthèse vocale : " + ex.Message);
            return null;
        }
    }

    /// <summary>Renders the phrase with the current voice settings (null after a toast on failure).</summary>
    public async Task<string?> RenderAsync(string text)
    {
        IsBusy = true;
        try
        {
            var path = await Speech.SynthesizeAsync(text, SelectedVoice?.Id, Settings.Rate, Settings.Pitch);
            _lastPath = path;
            _lastText = text;
            HasLastPhrase = true;
            return path;
        }
        catch (Exception ex)
        {
            _main.ShowToast("Synthèse vocale impossible : " + ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveAsPad() => HasLastPhrase;

    /// <summary>Copies the last phrase into the soundboard library and adds a sound for it (opens its editor for a hotkey).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAsPad))]
    private void SaveAsPad()
    {
        if (_lastPath == null || _lastText == null || !File.Exists(_lastPath)) return;
        try
        {
            var dir = Path.Combine(_main.Downloader.CacheDirectory, SoundsFolder);
            Directory.CreateDirectory(dir);
            var baseName = SafeFileName(_lastText);
            var target = Path.Combine(dir, baseName + ".wav");
            for (var i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{baseName} ({i}).wav");
            File.Copy(_lastPath, target);
            _soundboard.AddFiles([target]);
            if (_soundboard.SelectedPad is { } pad && pad.FilePath == target)
            {
                pad.Name = _lastText.Length > 40 ? _lastText[..40].TrimEnd() + "…" : _lastText;
                pad.Volume = (float)Settings.Volume;
                _main.Save();
            }
            _main.ShowToast("Phrase ajoutée à la soundboard : donnez-lui un raccourci dans le panneau de droite.");
        }
        catch (Exception ex)
        {
            _main.ShowToast("Impossible d'enregistrer la phrase : " + ex.Message);
        }
    }

    private void Remember(string text)
    {
        Settings.Remember(text);
        History.Clear();
        foreach (var h in Settings.History) History.Add(h);
        _main.Save();
    }

    private DateTime _speakStarted;

    private void RefreshPlaying()
    {
        // The file is opened on a worker thread: give it a moment before trusting "not playing".
        if (DateTime.UtcNow - _speakStarted < TimeSpan.FromMilliseconds(500)) return;
        if (_main.Engine.Pads.IsPlaying(SpeechPadId)) return;
        IsSpeaking = false;
        _playingTimer.Stop();
    }

    private static string SafeFileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(text.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 40) clean = clean[..40].TrimEnd();
        return clean.Length == 0 ? "Synthèse vocale" : clean;
    }

}
