using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Core.Speech;
using Xunit.Abstractions;

namespace SpokaSoundBoard.Tests;

public class NeuralVoiceCatalogTests
{
    [Fact]
    public void CatalogIsConsistent()
    {
        Assert.Equal(NeuralVoiceCatalog.Voices.Count, NeuralVoiceCatalog.Voices.Select(v => v.Id).Distinct().Count());
        Assert.Equal(NeuralVoiceCatalog.Packs.Count, NeuralVoiceCatalog.Packs.Select(p => p.Id).Distinct().Count());
        foreach (var voice in NeuralVoiceCatalog.Voices)
        {
            var pack = NeuralVoiceCatalog.PackOf(voice);
            Assert.StartsWith(NeuralVoice.IdPrefix, voice.Id);
            Assert.False(string.IsNullOrWhiteSpace(voice.Name));
            Assert.Single(NeuralVoiceCatalog.Voices, v => v.PackId == pack.Id && v.SpeakerId == voice.SpeakerId);
        }
        Assert.Contains(NeuralVoiceCatalog.DefaultVoice, NeuralVoiceCatalog.Voices);
    }

    [Fact]
    public void PacksArePinnedHashedAndLicensed()
    {
        foreach (var pack in NeuralVoiceCatalog.Packs)
        {
            // Pinned to an immutable commit, over HTTPS: the hashes below can never drift.
            Assert.StartsWith("https://", pack.BaseUrl);
            Assert.Matches("/resolve/[0-9a-f]{40}$", pack.BaseUrl);
            Assert.False(string.IsNullOrWhiteSpace(pack.License));
            Assert.StartsWith("https://", pack.LicenseUrl);
            Assert.NotEmpty(pack.Files);
            Assert.Equal(pack.Files.Count, pack.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var file in pack.Files)
            {
                Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
                Assert.True(file.Size > 0);
                Assert.Equal(file.Name, Path.GetFileName(file.Name));
                // Model data only: never an executable.
                Assert.DoesNotContain(Path.GetExtension(file.Name).ToLowerInvariant(), new[] { ".exe", ".dll", ".ps1", ".bat", ".cmd", ".msi" });
                Assert.Equal($"{pack.BaseUrl}/{file.Name}", pack.UrlOf(file).ToString());
            }
            Assert.Equal(pack.Files.Sum(f => f.Size), pack.TotalSize);
        }
        Assert.Equal(145295768, NeuralVoiceCatalog.Supertonic3.TotalSize);
    }

    [Fact]
    public void SupertonicHasTenSpeakersInVoiceBinOrder()
    {
        var voices = NeuralVoiceCatalog.Voices.Where(v => v.PackId == NeuralVoiceCatalog.Supertonic3.Id).ToList();
        Assert.Equal(Enumerable.Range(0, 10), voices.Select(v => v.SpeakerId));
        Assert.Equal(["F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5"], voices.Select(v => v.Key));
        Assert.All(voices.Take(5), v => Assert.True(v.IsFemale));
        Assert.All(voices.Skip(5), v => Assert.False(v.IsFemale));
    }

    [Fact]
    public void VoiceIdsRoundTripAndAreDistinctFromWindowsIds()
    {
        var voice = NeuralVoiceCatalog.Voices[3];
        Assert.Equal("neural:supertonic3:F4", voice.Id);
        Assert.Same(voice, NeuralVoiceCatalog.FindVoice(voice.Id));
        const string windowsId = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens\MSTTS_V110_frFR_HortenseM";
        Assert.False(NeuralVoiceCatalog.IsNeuralVoiceId(windowsId));
        Assert.Null(NeuralVoiceCatalog.FindVoice(windowsId));
        Assert.Null(NeuralVoiceCatalog.FindVoice(null));
        Assert.Null(NeuralVoiceCatalog.FindVoice("neural:supertonic3:Z9"));
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("EN_us", "en")]
    [InlineData(" de ", "de")]
    [InlineData("xx", "fr")]
    [InlineData("", "fr")]
    [InlineData(null, "fr")]
    public void LanguagesAreNormalized(string? code, string expected) =>
        Assert.Equal(expected, NeuralVoiceCatalog.NormalizeLanguage(code));

