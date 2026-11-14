using System.Diagnostics;
using SherpaOnnx;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;

namespace SpokaSoundBoard.Core.Speech;

/// <summary>A phrase heard in the microphone and written down.</summary>
/// <param name="Duration">Length of the spoken phrase.</param>
/// <param name="DecodeTime">Time the model took to write it down.</param>
public sealed record RecognizedSpeech(string Text, TimeSpan Duration, TimeSpan DecodeTime);

/// <summary>
/// Listens to the microphone (48 kHz mono, written into <see cref="Input"/> by the audio thread), cuts it into
/// phrases with the Silero voice activity detector and writes each phrase down with Canary, offline. Everything runs
/// on its own background thread: the audio thread only copies samples into the ring buffer.
/// </summary>
public sealed class LiveSpeechRecognizer : IDisposable
{
    public const int InputRate = 48000;
    public const int ModelRate = 16000;
    private const int VadWindow = 512; // 32 ms at 16 kHz, what Silero expects

    private readonly VoiceModelStore _store;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    private OfflineRecognizer? _recognizer;
    private VoiceActivityDetector? _vad;

    public LiveSpeechRecognizer(VoiceModelStore store) => _store = store;

    public static VoiceModelPack Pack => SpeechRecognitionCatalog.Canary180mFlash;

    /// <summary>Microphone samples at <see cref="InputRate"/>, filled by the mixer (4 s of margin).</summary>
    public FloatRingBuffer Input { get; } = new(InputRate * 4);

    /// <summary>Silence that ends a phrase. Shorter answers sooner but may cut a sentence in two.</summary>
    public float EndOfPhraseSeconds { get; init; } = 0.45f;

    /// <summary>A phrase longer than this is cut, so the voice never lags far behind.</summary>
    public float MaxPhraseSeconds { get; init; } = 12f;

    /// <summary>ONNX Runtime threads for the recognizer (leaves room for the voice, the audio and the game).</summary>
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);

    public bool IsRunning => _thread != null;

    /// <summary>The language being recognized (« fr », « en », « es », « de »).</summary>
    public string Language { get; private set; } = "fr";

    /// <summary>A phrase was written down (background thread).</summary>
    public event Action<RecognizedSpeech>? Recognized;

    /// <summary>The detector hears speech (true) or the phrase ended (false), on the background thread.</summary>
    public event Action<bool>? ListeningChanged;

    /// <summary>Recognition stopped on an error (background thread).</summary>
    public event Action<Exception>? Failed;

    /// <summary>Loads the models (about 2 s, blocking: call it off the UI thread) and starts listening.</summary>
    public void Start(string language)
    {
        lock (_gate)
        {
            if (_thread != null) return;
            if (!_store.IsInstalled(Pack)) throw new InvalidOperationException("La reconnaissance vocale n'est pas téléchargée.");
            Language = SpeechRecognitionCatalog.NormalizeLanguage(language);
            _recognizer = CreateRecognizer(Language);
            _vad = CreateVad();
            Input.Clear();
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "SpokaSoundBoard speech recognition", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            if (thread == null) return;
            _stop = true;
            _thread = null;
        }
        thread.Join(5000);
        lock (_gate)
        {
            _vad?.Dispose();
            _vad = null;
            _recognizer?.Dispose();
            _recognizer = null;
        }
        Input.Clear();
    }

    private OfflineRecognizer CreateRecognizer(string language)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Canary.Encoder = _store.FilePath(Pack, "encoder.int8.onnx");
        config.ModelConfig.Canary.Decoder = _store.FilePath(Pack, "decoder.int8.onnx");
        config.ModelConfig.Canary.SrcLang = language;
        config.ModelConfig.Canary.TgtLang = language; // same language: transcription, not translation
        config.ModelConfig.Canary.UsePnc = 1;         // punctuation and capitals: the voice reads them better
        config.ModelConfig.Tokens = _store.FilePath(Pack, "tokens.txt");
        config.ModelConfig.NumThreads = Threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    private VoiceActivityDetector CreateVad()
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = _store.FilePath(Pack, "silero_vad.onnx");
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = EndOfPhraseSeconds;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = MaxPhraseSeconds;
        config.SileroVad.WindowSize = VadWindow;
        config.SampleRate = ModelRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        return new VoiceActivityDetector(config, MaxPhraseSeconds + 5);
    }

    private void Run()
    {
        var decimator = new Decimator();
        var block = new float[1536]; // 32 ms at 48 kHz
        var pending = new List<float>(4096);
        var window = new float[VadWindow];
        var speaking = false;
        try
        {
            while (!_stop)
            {
                var n = Input.Read(block);
                if (n == 0)
                {
                    Thread.Sleep(15);
                    continue;
                }
                decimator.Process(block.AsSpan(0, n), pending);
                var used = 0;
                while (pending.Count - used >= VadWindow)
                {
                    pending.CopyTo(used, window, 0, VadWindow);
                    used += VadWindow;
                    _vad!.AcceptWaveform(window);
                    var now = _vad.IsSpeechDetected();
                    if (now != speaking)
                    {
                        speaking = now;
                        ListeningChanged?.Invoke(now);
                    }
                    Drain();
                }
                pending.RemoveRange(0, used);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            if (speaking) ListeningChanged?.Invoke(false);
        }
    }

    private void Drain()
    {
        while (!_vad!.IsEmpty())
        {
            var samples = _vad.Front().Samples;
            _vad.Pop();
            var sw = Stopwatch.StartNew();
            using var stream = _recognizer!.CreateStream();
            stream.AcceptWaveform(ModelRate, samples);
            _recognizer.Decode(stream);
            var text = LiveSpeechText.Clean(stream.Result.Text);
            if (text != null)
                Recognized?.Invoke(new RecognizedSpeech(text, TimeSpan.FromSeconds(samples.Length / (double)ModelRate), sw.Elapsed));
        }
    }

    /// <summary>Writes down a whole recording (tests and diagnostics): same models, same cutting into phrases.</summary>
    public static List<string> Transcribe(VoiceModelStore store, string language, ReadOnlySpan<float> mono48k)
    {
        using var recognizer = new LiveSpeechRecognizer(store);
        var results = new List<string>();
        recognizer.Recognized += r => results.Add(r.Text);
        lock (recognizer._gate)
        {
            recognizer.Language = SpeechRecognitionCatalog.NormalizeLanguage(language);
            recognizer._recognizer = recognizer.CreateRecognizer(recognizer.Language);
            recognizer._vad = recognizer.CreateVad();
        }
        var decimator = new Decimator();
        var pcm = new List<float>(mono48k.Length / 3 + 1);
        decimator.Process(mono48k, pcm);
        pcm.AddRange(new float[ModelRate]); // one second of silence closes the last phrase
        var window = new float[VadWindow];
        for (var i = 0; i + VadWindow <= pcm.Count; i += VadWindow)
        {
            pcm.CopyTo(i, window, 0, VadWindow);
            recognizer._vad!.AcceptWaveform(window);
            recognizer.Drain();
        }
        recognizer._vad!.Flush();
        recognizer.Drain();
        return results;
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _vad?.Dispose();
            _vad = null;
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
