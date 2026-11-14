using System.Diagnostics;
using System.Text.Json;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;

namespace SpokaSoundBoard.Core.Speech;

/// <summary>A voice made from the user's own recordings (« Mes voix »).</summary>
public sealed record CustomVoice(string Id, string Name, DateTime CreatedAt, double SpeechSeconds)
{
    public const string KeyPrefix = "custom:";
    public string Key => KeyPrefix + Id;
    public string FileName => $"{Id}.bin";
}

/// <summary>
/// The user's voices: imprint files (same format as the built-in voices) in voices\custom, listed in voices.json.
/// Small (0.5 MB each) and part of the full backup, like the rest of the data folder.
/// </summary>
public sealed class CustomVoiceLibrary
{
    public const string FolderName = "custom";
    private const string IndexName = "voices.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();

    public CustomVoiceLibrary(string voicesDirectory) => Directory = Path.Combine(voicesDirectory, FolderName);

    public string Directory { get; }

    public string PathOf(CustomVoice voice) => Path.Combine(Directory, voice.FileName);

    /// <summary>The voices whose file exists, oldest first. A damaged index gives an empty list, never an error.</summary>
    public List<CustomVoice> Load()
    {
        lock (_lock)
        {
            try
            {
                var path = Path.Combine(Directory, IndexName);
                if (!File.Exists(path)) return [];
                var list = JsonSerializer.Deserialize<List<CustomVoice>>(File.ReadAllText(path), Json) ?? [];
                return list.Where(v => v != null && !string.IsNullOrWhiteSpace(v.Id) && File.Exists(PathOf(v)))
                    .Select(v => v with { Name = CleanName(v.Name) }).ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>Adds a voice whose imprint was written to <paramref name="imprintFile"/> (moved into the library).</summary>
    public CustomVoice Add(string name, string imprintFile, double speechSeconds)
    {
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var voice = new CustomVoice(Guid.NewGuid().ToString("N")[..12], CleanName(name), DateTime.Now, Math.Round(speechSeconds, 1));
            File.Move(imprintFile, PathOf(voice), overwrite: true);
            Save([.. Load(), voice]);
            return voice;
        }
    }

    public void Rename(CustomVoice voice, string name)
    {
        lock (_lock) Save(Load().Select(v => v.Id == voice.Id ? v with { Name = CleanName(name) } : v).ToList());
    }

    public void Delete(CustomVoice voice)
    {
        lock (_lock)
        {
            Save(Load().Where(v => v.Id != voice.Id).ToList());
            try
            {
                File.Delete(PathOf(voice));
            }
            catch (IOException)
            {
                // In use right now: the index no longer lists it, which is what matters.
            }
        }
    }

    private void Save(List<CustomVoice> voices)
    {
        var path = Path.Combine(Directory, IndexName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(voices, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static string CleanName(string? name)
    {
        var n = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (n.Length > 40) n = n[..40].TrimEnd();
        return n.Length == 0 ? "Ma voix" : n;
    }
}

/// <summary>Builds a voice from recordings: decodes them, keeps the speech, and has SpokaVoiceEngine compute the imprint.</summary>
public static class VoiceImprintMaker
{
    /// <summary>Decodes audio files to mono 48 kHz (off the UI thread: Media Foundation must not start on the STA thread).</summary>
    public static List<float[]> Decode(IEnumerable<string> files)
    {
        var clips = new List<float[]>();
        foreach (var file in files)
        {
            var (reader, provider) = AudioFileLoader.Open(file, MixerCore.SampleRate);
            using (reader)
            {
                var mono = new List<float>();
                var buffer = new float[MixerCore.SampleRate * 2];
                int n;
                while ((n = provider.Read(buffer, 0, buffer.Length)) > 0 && mono.Count < MixerCore.SampleRate * 600)
                    for (var i = 0; i + 1 < n; i += 2) mono.Add((buffer[i] + buffer[i + 1]) * 0.5f);
                clips.Add(mono.ToArray());
            }
        }
        return clips;
    }

    /// <summary>
    /// Runs « SpokaVoiceEngine --imprint » on the selected speech. Returns the imprint file (in the temp folder).
    /// Throws with a French message when it fails.
    /// </summary>
    public static async Task<string> BuildAsync(string enginePath, string seedVcDirectory, string creatorDirectory,
        SelectedSpeech speech, CancellationToken ct = default)
    {
        if (speech.Seconds < SpeechSelector.MinimumSeconds)
            throw new InvalidOperationException($"Il faut au moins {SpeechSelector.MinimumSeconds:0} secondes de voix (il y en a {speech.Seconds:0.0}).");
        var work = Path.Combine(Path.GetTempPath(), "SpokaSoundBoard-voice-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(work);
        try
        {
            var at22 = new List<float>(speech.Speech.Length / 2 + 1);
            new StreamResampler(speech.SampleRate, 22050).Process(speech.Speech, at22);
            var at16 = new List<float>(speech.Speech.Length / 3 + 1);
            new StreamResampler(speech.SampleRate, 16000).Process(speech.Speech, at16);
            var wav22 = Path.Combine(work, "speech22.wav");
            var wav16 = Path.Combine(work, "speech16.wav");
            WriteWav(wav22, at22, 22050);
            WriteWav(wav16, at16, 16000);
            var output = Path.Combine(Path.GetTempPath(), $"SpokaSoundBoard-voice-{Guid.NewGuid():N}.bin");

            var psi = new ProcessStartInfo(enginePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(enginePath)!,
            };
            foreach (var a in new[] { "--imprint", seedVcDirectory, creatorDirectory, wav22, wav16, output }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Le moteur du changeur de voix n'a pas démarré.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                // The engine's first line is meant for the user; the rest (stack trace) goes to error.log as the inner exception.
                var log = (await stderr.ConfigureAwait(false)).Trim();
                var detail = log.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
                throw new InvalidOperationException("La création de la voix a échoué" + (detail is { Length: > 0 } ? $" : {detail}" : "."),
                    log.Length > 0 ? new EngineFailure($"code {process.ExitCode}{Environment.NewLine}{log}") : null);
            }
            await stdout.ConfigureAwait(false);
            return output;
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(work, true);
            }
            catch
            {
                // Temp folder: best effort.
            }
        }
    }

    private static void WriteWav(string path, List<float> samples, int rate)
    {
        using var writer = new NAudio.Wave.WaveFileWriter(path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
    }
}

/// <summary>What the voice engine wrote before failing (kept for error.log).</summary>
public sealed class EngineFailure(string details) : Exception(details);
