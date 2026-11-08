using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SherpaOnnx;

namespace SpokaSoundBoard.Core.Speech;

/// <summary>Raw output of a neural voice: mono samples at the model's sample rate.</summary>
public sealed record SynthesizedSpeech(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds(Samples.Length / (double)SampleRate) : TimeSpan.Zero;
}

/// <summary>
/// Offline neural text-to-speech with sherpa-onnx (ONNX Runtime, CPU). One model is kept loaded (about 300 MB of
/// RAM for Supertonic 3, loaded in about a second); callers unload it when idle. Not thread-safe by design: calls
/// are serialized by a lock, and every call blocks, so run them on a worker thread (never the UI or audio thread).
/// </summary>
public sealed class NeuralSpeechEngine : IDisposable
{
    /// <summary>Flow-matching steps of Supertonic: 8 is the reference quality (5 is faster, slightly rougher).</summary>
    public const int DefaultSteps = 8;

    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2.0;

    private readonly VoiceModelStore _store;
    private readonly object _lock = new();
    private OfflineTts? _tts;
    private VoiceModelPack? _loaded;
    private bool _disposed;

    public NeuralSpeechEngine(VoiceModelStore store) => _store = store;

    public int Steps { get; init; } = DefaultSteps;

    /// <summary>ONNX Runtime threads (half the cores, 1 to 4: leaves room for the audio engine and the game).</summary>
    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public bool IsLoaded
    {
        get { lock (_lock) return _tts != null; }
    }

    /// <summary>Loads the pack's model if needed (blocking). Throws when the pack is not installed or cannot load.</summary>
    public void Load(VoiceModelPack pack)
    {
        lock (_lock) EnsureLoaded(pack);
    }

