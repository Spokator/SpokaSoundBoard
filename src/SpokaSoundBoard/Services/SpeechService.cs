using System.IO;
using System.Security.Cryptography;
using System.Text;
using Windows.Media.SpeechSynthesis;

namespace SpokaSoundBoard.Services;

/// <summary>An installed Windows (OneCore) voice.</summary>
public sealed record SpeechVoice(string Id, string Name, string Language)
{
    public bool IsFrench => Language.StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    public string Display => $"{Name} ({Language})";
}

/// <summary>
/// Offline text-to-speech with the Windows voices (Windows.Media.SpeechSynthesis). Phrases are rendered to WAV files
/// in a small cache, so they can be played by the soundboard player (and therefore go into the microphone).
/// </summary>
public sealed class SpeechService : IDisposable
{
    public const int MaxTextLength = 500;
    private const int MaxCachedFiles = 40;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDirectory;
    private SpeechSynthesizer? _synthesizer;

    public SpeechService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Voices = LoadVoices();
    }

    /// <summary>Installed voices, French ones first (empty when speech synthesis is unavailable).</summary>
    public IReadOnlyList<SpeechVoice> Voices { get; }

    public bool IsAvailable => Voices.Count > 0;

    /// <summary>The voice to use when none was chosen: French (France first) if installed, else the system default.</summary>
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

    /// <summary>Renders <paramref name="text"/> to a WAV file (reused when the same phrase was already rendered).</summary>
    /// <param name="rate">Speaking rate, 0.5 to 6 (1 = normal).</param>
    /// <param name="pitch">Pitch, 0 to 2 (1 = normal).</param>
    public async Task<string> SynthesizeAsync(string text, string? voiceId, double rate, double pitch)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("Texte vide.", nameof(text));
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        rate = Math.Clamp(rate, 0.5, 6);
        pitch = Math.Clamp(pitch, 0, 2);

        var key = Hash($"{voiceId}|{rate:0.00}|{pitch:0.00}|{text}");
        var path = Path.Combine(_cacheDirectory, key + ".wav");
        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return path;
        }

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
        _synthesizer?.Dispose();
        _gate.Dispose();
    }
}
