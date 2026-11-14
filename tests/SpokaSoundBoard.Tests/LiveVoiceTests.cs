using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.Tests;

/// <summary>2.14: « Parler avec la voix choisie » (speech recognition, then the synthetic voice says it).</summary>
public sealed class LiveVoiceTests
{
    private sealed class ArraySource(float[] data) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MixerCore.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static float[] Sine(float freq, int samples, int rate = 48000, float amplitude = 0.5f) =>
        Enumerable.Range(0, samples).Select(i => amplitude * MathF.Sin(2 * MathF.PI * freq * i / rate)).ToArray();

    private static float Peak(IEnumerable<float> x) => x.Select(MathF.Abs).DefaultIfEmpty().Max();

    [Fact]
    public void Decimator_KeepsSpeech_RemovesWhatWouldAlias()
    {
        var speech = new List<float>();
        new Decimator().Process(Sine(1000, 48000), speech);
        Assert.Equal(16000, speech.Count);
        Assert.InRange(Peak(speech.Skip(200)), 0.47f, 0.53f);

        // 12 kHz cannot exist at 16 kHz: it must not fold back into the voice band.
        var high = new List<float>();
        new Decimator().Process(Sine(12000, 48000), high);
        Assert.True(Peak(high.Skip(200)) < 0.01f, $"12 kHz leaked: {Peak(high.Skip(200))}");
    }

    [Fact]
    public void Decimator_GivesTheSameResult_WhateverTheBlockSizes()
    {
        var input = Sine(440, 9000).Zip(Sine(3000, 9000, amplitude: 0.2f), (a, b) => a + b).ToArray();
        var whole = new List<float>();
        new Decimator().Process(input, whole);

        var pieces = new List<float>();
        var d = new Decimator();
        var rng = new Random(3);
        for (var i = 0; i < input.Length;)
        {
            var n = Math.Min(rng.Next(1, 700), input.Length - i);
            d.Process(input.AsSpan(i, n), pieces);
            i += n;
        }
        Assert.Equal(whole.Count, pieces.Count);
        for (var i = 0; i < whole.Count; i++) Assert.Equal(whole[i], pieces[i], 5);
    }