    [Fact]
    public void ThirtyOneLanguagesFrenchFirst()
    {
        Assert.Equal(31, NeuralVoiceCatalog.Languages.Count);
        Assert.Equal(31, NeuralVoiceCatalog.Languages.Select(l => l.Code).Distinct().Count());
        Assert.Equal("fr", NeuralVoiceCatalog.Languages[0].Code);
        Assert.Equal("en", NeuralVoiceCatalog.Languages[1].Code);
        Assert.Contains("micro", NeuralVoiceCatalog.SamplePhrase("fr-FR"));
        Assert.Contains("microphone", NeuralVoiceCatalog.SamplePhrase("ja"));
    }
}

public class TextToSpeechSettingsTests
{
    [Fact]
    public void OldSettingsFilesGetTheDefaultLanguage()
    {
        var settings = SettingsStore.Deserialize("""{ "TextToSpeech": { "VoiceId": "MSTTS_V110_frFR_HortenseM", "Rate": 1.2 } }""");
        Assert.Equal("fr", settings.TextToSpeech.Language);
        Assert.Equal("MSTTS_V110_frFR_HortenseM", settings.TextToSpeech.VoiceId);
    }

    [Fact]
    public void SanitizeFixesLanguageAndRetiredNaturalVoices()
    {
        var settings = SettingsStore.Deserialize("""{ "TextToSpeech": { "VoiceId": "neural:oldpack:X1", "Language": "klingon" } }""");
        Assert.Equal("fr", settings.TextToSpeech.Language);
        Assert.Null(settings.TextToSpeech.VoiceId);

        var kept = SettingsStore.Deserialize("""{ "TextToSpeech": { "VoiceId": "neural:supertonic3:M2", "Language": "en-GB" } }""");
        Assert.Equal("neural:supertonic3:M2", kept.TextToSpeech.VoiceId);
        Assert.Equal("en", kept.TextToSpeech.Language);
    }

    [Fact]
    public void LanguageSurvivesARoundTrip()
    {
        var settings = new AppSettings();
        settings.TextToSpeech.Language = "es";
        settings.TextToSpeech.VoiceId = NeuralVoiceCatalog.Voices[7].Id;
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(settings));
        Assert.Equal("es", back.TextToSpeech.Language);
        Assert.Equal(NeuralVoiceCatalog.Voices[7].Id, back.TextToSpeech.VoiceId);
    }
}