    /// <summary>Renders <paramref name="text"/> (blocking). Throws <see cref="InvalidOperationException"/> with a French message on failure.</summary>
    /// <param name="speed">1 = normal, clamped to <see cref="MinSpeed"/>…<see cref="MaxSpeed"/>.</param>
    public SynthesizedSpeech Synthesize(NeuralVoice voice, string text, string language, double speed, CancellationToken ct = default)
    {
        text = PrepareText(text);
        if (text.Length == 0) throw new InvalidOperationException("Rien à dire : le texte ne contient que des symboles.");
        var pack = NeuralVoiceCatalog.PackOf(voice);
        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            var tts = EnsureLoaded(pack);
            var config = new OfflineTtsGenerationConfig
            {
                Sid = Math.Clamp(voice.SpeakerId, 0, Math.Max(0, tts.NumSpeakers - 1)),
                Speed = (float)Math.Clamp(double.IsFinite(speed) ? speed : 1, MinSpeed, MaxSpeed),
                NumSteps = Steps,
            };
            config.Extra["lang"] = NeuralVoiceCatalog.NormalizeLanguage(language);

            // Returning 0 from the callback stops the generation (between sentences).
            OfflineTtsCallbackProgressWithArg callback = (_, _, _, _) => ct.IsCancellationRequested ? 0 : 1;
            var audio = tts.GenerateWithConfig(text, config, callback);
            try
            {
                GC.KeepAlive(callback);
                ct.ThrowIfCancellationRequested();
                var samples = audio.Samples;
                if (samples.Length == 0 || audio.SampleRate <= 0)
                    throw new InvalidOperationException("La voix naturelle n'a rien produit pour ce texte.");
                return new SynthesizedSpeech(samples, audio.SampleRate);
            }
            finally
            {
                audio.Dispose();
            }
        }
    }

    /// <summary>Frees the loaded model (the next call reloads it).</summary>
    public void Unload()
    {
        lock (_lock)
        {
            _tts?.Dispose();
            _tts = null;
            _loaded = null;
        }
    }

    private OfflineTts EnsureLoaded(VoiceModelPack pack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tts != null && _loaded == pack) return _tts;
        _tts?.Dispose();
        _tts = null;
        _loaded = null;

        if (!_store.IsInstalled(pack))
            throw new InvalidOperationException($"La voix naturelle {pack.Name} n'est pas téléchargée.");
        NativeLibraries.EnsureResolver();

        var config = new OfflineTtsConfig();
        config.Model.NumThreads = Threads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        switch (pack.Kind)
        {
            case NeuralModelKind.Supertonic:
                config.Model.Supertonic.DurationPredictor = _store.FilePath(pack, "duration_predictor.int8.onnx");
                config.Model.Supertonic.TextEncoder = _store.FilePath(pack, "text_encoder.int8.onnx");
                config.Model.Supertonic.VectorEstimator = _store.FilePath(pack, "vector_estimator.int8.onnx");
                config.Model.Supertonic.Vocoder = _store.FilePath(pack, "vocoder.int8.onnx");
                config.Model.Supertonic.TtsJson = _store.FilePath(pack, "tts.json");
                config.Model.Supertonic.UnicodeIndexer = _store.FilePath(pack, "unicode_indexer.bin");
                config.Model.Supertonic.VoiceStyle = _store.FilePath(pack, "voice.bin");
                break;
            default:
                throw new InvalidOperationException($"Modèle de voix non pris en charge : {pack.Kind}.");
        }

        OfflineTts tts;
        try
        {
            tts = new OfflineTts(config);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Le moteur des voix naturelles est absent ou endommagé (" + ex.Message + "). Réinstallez SpokaSoundBoard.", ex);
        }
        // The C API returns a null handle (no exception) when a model file cannot be loaded, and its getters do
        // not check for null: look at the handle before calling anything else.
        var created = HasNativeHandle(tts);
        if (!created || tts.SampleRate <= 0)
        {
            if (created) tts.Dispose();
            else GC.SuppressFinalize(tts);
            throw new InvalidOperationException($"Impossible de charger la voix naturelle {pack.Name} : fichiers endommagés ? Supprimez-la puis téléchargez-la à nouveau.");
        }
        _tts = tts;
        _loaded = pack;
        return tts;
    }

    private static readonly FieldInfo? HandleField =
        typeof(OfflineTts).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>False when sherpa-onnx failed to create the model (null native handle).</summary>
    private static bool HasNativeHandle(OfflineTts tts)
    {
        // If a future version renames the field, assume it is valid (the old behavior).
        if (HandleField?.GetValue(tts) is not HandleRef handle) return true;
        return handle.Handle != IntPtr.Zero;
    }

    /// <summary>
    /// Cleans text for a character-level model: typographic quotes and dashes become plain ones, emoji and
    /// control characters are dropped, whitespace is collapsed. Letters of every script are kept.
    /// </summary>
    public static string PrepareText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var c in text)
        {
            if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c)) continue; // emoji and other astral symbols
            switch (c)
            {
                case '«': // « : French spacing inside the quotes is dropped
                case '“' or '„':
                    sb.Append('"');
                    lastSpace = true;
                    continue;
                case '–' or '—' or '−': // dashes: a pause
                    while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    sb.Append(", ");
                    lastSpace = true;
                    continue;
                case '»' or '”':
                    while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    sb.Append('"');
                    lastSpace = false;
                    continue;
            }
            string? replacement = c switch
            {
                '‘' or '’' or '‚' or 'ʼ' => "'",
                '…' => "...",
                _ => null,
            };
            if (replacement == null)
            {
                var category = char.GetUnicodeCategory(c);
                var symbol = category is UnicodeCategory.OtherSymbol or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                             || c is >= '\uFE00' and <= '\uFE0F'; // emoji variation selectors
                if (char.IsWhiteSpace(c) || char.IsControl(c)) replacement = " "; // includes non-breaking spaces
                else if (symbol) continue;
                else replacement = c.ToString();
            }
            foreach (var r in replacement)
            {
                if (r == ' ')
                {
                    if (lastSpace) continue;
                    lastSpace = true;
                }
                else lastSpace = false;
                sb.Append(r);
            }
        }
        var result = sb.ToString().Trim();
        // Quotes left alone (« » around nothing) or pure punctuation: nothing to say.
        return result.Any(char.IsLetterOrDigit) ? result : "";
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _tts?.Dispose();
            _tts = null;
            _loaded = null;
        }
    }

    /// <summary>
    /// Loads the app's own onnxruntime.dll before sherpa-onnx-c-api.dll. Recent Windows 11 builds ship an older
    /// onnxruntime.dll in System32; if the loader picked that one, sherpa-onnx would crash or fail to load. The
    /// app's native folders are the probing directories of the host (app folder, runtimes\win-x64\native, or the
    /// single-file extraction folder).
    /// </summary>
    internal static class NativeLibraries
    {
        private const string SherpaLibrary = "sherpa-onnx-c-api";
        private static int _registered;

        public static void EnsureResolver()
        {
            if (Interlocked.Exchange(ref _registered, 1) == 1) return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(OfflineTts).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // A resolver is already set for that assembly: keep it.
            }
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!name.StartsWith(SherpaLibrary, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            foreach (var dir in SearchDirectories())
            {
                var sherpa = Path.Combine(dir, SherpaLibrary + ".dll");
                var onnx = Path.Combine(dir, "onnxruntime.dll");
                if (!File.Exists(sherpa) || !File.Exists(onnx)) continue;
                if (NativeLibrary.TryLoad(onnx, out _) && NativeLibrary.TryLoad(sherpa, out var handle)) return handle;
            }
            return IntPtr.Zero; // Default probing.
        }

        internal static IEnumerable<string> SearchDirectories()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var probing = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "";
            var candidates = probing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append(AppContext.BaseDirectory)
                .Append(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"));
            foreach (var dir in candidates)
            {
                string full;
                try { full = Path.GetFullPath(dir); }
                catch { continue; }
                if (seen.Add(full)) yield return full;
            }
        }
    }
}