    [Theory]
    [InlineData("  Salut   tout le monde.  ", "Salut tout le monde.")]
    [InlineData("Euh…", null)]
    [InlineData("hum, euh.", null)]
    [InlineData("...", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Euh, attention derrière !", "Euh, attention derrière !")]
    public void RecognizedText_IsCleaned(string? heard, string? expected) => Assert.Equal(expected, LiveSpeechText.Clean(heard));

    [Fact]
    public void TheVoiceHeardThroughSpeakers_IsNotRepeated()
    {
        string[] spoken = ["Bien joué l'équipe, on a gagné la partie !"];
        Assert.True(LiveSpeechText.IsEcho("Bien jouer l'équipe on a gagné la partie.", spoken));
        Assert.True(LiveSpeechText.IsEcho("on a gagné la partie", spoken));
        Assert.False(LiveSpeechText.IsEcho("Attention, un ennemi à gauche !", spoken));
        Assert.False(LiveSpeechText.IsEcho("On repart pour une partie ?", spoken));
        Assert.False(LiveSpeechText.IsEcho("Bien joué", []));
    }

    [Fact]
    public void TheMicIsListened_ButTheRealVoiceIsNotSent()
    {
        var mix = new MixSettings();
        using var music = new MusicPlayer(MixerCore.SampleRate);
        using var pads = new SoundPadPlayer(MixerCore.SampleRate);
        var core = new MixerCore(new VoiceEffectSettings { GateEnabled = false, VoiceThresholdDb = -60 }, mix, music, pads);
        core.SetMicSource(new ArraySource(Sine(250, 48000 * 2, amplitude: 0.3f)));
        var tap = new FloatRingBuffer(48000 * 4);
        core.SpeechTap = tap;
        core.VoiceReplaced = true;

        var game = new float[4800 * 2];
        var discord = new float[4800 * 2];
        for (var i = 0; i < 5; i++) core.Render(game, discord, Span<float>.Empty, 4800);
        Assert.True(Peak(game) < 0.001f, "the real voice reached the game");
        Assert.True(Peak(discord) < 0.001f, "the real voice reached Discord");
        Assert.Equal(4800 * 5, tap.Count);
        var heard = new float[tap.Count];
        tap.Read(heard);
        Assert.InRange(Peak(heard), 0.25f, 0.35f);

        // Back to normal: the voice passes again, nothing is listened to.
        core.SpeechTap = null;
        core.VoiceReplaced = false;
        for (var i = 0; i < 3; i++) core.Render(game, discord, Span<float>.Empty, 4800);
        Assert.True(Peak(game) > 0.1f);
    }

    [Fact]
    public void RadioMode_OnlyListensWhileTheKeyIsHeld()
    {
        var mix = new MixSettings { PushToTalkEnabled = true };
        using var music = new MusicPlayer(MixerCore.SampleRate);
        using var pads = new SoundPadPlayer(MixerCore.SampleRate);
        var core = new MixerCore(new VoiceEffectSettings(), mix, music, pads);
        core.SetMicSource(new ArraySource(Sine(250, 48000 * 2, amplitude: 0.3f)));
        var tap = new FloatRingBuffer(48000 * 4);
        core.SpeechTap = tap;
        core.VoiceReplaced = true;
        var game = new float[4800 * 2];

        core.Render(game, Span<float>.Empty, Span<float>.Empty, 4800);
        var block = new float[4800];
        tap.Read(block);
        Assert.True(Peak(block) == 0f, "listened without the radio key");

        core.PushToTalkHeld = true;
        core.Render(game, Span<float>.Empty, Span<float>.Empty, 4800);
        tap.Read(block);
        Assert.True(Peak(block) > 0.25f);
        Assert.True(Peak(game) < 0.001f, "the real voice reached the game");

        // Microphone muted by the user: nothing is listened to at all.
        mix.MicEnabled = false;
        var before = tap.Count;
        core.Render(game, Span<float>.Empty, Span<float>.Empty, 4800);
        Assert.Equal(before, tap.Count);
    }

    [Fact]
    public void Settings_KeepTheSpokenLanguage_AndOldFilesLoad()
    {
        var s = new AppSettings();
        s.TextToSpeech.LiveLanguage = "es";
        s.Hotkeys.ToggleLiveVoice.Set(0x77, HotkeyModifiers.Ctrl);
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal("es", back.TextToSpeech.LiveLanguage);
        Assert.Equal(0x77, back.Hotkeys.ToggleLiveVoice.VirtualKey);

        var old = SettingsStore.Deserialize("""{ "TextToSpeech": { "Language": "it", "LiveLanguage": "it" } }""");
        Assert.Equal("fr", old.TextToSpeech.LiveLanguage); // Italian is not understood by the model
        Assert.Equal("it", old.TextToSpeech.Language);     // but the voices can still read it
        Assert.False(old.Hotkeys.ToggleLiveVoice.IsSet);
        Assert.Equal("fr", SettingsStore.Deserialize("{}").TextToSpeech.LiveLanguage);
    }

    [Fact]
    public void RecognitionModel_IsPinned_AndTheDetectorComesFromItsOwnAddress()
    {
        var pack = SpeechRecognitionCatalog.Canary180mFlash;
        Assert.Equal(NeuralModelKind.Canary, pack.Kind);
        Assert.All(pack.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.Contains("/resolve/9077164e0d3dd1d5353743e89ceaa1d3a770838c", pack.UrlOf(pack.Files.First(f => f.Name == "encoder.int8.onnx")).AbsoluteUri);
        Assert.Equal("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
            pack.UrlOf(pack.Files.First(f => f.Name == "silero_vad.onnx")).AbsoluteUri);
        Assert.InRange(pack.TotalSize, 200_000_000, 220_000_000);
        Assert.Equal(["fr", "en", "es", "de"], SpeechRecognitionCatalog.Languages.Select(l => l.Code));
    }

    /// <summary>
    /// The real models, end to end: a natural voice says phrases, recognition writes them down. Opt-in: set
    /// SPOKA_SPEECH_MODELS to a folder holding the « supertonic3 » and « canary180m » packs.
    /// </summary>
    [Fact]
    public void RealModels_WriteDownWhatTheVoiceSays()
    {
        var root = Environment.GetEnvironmentVariable("SPOKA_SPEECH_MODELS");
        if (string.IsNullOrEmpty(root)) return;
        var store = new VoiceModelStore(root);
        Assert.True(store.IsInstalled(SpeechRecognitionCatalog.Canary180mFlash));
        using var tts = new NeuralSpeechEngine(store);
        var voice = NeuralVoiceCatalog.Voices.First(v => !v.IsFemale);

        string[] phrases = ["Salut tout le monde, je reviens dans deux minutes.", "Est-ce que quelqu'un peut me lancer des munitions ?"];
        var audio = new List<float>();
        foreach (var p in phrases)
        {
            var s = tts.Synthesize(voice, p, "fr", 1.0);
            audio.AddRange(SpeechAudio.Resample(s.Samples, s.SampleRate, 48000));
            audio.AddRange(new float[48000]); // a pause between the two phrases
        }

        var heard = LiveSpeechRecognizer.Transcribe(store, "fr", audio.ToArray());
        Assert.Equal(2, heard.Count);
        for (var i = 0; i < 2; i++)
        {
            var expected = LiveSpeechText.Words(phrases[i]);
            var got = LiveSpeechText.Words(heard[i]).ToHashSet();
            Assert.True(expected.Count(got.Contains) >= expected.Count * 0.8, $"« {heard[i]} » for « {phrases[i]} »");
        }
    }
}
