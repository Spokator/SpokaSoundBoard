using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Core.Speech;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels;

/// <summary>
/// « Synthèse vocale » panel of the soundboard: a voice reads a phrase into the microphone. Natural (neural) voices
/// are downloaded on demand and run offline; Windows voices are the fallback. The phrase is rendered to a WAV file
/// and played by the soundboard player, so it follows the « Sons » routing like any sound.
/// </summary>
public sealed partial class TextToSpeechViewModel : ObservableObject
{
    /// <summary>Soundboard player id of the spoken phrases (one at a time).</summary>
    public static readonly Guid SpeechPadId = new("6b1f3c2e-9a57-4d1e-8e0b-5c7a2f9d4e11");

    public const string SoundsFolder = "Sons";

    private readonly MainViewModel _main;
    private readonly SoundboardViewModel _soundboard;
    private readonly DispatcherTimer _playingTimer;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _downloadCts;
    private string? _lastPath;
    private string? _lastText;
    private bool _fallbackHintShown;

    public TextToSpeechViewModel(MainViewModel main, SoundboardViewModel soundboard)
    {
        _main = main;
        _soundboard = soundboard;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Speech = new SpeechService(Path.Combine(main.DataDirectory, "Synthese"), Path.Combine(main.DataDirectory, VoiceModelStore.FolderName));
        Pack = NeuralVoiceCatalog.Supertonic3;

        var installed = Speech.Models.IsInstalled(Pack);
        _isNaturalInstalled = installed;
        Voices =
        [
            .. NeuralVoiceCatalog.Voices.Select(v => TtsVoiceOption.FromNatural(v, installed)),
            .. Speech.Voices.Select(TtsVoiceOption.FromWindows),
        ];
        VoicesView = new ListCollectionView(Voices.ToList());
        VoicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TtsVoiceOption.Group)));
        _selectedVoice = Voices.FirstOrDefault(v => v.Id == Settings.VoiceId) ?? AutomaticVoice();
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == Settings.Language) ?? Languages[0];
        Speech.Models.Changed += _ => _dispatcher.BeginInvoke(RefreshInstalled);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextToSpeechSettings.IsExpanded)) OnPropertyChanged(nameof(ShowCollapsedHint));
        };

        foreach (var h in Settings.History) History.Add(h);
        _playingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => RefreshPlaying(),
            _dispatcher);
        _playingTimer.Stop();
    }

    public SpeechService Speech { get; }
    public TextToSpeechSettings Settings => _main.Settings.TextToSpeech;

    /// <summary>The downloadable natural voice model (all natural voices share it).</summary>
    public VoiceModelPack Pack { get; }

    /// <summary>Natural voices first, then Windows voices.</summary>
    public IReadOnlyList<TtsVoiceOption> Voices { get; }

    /// <summary><see cref="Voices"/> grouped by « Voix naturelles » / « Voix Windows » for the picker.</summary>
    public ListCollectionView VoicesView { get; }

    public IReadOnlyList<SpeechLanguage> Languages => NeuralVoiceCatalog.Languages;

    /// <summary>Something can speak: a Windows voice is installed or the natural voices are downloaded.</summary>
    public bool IsAvailable => Speech.HasWindowsVoices || IsNaturalInstalled;

    /// <summary>No French Windows voice and no natural voice: only foreign voices would read French text.</summary>
    public bool HasNoFrenchVoice => Speech.HasWindowsVoices && !IsNaturalInstalled && !Speech.Voices.Any(v => v.IsFrench);

    public string PackSizeText => VoiceModelStore.FormatSize(Pack.TotalSize);

    public string LicenseText =>
        $"{Pack.Name} (Supertone), licence {Pack.License} : usage libre, mais interdiction d'imiter une personne sans son accord, " +
        "de harceler ou de diffamer, et de faire passer la voix pour humaine. Le modèle fonctionne hors ligne.";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNaturalSelected), nameof(ShowPitch), nameof(SelectedNeedsDownload))]
    private TtsVoiceOption? _selectedVoice;

    [ObservableProperty] private SpeechLanguage? _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(HasNoFrenchVoice), nameof(ShowNaturalOffer), nameof(ShowCollapsedHint), nameof(SelectedNeedsDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNaturalCommand), nameof(DeleteNaturalCommand))]
    private bool _isNaturalInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNaturalOffer), nameof(ShowCollapsedHint))]
    [NotifyCanExecuteChangedFor(nameof(DownloadNaturalCommand), nameof(DeleteNaturalCommand))]
    private bool _isDownloading;

    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadStatus = "";

    public bool IsNaturalSelected => SelectedVoice?.IsNatural == true;

    /// <summary>Pitch only exists for Windows voices.</summary>
    public bool ShowPitch => !IsNaturalSelected;

    public bool SelectedNeedsDownload => SelectedVoice is { IsNatural: true, IsInstalled: false };

    /// <summary>« Télécharger les voix naturelles » block (until they are installed).</summary>
    public bool ShowNaturalOffer => !IsNaturalInstalled && !IsDownloading;

    /// <summary>One-line invitation under the text box while the options are folded.</summary>
    public bool ShowCollapsedHint => ShowNaturalOffer && !Settings.IsExpanded;

    partial void OnSelectedVoiceChanged(TtsVoiceOption? value) => Settings.VoiceId = value?.Id;

    partial void OnSelectedLanguageChanged(SpeechLanguage? value)
    {
        if (value != null) Settings.Language = value.Code;
    }

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
            _main.ShowToast($"Aucune voix disponible : téléchargez les voix naturelles ({PackSizeText}) dans les options de la synthèse vocale, " +
                            "ou ajoutez une voix dans Paramètres Windows → Heure et langue → Voix.");
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
            var path = await RenderWithAsync(SelectedVoice, text);
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

    /// <summary>Loads the natural voice in the background when it will be used (blind test start, panel opened).</summary>
    public void WarmUp()
    {
        if (ResolveVoice(SelectedVoice, notify: false)?.Natural is { } natural) _ = Speech.WarmUpAsync(natural);
    }

    private async Task<string> RenderWithAsync(TtsVoiceOption? choice, string text)
    {
        var voice = ResolveVoice(choice, notify: true)
                    ?? throw new InvalidOperationException("aucune voix disponible.");
        if (voice.Natural is { } natural)
        {
            try
            {
                return await Speech.SynthesizeNeuralAsync(text, natural, Settings.Language, Settings.Rate);
            }
            catch (Exception ex) when (Speech.DefaultVoice != null && ex is not ArgumentException)
            {
                // The natural engine failed (damaged files, missing native library…): say it with Windows anyway.
                _main.ShowToast("Voix naturelle indisponible (" + ex.Message + ") : voix Windows utilisée.");
                return await Speech.SynthesizeAsync(text, Speech.DefaultVoice.Id, Settings.Rate, Settings.Pitch);
            }
        }
        return await Speech.SynthesizeAsync(text, voice.Windows!.Id, Settings.Rate, Settings.Pitch);
    }

    /// <summary>The voice that will actually speak: a natural voice that is not downloaded yet falls back to Windows.</summary>
    private TtsVoiceOption? ResolveVoice(TtsVoiceOption? choice, bool notify)
    {
        choice ??= AutomaticVoice();
        if (choice is not { IsNatural: true, IsInstalled: false }) return choice;
        var fallback = Voices.FirstOrDefault(v => v.Windows != null && v.Id == Speech.DefaultVoice?.Id);
        if (notify && fallback != null && !_fallbackHintShown)
        {
            _fallbackHintShown = true;
            _main.ShowToast($"Voix naturelle pas encore téléchargée ({PackSizeText}) : voix Windows utilisée en attendant.");
        }
        return fallback;
    }

    /// <summary>No explicit choice: the default natural voice once downloaded, otherwise the French Windows voice.</summary>
    private TtsVoiceOption? AutomaticVoice()
    {
        if (IsNaturalInstalled) return Voices.FirstOrDefault(v => v.Id == NeuralVoiceCatalog.DefaultVoice.Id);
        var windows = Speech.DefaultVoice;
        return windows != null ? Voices.FirstOrDefault(v => v.Id == windows.Id) : null;
    }

    // ---- Preview ---------------------------------------------------------------------------------------------

    /// <summary>Plays a sample phrase with the selected voice in the headphones only (never into the microphone).</summary>
    [RelayCommand]
    private async Task PreviewVoice()
    {
        if (_main.Settings.MonitorDeviceId == null)
        {
            _main.ShowToast("Choisissez un « Retour casque » dans Configuration pour écouter un aperçu de la voix (sinon, utilisez « Dire »).");
            return;
        }
        if (!IsAvailable) return;
        var voice = SelectedVoice;
        var language = voice?.Windows is { } windows ? windows.Language : Settings.Language;
        IsBusy = true;
        try
        {
            var path = await RenderWithAsync(voice, NeuralVoiceCatalog.SamplePhrase(language));
            _main.Engine.Preview.Load(path, (float)Settings.Volume);
        }
        catch (Exception ex)
        {
            _main.ShowToast("Aperçu impossible : " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Natural voice download ------------------------------------------------------------------------------

    private bool CanDownloadNatural() => !IsNaturalInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadNatural))]
    private async Task DownloadNatural()
    {
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
            await Speech.Models.InstallAsync(Pack, progress, _downloadCts.Token);
            RefreshInstalled();
            // Downloading is an explicit choice of natural voices: switch to one unless one is already chosen.
            if (SelectedVoice is not { IsNatural: true })
                SelectedVoice = Voices.First(v => v.Id == NeuralVoiceCatalog.DefaultVoice.Id);
            _main.Save();
            _main.ShowToast("Voix naturelles installées : choisissez-en une dans la liste et écoutez-la avec ▶.");
            WarmUp();
        }
        catch (OperationCanceledException)
        {
            _main.ShowToast("Téléchargement des voix naturelles annulé : il reprendra là où il s'est arrêté.");
        }
        catch (DownloadException ex)
        {
            _main.ShowToast(ex.Message);
        }
        catch (Exception ex)
        {
            _main.ShowToast("Téléchargement des voix naturelles impossible : " + ex.Message);
        }
        finally
        {
            IsDownloading = false;
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    private bool CanDeleteNatural() => IsNaturalInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDeleteNatural))]
    private async Task DeleteNatural()
    {
        if (MessageBox.Show($"Supprimer les voix naturelles ({PackSizeText}) ? Vous pourrez les télécharger à nouveau.", "Synthèse vocale",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _main.Engine.Pads.Stop(SpeechPadId);
            await Speech.UnloadNeuralAsync();
            await Task.Run(() => Speech.Models.Delete(Pack));
        }
        catch (Exception ex)
        {
            _main.ShowToast("Suppression impossible des voix naturelles : " + ex.Message);
        }
        RefreshInstalled();
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
            // No browser: nothing to do.
        }
    }

    private void RefreshInstalled()
    {
        var installed = Speech.Models.IsInstalled(Pack);
        foreach (var v in Voices.Where(v => v.IsNatural)) v.IsInstalled = installed;
        IsNaturalInstalled = installed;
        OnPropertyChanged(nameof(SelectedNeedsDownload));
        // Natural voices deleted: back to the Windows voice rather than a voice that cannot speak.
        if (!installed && SelectedVoice is { IsNatural: true } && AutomaticVoice() is { } windows)
        {
            SelectedVoice = windows;
            _main.Save();
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