public class SpeechAudioTests
{
    private static float[] Sine(double frequency, int sampleRate, double seconds, float amplitude = 0.5f) =>
        Enumerable.Range(0, (int)(sampleRate * seconds)).Select(i => amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate)).ToArray();

    private static int ZeroCrossings(ReadOnlySpan<float> samples)
    {
        var count = 0;
        for (var i = 1; i < samples.Length; i++)
            if (samples[i - 1] < 0 != samples[i] < 0) count++;
        return count;
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(22050)]
    [InlineData(24000)]
    [InlineData(16000)]
    public void ResampleKeepsDurationAndPitch(int sourceRate)
    {
        var input = Sine(440, sourceRate, 1.0);
        var output = SpeechAudio.Resample(input, sourceRate, 48000);
        Assert.InRange(output.Length, 48000 - 200, 48000);
        // 440 Hz: about 880 zero crossings per second at any sample rate.
        Assert.InRange(ZeroCrossings(output), 870, 890);
        Assert.InRange(output.Max(), 0.45f, 0.55f);
    }

    [Fact]
    public void ResampleEdgeCases()
    {
        Assert.Empty(SpeechAudio.Resample([], 44100, 48000));
        var same = new[] { 0.1f, -0.2f, 0.3f };
        var copy = SpeechAudio.Resample(same, 48000, 48000);
        Assert.Equal(same, copy);
        Assert.NotSame(same, copy);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechAudio.Resample(same, 0, 48000));
    }

    [Fact]
    public void NormalizeRaisesToTargetPeakWithCappedGain()
    {
        var loud = new[] { 0.5f, -0.25f, 0.1f };
        SpeechAudio.Normalize(loud);
        Assert.Equal(SpeechAudio.TargetPeak, loud[0], 4);
        Assert.Equal(-SpeechAudio.TargetPeak / 2, loud[1], 4);

        var quiet = new[] { 0.01f, -0.005f };
        SpeechAudio.Normalize(quiet);
        Assert.Equal(0.01f * SpeechAudio.MaxGain, quiet[0], 5);

        var clipped = new[] { 1.8f, float.NaN, -0.9f };
        SpeechAudio.Normalize(clipped);
        Assert.Equal(SpeechAudio.TargetPeak, clipped[0], 4);
        Assert.Equal(0f, clipped[1]);

        var silence = new float[4];
        SpeechAudio.Normalize(silence);
        Assert.All(silence, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void FadeSilencesTheEdges()
    {
        var samples = Enumerable.Repeat(1f, 100).ToArray();
        SpeechAudio.Fade(samples, 10);
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[^1]);
        Assert.Equal(0.5f, samples[5], 4);
        Assert.Equal(1f, samples[50]);

        var tiny = new[] { 1f, 1f, 1f };
        SpeechAudio.Fade(tiny, 10); // Clamped to half the length: no index error.
        Assert.Equal(0f, tiny[0]);
    }

    [Fact]
    public void MixerFormatIsStereo48kAndWritesAValidWav()
    {
        var mono = Sine(200, 44100, 0.5, 0.4f);
        var stereo = SpeechAudio.ToMixerFormat(mono, 44100);
        Assert.InRange(stereo.Length, 2 * (24000 - 200), 2 * 24000);
        Assert.Equal(0, stereo.Length % 2);
        for (var i = 0; i < stereo.Length; i += 2) Assert.Equal(stereo[i], stereo[i + 1]);
        Assert.Equal(SpeechAudio.TargetPeak, stereo.Max(), 2);
        Assert.Equal(0f, stereo[0]);

        var path = Path.Combine(Path.GetTempPath(), $"spoka_tts_{Guid.NewGuid():N}.wav");
        try
        {
            SpeechAudio.WriteWav(path, stereo, 48000, 2);
            using var reader = new WaveFileReader(path);
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.Equal(WaveFormatEncoding.IeeeFloat, reader.WaveFormat.Encoding);
            Assert.InRange(reader.TotalTime.TotalSeconds, 0.49, 0.51);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class NeuralTextTests
{
    [Theory]
    [InlineData("C'était « Bohemian Rhapsody », de Queen.", "C'était \"Bohemian Rhapsody\", de Queen.")]
    [InlineData("GG 😀 !", "GG !")]
    [InlineData("  Salut\t\n  toi  ", "Salut toi")]
    [InlineData("L’équipe — bravo…", "L'équipe, bravo...")]
    [InlineData("12\u00A0points", "12 points")]
    [InlineData("👍👍", "")]
    [InlineData("« »", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("こんにちは", "こんにちは")]
    public void PrepareTextCleansForACharacterModel(string? input, string expected) =>
        Assert.Equal(expected, NeuralSpeechEngine.PrepareText(input));
}

public class VoiceModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spoka_voices_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _content = new()
    {
        ["a.onnx"] = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray(),
        ["b.bin"] = Enumerable.Range(0, 1_000).Select(i => (byte)i).ToArray(),
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    private VoiceModelPack Pack(string? corruptFile = null) => new(
        "testpack", "Pack de test", NeuralModelKind.Supertonic, "https://voices.test/pack/resolve/0123",
        _content.Select(kv => new VoiceModelFile(kv.Key, kv.Value.Length,
            kv.Key == corruptFile ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(kv.Value)).ToLowerInvariant())).ToList(),
        "MIT", "https://voices.test/license", "https://voices.test");

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private FakeHandler Serving(HttpStatusCode status = HttpStatusCode.OK) => new(request =>
    {
        var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
        return status == HttpStatusCode.OK && _content.TryGetValue(name, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(status == HttpStatusCode.OK ? HttpStatusCode.NotFound : status);
    });

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task InstallsVerifiesAndDeletes()
    {
        var handler = Serving();
        var store = new VoiceModelStore(_root, new HttpClient(handler));
        var pack = Pack();
        Assert.False(store.IsInstalled(pack));
        var changed = 0;
        store.Changed += _ => changed++;
        var reports = new List<VoiceDownloadProgress>();

        await store.InstallAsync(pack, new SyncProgress<VoiceDownloadProgress>(reports.Add), CancellationToken.None);

        Assert.True(store.IsInstalled(pack));
        Assert.Equal(1, changed);
        Assert.Equal(100, reports[^1].Percent);
        Assert.All(reports.Zip(reports.Skip(1)), p => Assert.True(p.Second.Done >= p.First.Done || p.Second.Done == 0));
        Assert.Equal(_content["a.onnx"], await File.ReadAllBytesAsync(store.FilePath(pack, "a.onnx")));
        Assert.Equal(Path.Combine(_root, "testpack", "b.bin"), store.FilePath(pack, "b.bin"));
        Assert.False(Directory.Exists(Path.Combine(_root, "testpack.partial")));

        // Installed: nothing is downloaded again.
        handler.Requests.Clear();
        await store.InstallAsync(pack, null, CancellationToken.None);
        Assert.Empty(handler.Requests);

        store.Delete(pack);
        Assert.False(store.IsInstalled(pack));
        Assert.False(Directory.Exists(store.PackDirectory(pack)));
    }

    [Fact]
    public void FilePathOnlyAcceptsFilesOfThePack()
    {
        var store = new VoiceModelStore(_root, new HttpClient(Serving()));
        Assert.Throws<ArgumentException>(() => store.FilePath(Pack(), "..\\..\\evil.dll"));
        Assert.Throws<ArgumentException>(() => store.PackDirectory(Pack() with { Id = "..\\up" }));
    }

    [Fact]
    public async Task CorruptedFileIsRejected()
    {
        var store = new VoiceModelStore(_root, new HttpClient(Serving()));
        var pack = Pack(corruptFile: "b.bin");
        var ex = await Assert.ThrowsAsync<DownloadException>(() => store.InstallAsync(pack, null, CancellationToken.None));
        Assert.Contains("corrompu", ex.Message);
        Assert.False(store.IsInstalled(pack));
        Assert.False(File.Exists(Path.Combine(_root, "testpack.partial", "b.bin.part")));
    }

    [Fact]
    public async Task MissingFileGivesAFrenchError()
    {
        var store = new VoiceModelStore(_root, new HttpClient(Serving(HttpStatusCode.NotFound)));
        var ex = await Assert.ThrowsAsync<DownloadException>(() => store.InstallAsync(Pack(), null, CancellationToken.None));
        Assert.Contains("introuvable", ex.Message);
    }

    [Fact]
    public async Task CancelledDownloadResumesWithoutRefetchingVerifiedFiles()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(request =>
        {
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (name == "b.bin") cts.Cancel(); // Cancelled once the first file is complete.
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_content[name]) };
        });
        var store = new VoiceModelStore(_root, new HttpClient(handler));
        var pack = Pack();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(pack, null, cts.Token));
        Assert.False(store.IsInstalled(pack));

        handler.Requests.Clear();
        await store.InstallAsync(pack, null, CancellationToken.None);
        Assert.True(store.IsInstalled(pack));
        Assert.Equal(["/pack/resolve/0123/b.bin"], handler.Requests);
    }

    [Fact]
    public async Task TruncatedFileFromAnOldInstallIsRepaired()
    {
        var store = new VoiceModelStore(_root, new HttpClient(Serving()));
        var pack = Pack();
        await store.InstallAsync(pack, null, CancellationToken.None);
        await File.WriteAllBytesAsync(store.FilePath(pack, "a.onnx"), [1, 2, 3]);
        Assert.False(store.IsInstalled(pack));
        await store.InstallAsync(pack, null, CancellationToken.None);
        Assert.True(store.IsInstalled(pack));
        Assert.Equal(_content["a.onnx"], await File.ReadAllBytesAsync(store.FilePath(pack, "a.onnx")));
    }

    [Theory]
    [InlineData(145295768, "139 Mo")]
    [InlineData(1_048_576, "1 Mo")]
    [InlineData(500, "1 Ko")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 Go")]
    public void SizesAreFormattedInFrench(long bytes, string expected) =>
        Assert.Equal(expected.Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator), VoiceModelStore.FormatSize(bytes));
}

