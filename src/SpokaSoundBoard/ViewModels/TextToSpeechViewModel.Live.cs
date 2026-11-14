using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.ViewModels;

/// <summary>One phrase of « Parler avec cette voix », as shown in the list under the switch.</summary>
public sealed partial class LivePhrase(string text) : ObservableObject
{
    public string Text { get; } = text;

    /// <summary>« Préparation… », « Dite », « Ignorée (écho) »…</summary>
    [ObservableProperty] private string _state = "En attente";

    [ObservableProperty] private bool _isDone;
}

/// <summary>
/// « Parler avec cette voix »: you speak into the microphone, your phrase is written down offline (Canary) and the
/// chosen synthetic voice says it in your place. Your real voice is not sent meanwhile.
/// </summary>
public sealed partial class TextToSpeechViewModel
{
    private const int MaxLivePhrases = 6;
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(15);

    private readonly Queue<LivePhrase> _liveQueue = new();
    private readonly List<(string Text, DateTime At)> _recentlySpoken = [];
    private CancellationTokenSource? _recognitionDownloadCts;
    private bool _livePumping;

    public LiveSpeechRecognizer Recognizer { get; private set; } = null!;

    public VoiceModelPack RecognitionPack => SpeechRecognitionCatalog.Canary180mFlash;

    public string RecognitionSizeText => VoiceModelStore.FormatSize(RecognitionPack.TotalSize);

    public IReadOnlyList<SpeechLanguage> LiveLanguages => SpeechRecognitionCatalog.Languages;

    public ObservableCollection<LivePhrase> LivePhrases { get; } = [];

