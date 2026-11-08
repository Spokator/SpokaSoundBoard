using System.IO;
using System.Security.Cryptography;
using System.Text;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Speech;
using Windows.Media.SpeechSynthesis;

namespace SpokaSoundBoard.Services;

/// <summary>An installed Windows (OneCore) voice.</summary>
public sealed record SpeechVoice(string Id, string Name, string Language)
{
    public bool IsFrench => Language.StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    public string Display => $"{Name} ({Language})";
}

/// <summary>
/// Offline text-to-speech with two engines: natural (neural) voices run in-process by sherpa-onnx once their model
/// is downloaded, and the classic Windows voices (Windows.Media.SpeechSynthesis) as a fallback. Phrases are
/// rendered to WAV files in a small cache, so they can be played by the soundboard player (and therefore go into
/// the microphone). Rendering always happens off the UI thread.
/// </summary>
public sealed class SpeechService : IDisposable
{
    public const int MaxTextLength = 500;
    private const int MaxCachedFiles = 40;

    /// <summary>The natural voice model is freed after this long without speaking (it holds about 300 MB).</summary>
    private static readonly TimeSpan NeuralIdleUnload = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDirectory;
    private readonly Timer _idleTimer;
    private SpeechSynthesizer? _synthesizer;

    public SpeechService(string cacheDirectory, string voicesDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Voices = LoadVoices();
        Models = new VoiceModelStore(voicesDirectory);
        Neural = new NeuralSpeechEngine(Models);
        _idleTimer = new Timer(_ => Neural.Unload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Installed Windows voices, French ones first (empty when speech synthesis is unavailable).</summary>
    public IReadOnlyList<SpeechVoice> Voices { get; }

    /// <summary>Downloaded natural voice models.</summary>
    public VoiceModelStore Models { get; }

    public NeuralSpeechEngine Neural { get; }

    public bool HasWindowsVoices => Voices.Count > 0;

    /// <summary>The Windows voice to use when none was chosen: French (France first) if installed, else the system default.</summary>
    public SpeechVoice? DefaultVoice
    {
        get
        {
            if (Voices.Count == 0) return null;
            var french = Voices.FirstOrDefault(v => v.Language.Equals("fr-FR", StringComparison.OrdinalIgnoreCase))
                         ?? Voices.FirstOrDefault(v => v.IsFrench);
            if (french != null) return french;
            try
            {
                var id = SpeechSynthesizer.DefaultVoice?.Id;
                return Voices.FirstOrDefault(v => v.Id == id) ?? Voices[0];
            }
            catch
            {
                return Voices[0];
            }
        }
    }

    /// <summary>Renders <paramref name="text"/> with a Windows voice (reused when the same phrase was already rendered).</summary>
    /// <param name="rate">Speaking rate, 0.5 to 6 (1 = normal).</param>
    /// <param name="pitch">Pitch, 0 to 2 (1 = normal).</param>
    public async Task<string> SynthesizeAsync(string text, string? voiceId, double rate, double pitch)
    {
        text = Clean(text);
        rate = Math.Clamp(rate, 0.5, 6);
        pitch = Math.Clamp(pitch, 0, 2);

        var path = CachePath($"{voiceId}|{rate:0.00}|{pitch:0.00}|{text}");
        if (Reuse(path)) return path;

        await _gate.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(_cacheDirectory);
            var synth = _synthesizer ??= new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId);
            if (voice != null && synth.Voice?.Id != voice.Id) synth.Voice = voice;
            synth.Options.SpeakingRate = rate;
            synth.Options.AudioPitch = pitch;

            using var stream = await synth.SynthesizeTextToStreamAsync(text);
            var temp = path + ".tmp";
            await using (var output = File.Create(temp))
            await using (var input = stream.AsStreamForRead())
                await input.CopyToAsync(output);
            File.Move(temp, path, true);
            Prune();
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renders <paramref name="text"/> with a natural voice on a worker thread: model output resampled to the
    /// mixer's 48 kHz stereo float, peak-normalized. Pitch is not adjustable with these voices.
    /// </summary>
    /// <param name="rate">Speaking rate (1 = normal), clamped to the engine's range.</param>
    public async Task<string> SynthesizeNeuralAsync(string text, NeuralVoice voice, string language, double rate)
    {
        text = Clean(text);
        language = NeuralVoiceCatalog.NormalizeLanguage(language);
        rate = Math.Clamp(rate, NeuralSpeechEngine.MinSpeed, NeuralSpeechEngine.MaxSpeed);

        var path = CachePath($"{voice.Id}|{language}|{rate:0.00}|{Neural.Steps}|{text}");
        if (Reuse(path)) return path;

        await _gate.WaitAsync();
        try
        {
            if (File.Exists(path)) return path;
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            Directory.CreateDirectory(_cacheDirectory);
            await Task.Run(() =>
            {
                var speech = Neural.Synthesize(voice, text, language, rate);
                var stereo = SpeechAudio.ToMixerFormat(speech.Samples, speech.SampleRate);
                var temp = path + ".tmp";
                SpeechAudio.WriteWav(temp, stereo, MixerCore.SampleRate, 2);
                File.Move(temp, path, true);
            });
            Prune();
            return path;
        }
        finally
        {
            _idleTimer.Change(NeuralIdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>Loads the natural voice model in the background, so the first phrase is not delayed (best effort).</summary>
    public Task WarmUpAsync(NeuralVoice voice) => Task.Run(() =>
    {
        try
        {
            var pack = NeuralVoiceCatalog.PackOf(voice);
            if (!Models.IsInstalled(pack)) return;
            Neural.Load(pack);
            _idleTimer.Change(NeuralIdleUnload, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            // The real call reports the error.
        }
    });

    /// <summary>Frees the natural voice model now (before deleting its files).</summary>
    public async Task UnloadNeuralAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await Task.Run(Neural.Unload);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Duration of a rendered phrase (zero when unknown).</summary>
    public static TimeSpan DurationOf(string path)
    {
        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(path);
            return reader.TotalTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static string Clean(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Texte vide.", nameof(text));
        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }

    private string CachePath(string key) => Path.Combine(_cacheDirectory, Hash(key) + ".wav");

    private static bool Reuse(string path)
    {
        if (!File.Exists(path)) return false;
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* in use */ }
        return true;
    }

    private static IReadOnlyList<SpeechVoice> LoadVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => new SpeechVoice(v.Id, v.DisplayName, v.Language))
                .OrderByDescending(v => v.IsFrench)
                .ThenByDescending(v => v.Language.Equals("fr-FR", StringComparison.OrdinalIgnoreCase))
                .ThenBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            // Speech synthesis missing (N editions, stripped images…).
            return [];
        }
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_cacheDirectory).GetFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(MaxCachedFiles))
                try { old.Delete(); } catch { /* in use: next time */ }
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();

    public void Dispose()
    {
        _idleTimer.Dispose();
        _synthesizer?.Dispose();
        Neural.Dispose();
        _gate.Dispose();
    }
}