public class NeuralSpeechEngineTests
{
    [Fact]
    public void NativeLibrariesShipWithTheBuild()
    {
        // The Directory.Build.targets trimming must keep the win-x64 natives.
        var dirs = NeuralSpeechEngine.NativeLibraries.SearchDirectories().ToList();
        Assert.Contains(dirs, d => File.Exists(Path.Combine(d, "sherpa-onnx-c-api.dll")) && File.Exists(Path.Combine(d, "onnxruntime.dll")));
    }

    [Fact]
    public void NotInstalledPackGivesAFrenchError()
    {
        var root = Path.Combine(Path.GetTempPath(), "spoka_voices_" + Guid.NewGuid().ToString("N"));
        using var engine = new NeuralSpeechEngine(new VoiceModelStore(root));
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Synthesize(NeuralVoiceCatalog.DefaultVoice, "Bonjour", "fr", 1));
        Assert.Contains("pas téléchargée", ex.Message);
        Assert.False(engine.IsLoaded);
    }

    [Fact]
    public void SymbolOnlyTextIsRejectedBeforeLoading()
    {
        using var engine = new NeuralSpeechEngine(new VoiceModelStore(Path.GetTempPath()));
        Assert.Throws<InvalidOperationException>(() => engine.Synthesize(NeuralVoiceCatalog.DefaultVoice, "🎉🎉", "fr", 1));
    }
}