    public string RecognitionLicenseText =>
        "Reconnaissance vocale : NVIDIA Canary 180M Flash (licence CC BY 4.0) et Silero VAD (licence MIT). " +
        "Tout se passe sur votre PC : rien n'est envoyé sur Internet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatus))]
    [NotifyCanExecuteChangedFor(nameof(DownloadRecognitionCommand))]
    private bool _isRecognitionInstalled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadRecognitionCommand))]
    private bool _isRecognitionDownloading;

    [ObservableProperty] private double _recognitionPercent;
    [ObservableProperty] private string _recognitionStatus = "";

    /// <summary>The mode is on: the microphone is listened to and your voice is replaced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatus), nameof(LiveSwitch))]
    private bool _isLive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatus), nameof(LiveSwitch))]
    private bool _isLiveStarting;

    /// <summary>You are speaking right now (the detector hears a phrase).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatus))]
    private bool _isHearing;

    /// <summary>The switch of the card: on while starting or running; flipping it starts or stops the mode.</summary>
    public bool LiveSwitch
    {
        get => IsLive || IsLiveStarting;
        set
        {
            if (value != LiveSwitch) _ = ToggleLive();
        }
    }

    [ObservableProperty] private SpeechLanguage? _selectedLiveLanguage;

    [ObservableProperty] private string _liveDelayText = "";

    public string LiveStatus =>
        IsLiveStarting ? "Démarrage de la reconnaissance vocale…"
        : !IsLive ? IsRecognitionInstalled ? "Désactivé : votre vraie voix passe normalement." : "La reconnaissance vocale n'est pas encore téléchargée."
        : IsHearing ? "Je vous écoute…"
        : _main.Mix.PushToTalkEnabled ? "Maintenez la touche radio et parlez : la voix choisie le redit."
        : "Parlez : la voix choisie redit vos phrases. Votre vraie voix ne passe plus.";

    private void InitializeLive()
    {
        Recognizer = new LiveSpeechRecognizer(Speech.Models);
        Recognizer.Recognized += r => _dispatcher.BeginInvoke(() => OnRecognized(r));
        Recognizer.ListeningChanged += hearing => _dispatcher.BeginInvoke(() => IsHearing = hearing && IsLive);
        Recognizer.Failed += ex => _dispatcher.BeginInvoke(() => OnRecognitionFailed(ex));
        IsRecognitionInstalled = Speech.Models.IsInstalled(RecognitionPack);
        SelectedLiveLanguage = LiveLanguages.FirstOrDefault(l => l.Code == Settings.LiveLanguage) ?? LiveLanguages[0];
        Speech.Models.Changed += pack =>
        {
            if (pack.Id == RecognitionPack.Id) _dispatcher.BeginInvoke(() => IsRecognitionInstalled = Speech.Models.IsInstalled(RecognitionPack));
        };
    }

    partial void OnSelectedLiveLanguageChanged(SpeechLanguage? value)
    {
        if (value == null || value.Code == Settings.LiveLanguage) return;
        Settings.LiveLanguage = value.Code;
        _main.Save();
        // The model is loaded for one language: restart it.
        if (IsLive) _ = RestartLiveAsync();
    }

    private async Task RestartLiveAsync()
    {
        await StopLiveAsync(notify: false);
        await StartLiveAsync();
    }

    [RelayCommand]
    private async Task ToggleLive()
    {
        if (IsLiveStarting) return;
        if (IsLive) await StopLiveAsync(notify: true);
        else await StartLiveAsync();
    }

    private async Task StartLiveAsync()
    {
        if (!IsAvailable)
        {
            _main.ShowToast($"Choisissez d'abord une voix : téléchargez les voix naturelles ({PackSizeText}) ou ajoutez une voix Windows.");
            return;
        }
        if (!IsRecognitionInstalled)
        {
            if (!await InstallRecognitionAsync()) return;
        }
        IsLiveStarting = true;
        try
        {
            var language = SelectedLiveLanguage?.Code ?? "fr";
            await Task.Run(() => Recognizer.Start(language));
            WarmUp();
            var core = _main.Engine.Core;
            core.SpeechTap = Recognizer.Input;
            core.VoiceReplaced = true;
            IsLive = true;
            _main.ShowToast($"Parlez : « {SelectedVoice?.Title ?? "la voix choisie"} » dit vos phrases à votre place. Votre vraie voix ne passe plus.", 5);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _main.ShowToast("Impossible de démarrer la reconnaissance vocale : " + Core.FriendlyErrors.Describe(ex), 8);
        }
        finally
        {
            IsLiveStarting = false;
        }
    }

    private async Task StopLiveAsync(bool notify)
    {
        var core = _main.Engine.Core;
        core.SpeechTap = null;
        core.VoiceReplaced = false;
        IsLive = false;
        IsHearing = false;
        _liveQueue.Clear();
        await Task.Run(Recognizer.Stop);
        if (notify) _main.ShowToast("Votre vraie voix passe de nouveau dans le micro.");
    }

    /// <summary>Leaving the app: give the microphone back and free the model.</summary>
    public void ShutdownLive()
    {
        var core = _main.Engine.Core;
        core.SpeechTap = null;
        core.VoiceReplaced = false;
        Recognizer.Dispose();
    }

    private void OnRecognitionFailed(Exception ex)
    {
        App.Log(ex);
        _ = StopLiveAsync(notify: false);
        _main.ShowToast("La reconnaissance vocale s'est arrêtée : " + Core.FriendlyErrors.Describe(ex) + " Votre vraie voix passe de nouveau.", 8);
    }

    private void OnRecognized(RecognizedSpeech speech)
    {
        if (!IsLive) return;
        var phrase = new LivePhrase(speech.Text);
        LivePhrases.Insert(0, phrase);
        while (LivePhrases.Count > MaxLivePhrases) LivePhrases.RemoveAt(LivePhrases.Count - 1);

        _recentlySpoken.RemoveAll(s => DateTime.UtcNow - s.At > EchoWindow);
        if (LiveSpeechText.IsEcho(speech.Text, _recentlySpoken.Select(s => s.Text)))
        {
            // Speakers instead of headphones: the microphone heard the synthetic voice.
            phrase.State = "Ignorée : c'était l'écho de la voix";
            phrase.IsDone = true;
            return;
        }
        _liveQueue.Enqueue(phrase);
        if (!_livePumping) _ = PumpLiveAsync();
    }

    /// <summary>Says the queued phrases one after the other; the next one is prepared while the previous one plays.</summary>
    private async Task PumpLiveAsync()
    {
        _livePumping = true;
        try
        {
            while (_liveQueue.Count > 0)
            {
                var phrase = _liveQueue.Dequeue();
                phrase.State = "Préparation de la voix…";
                var started = Stopwatch.StartNew();
                string? path;
                try
                {
                    path = await RenderLiveAsync(phrase.Text);
                }
                catch (Exception ex)
                {
                    phrase.State = "Échec : " + Core.FriendlyErrors.Describe(ex);
                    phrase.IsDone = true;
                    continue;
                }
                await WaitForSpeechIdleAsync();
                _recentlySpoken.Add((phrase.Text, DateTime.UtcNow));
                _main.Engine.Pads.Stop(SpeechPadId);
                IsSpeaking = true;
                _speakStarted = DateTime.UtcNow;
                _playingTimer.Start();
                await _main.Engine.Pads.PlayAsync(SpeechPadId, path, (float)Settings.Volume, false, cache: false);
                phrase.State = "Dite";
                phrase.IsDone = true;
                LiveDelayText = $"Voix prête en {started.Elapsed.TotalSeconds:0.0} s après la fin de la phrase";
            }
        }
        finally
        {
            _livePumping = false;
        }
    }

    private async Task<string> RenderLiveAsync(string text)
    {
        var voice = ResolveVoice(SelectedVoice, notify: true) ?? throw new InvalidOperationException("aucune voix disponible.");
        if (voice.Natural is { } natural)
            return await Speech.SynthesizeNeuralAsync(text, natural, Settings.LiveLanguage, Settings.Rate);
        return await Speech.SynthesizeAsync(text, voice.Windows!.Id, Settings.Rate, Settings.Pitch);
    }

    private async Task WaitForSpeechIdleAsync()
    {
        // The file opens on a worker thread: give a phrase that just started a moment before trusting "not playing".
        while (DateTime.UtcNow - _speakStarted < TimeSpan.FromMilliseconds(300) || _main.Engine.Pads.IsPlaying(SpeechPadId))
            await Task.Delay(40);
    }

    // ---- Recognition model download ----------------------------------------------------------------------------

    private bool CanDownloadRecognition() => !IsRecognitionInstalled && !IsRecognitionDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadRecognition))]
    private Task DownloadRecognition() => InstallRecognitionAsync();

    [RelayCommand]
    private void CancelRecognitionDownload() => _recognitionDownloadCts?.Cancel();

    private async Task<bool> InstallRecognitionAsync()
    {
        if (IsRecognitionDownloading) return false;
        _recognitionDownloadCts = new CancellationTokenSource();
        IsRecognitionDownloading = true;
        RecognitionPercent = 0;
        RecognitionStatus = $"Téléchargement de la reconnaissance vocale… 0 / {RecognitionSizeText}";
        var progress = new Progress<VoiceDownloadProgress>(p =>
        {
            RecognitionPercent = p.Percent;
            RecognitionStatus = $"Téléchargement de la reconnaissance vocale… {VoiceModelStore.FormatSize(p.Done)} / {RecognitionSizeText}";
        });
        try
        {
            await Speech.Models.InstallAsync(RecognitionPack, progress, _recognitionDownloadCts.Token);
            IsRecognitionInstalled = Speech.Models.IsInstalled(RecognitionPack);
            return IsRecognitionInstalled;
        }
        catch (OperationCanceledException)
        {
            _main.ShowToast("Téléchargement annulé : il reprendra là où il s'est arrêté.");
        }
        catch (DownloadException ex)
        {
            _main.ShowToast(Core.FriendlyErrors.Describe(ex));
        }
        catch (Exception ex)
        {
            _main.ShowToast("Téléchargement de la reconnaissance vocale impossible : " + Core.FriendlyErrors.Describe(ex));
        }
        finally
        {
            IsRecognitionDownloading = false;
            _recognitionDownloadCts.Dispose();
            _recognitionDownloadCts = null;
        }
        return false;
    }

    [RelayCommand]
    private void OpenRecognitionLicense()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RecognitionPack.LicenseUrl) { UseShellExecute = true });
        }
        catch
        {
            // No browser: nothing to do.
        }
    }
}