/// <summary>Downloads Supertonic 3 (about 140 MB) and speaks French; only runs with SPOKA_ONLINE_TESTS=1.</summary>
[Collection("Online")]
public class NeuralSpeechLiveTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") == "1";

    [Fact]
    public async Task DownloadsAndSpeaksFrench_Online()
    {
        if (!Enabled) return;
        // SPOKA_TEST_VOICES_DIR keeps the model between runs (otherwise a fresh temp folder).
        var root = Environment.GetEnvironmentVariable("SPOKA_TEST_VOICES_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "spoka_voices_live");
        var store = new VoiceModelStore(root);
        var pack = NeuralVoiceCatalog.Supertonic3;
        var sw = Stopwatch.StartNew();
        await store.InstallAsync(pack, null, CancellationToken.None);
        output.WriteLine($"Pack prêt en {sw.Elapsed.TotalSeconds:0.0} s ({VoiceModelStore.FormatSize(pack.TotalSize)})");
        Assert.True(store.IsInstalled(pack));

        using var engine = new NeuralSpeechEngine(store);
        sw.Restart();
        engine.Load(pack);
        output.WriteLine($"Chargement : {sw.ElapsedMilliseconds} ms");

        foreach (var voice in new[] { NeuralVoiceCatalog.Voices[0], NeuralVoiceCatalog.Voices[5] })
        {
            sw.Restart();
            var speech = engine.Synthesize(voice, "Bravo Julie, tu remportes le blind test avec douze points !", "fr", 1.0);
            var ms = sw.ElapsedMilliseconds;
            output.WriteLine($"{voice.Name} : {speech.Duration.TotalSeconds:0.00} s d'audio en {ms} ms ({speech.SampleRate} Hz)");
            Assert.Equal(44100, speech.SampleRate);
            Assert.InRange(speech.Duration.TotalSeconds, 1.5, 8);
            Assert.True(speech.Samples.Max(Math.Abs) > 0.05f);

            var stereo = SpeechAudio.ToMixerFormat(speech.Samples, speech.SampleRate);
            var path = Path.Combine(root, $"test-{voice.Key}.wav");
            SpeechAudio.WriteWav(path, stereo, 48000, 2);
            output.WriteLine("  → " + path);
        }

        engine.Unload();
        Assert.False(engine.IsLoaded);
        // Once unloaded, the model files are not locked: « Supprimer » can delete them.
        foreach (var file in pack.Files)
        {
            using var exclusive = new FileStream(store.FilePath(pack, file.Name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
