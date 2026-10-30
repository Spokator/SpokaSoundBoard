using NAudio.Wave;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.Tests;

public class RoutingTests
{
    private const int Frames = 4800;

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

    internal static string WriteTone(float seconds, float freq = 440)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spoka_route_{Guid.NewGuid():N}.wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        for (var i = 0; i < (int)(48000 * seconds); i++)
        {
            var s = 0.5f * MathF.Sin(2 * MathF.PI * freq * i / 48000f);
            writer.WriteSample(s);
            writer.WriteSample(s);
        }
        return path;
    }

    private static float Peak(ReadOnlySpan<float> x)
    {
        var p = 0f;
        foreach (var s in x) p = MathF.Max(p, MathF.Abs(s));
        return p;
    }

    private static float[] Speech(float seconds) => Enumerable.Range(0, (int)(MixerCore.SampleRate * seconds))
        .Select(i => 0.3f * MathF.Sin(2 * MathF.PI * 250 * i / MixerCore.SampleRate)).ToArray();

    [Fact]
    public void Music_GoesToGameButNotDiscord_ByDefault()
    {
        var path = WriteTone(2);
        try
        {
            var mix = new MixSettings { MediaToMicVolume = 1f, MusicVolume = 1f };
            using var music = new MusicPlayer(MixerCore.SampleRate);
            using var pads = new SoundPadPlayer(MixerCore.SampleRate);
            var core = new MixerCore(new VoiceEffectSettings(), mix, music, pads);
            music.Load(path);

            var game = new float[Frames * 2];
            var discord = new float[Frames * 2];
            var monitor = new float[Frames * 2];
            core.Render(game, discord, monitor, Frames);

            Assert.True(Peak(game) > 0.3f);
            Assert.Equal(0f, Peak(discord));
            Assert.True(Peak(monitor) > 0.1f);

            mix.DiscordMusic = true;
            core.Render(game, discord, monitor, Frames);
            Assert.True(Peak(discord) > 0.3f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Discord_GetsNaturalVoice_EvenWhenRadioKeyIsReleased()
    {
        var voice = BuiltInPresets.All.First(p => p.Name == "Pilote d'hélico").Settings.Clone();
        voice.VoiceThresholdDb = -40f;
        var mix = new MixSettings { PushToTalkEnabled = true };
        using var music = new MusicPlayer(MixerCore.SampleRate);
        using var pads = new SoundPadPlayer(MixerCore.SampleRate);
        var core = new MixerCore(voice, mix, music, pads);
        core.SetMicSource(new ArraySource(Speech(2)));

        var game = new float[Frames * 2];
        var discord = new float[Frames * 2];
        for (var i = 0; i < 4; i++) core.Render(game, discord, Span<float>.Empty, Frames);

        // Radio key not held: the game hears nothing, Discord hears the natural voice.
        Assert.True(Peak(game.AsSpan(Frames)) < 0.01f);
        Assert.True(Peak(discord.AsSpan(Frames)) > 0.2f);

        // Natural voice = same waveform as the input (no radio filtering).
        var expected = 0.3f;
        Assert.InRange(Peak(discord.AsSpan(Frames)), expected * 0.8f, expected * 1.2f);
    }

    [Fact]
    public void Game_CanUseNaturalVoiceToo()
    {
        var voice = new VoiceEffectSettings { PitchSemitones = 7, VoiceThresholdDb = -40f, CompressorEnabled = false };
        var mix = new MixSettings { GameVoiceEffects = false, DiscordVoiceEffects = true };
        using var music = new MusicPlayer(MixerCore.SampleRate);
        using var pads = new SoundPadPlayer(MixerCore.SampleRate);
        var core = new MixerCore(voice, mix, music, pads);
        var input = Speech(1);
        core.SetMicSource(new ArraySource(input));

        var game = new float[Frames * 2];
        var discord = new float[Frames * 2];
        core.Render(game, discord, Span<float>.Empty, Frames);
        core.Render(game, discord, Span<float>.Empty, Frames);

        // Dry voice is the input itself (after the 80 Hz rumble filter); the wet one is pitch-shifted.
        var diffDry = 0f;
        var diffWet = 0f;
        for (var f = Frames / 2; f < Frames; f++)
        {
            var src = input[Frames + f];
            diffDry += MathF.Abs(game[f * 2] - src);
            diffWet += MathF.Abs(discord[f * 2] - src);
        }
        Assert.True(diffDry < diffWet * 0.5f, $"dry={diffDry} wet={diffWet}");
    }

    [Fact]
    public void Voice_CanBeRemovedFromDiscord()
    {
        var mix = new MixSettings { DiscordVoice = false };
        using var music = new MusicPlayer(MixerCore.SampleRate);
        using var pads = new SoundPadPlayer(MixerCore.SampleRate);
        var core = new MixerCore(new VoiceEffectSettings { VoiceThresholdDb = -40f }, mix, music, pads);
        core.SetMicSource(new ArraySource(Speech(1)));
        var game = new float[Frames * 2];
        var discord = new float[Frames * 2];
        core.Render(game, discord, Span<float>.Empty, Frames);
        core.Render(game, discord, Span<float>.Empty, Frames);
        Assert.True(Peak(game) > 0.1f);
        Assert.Equal(0f, Peak(discord));
    }

    [Fact]
    public async Task SoundPad_PlaysOnlyTheSelectedExcerpt()
    {
        var path = WriteTone(3);
        try
        {
            using var pads = new SoundPadPlayer(MixerCore.SampleRate);
            var id = Guid.NewGuid();
            await pads.PlayAsync(id, path, 1f, false, startSeconds: 1, endSeconds: 1.5);

            var buffer = new float[MixerCore.SampleRate * 2]; // 1 second
            pads.Read(buffer, 0, buffer.Length);
            var lastNonZero = Array.FindLastIndex(buffer, s => MathF.Abs(s) > 1e-4f);
            var seconds = (lastNonZero + 1) / 2.0 / MixerCore.SampleRate;
            Assert.InRange(seconds, 0.45, 0.55);
            Assert.False(pads.IsPlaying(id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SoundPad_LoopRestartsAtExcerptStart()
    {
        var path = WriteTone(3);
        try
        {
            using var pads = new SoundPadPlayer(MixerCore.SampleRate);
            var id = Guid.NewGuid();
            await pads.PlayAsync(id, path, 1f, true, startSeconds: 2, endSeconds: 2.2);
            var buffer = new float[MixerCore.SampleRate * 2];
            pads.Read(buffer, 0, buffer.Length);
            Assert.True(pads.IsPlaying(id));
            Assert.True(Peak(buffer.AsSpan(buffer.Length - 2000)) > 0.3f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CableFamilies_DistinguishCables()
    {
        var vb = new AudioDevice("1", "CABLE Input (VB-Audio Virtual Cable)", true);
        var vb16 = new AudioDevice("2", "CABLE In 16ch (VB-Audio Virtual Cable)", true);
        var vac = new AudioDevice("3", "Line 1 (Virtual Audio Cable)", true);
        var cableA = new AudioDevice("4", "CABLE-A Input (VB-Audio Cable A)", true);
        var speakers = new AudioDevice("5", "Haut-parleurs (Realtek(R) Audio)", false);

        Assert.True(DeviceService.SameCable(vb, vb16));
        Assert.False(DeviceService.SameCable(vb, vac));
        Assert.False(DeviceService.SameCable(vb, cableA));
        Assert.Equal(1, DeviceService.CountDistinctCables([vb, vb16, speakers]));
        Assert.Equal(2, DeviceService.CountDistinctCables([vb, vb16, vac, speakers]));
        Assert.Equal("3", DeviceService.FindSecondCableOutput([speakers, vb, vb16, vac], vb)?.Id);
        Assert.True(DeviceService.LooksVirtual(vac.Name));
    }

    [Fact]
    public void Downloader_ParsesProgressAndErrors()
    {
        Assert.Equal(42.5, MediaDownloader.ParsePercent("  42.5%"));
        Assert.Equal(0, MediaDownloader.ParsePercent("NA"));
        Assert.Equal("Ce site n'est pas pris en charge.", MediaDownloader.FriendlyError("ERROR: Unsupported URL: https://x"));
        Assert.True(MediaDownloader.LooksLikeUrl("https://www.youtube.com/watch?v=abc"));
        Assert.False(MediaDownloader.LooksLikeUrl("C:\\musique\\a.mp3"));
        Assert.False(MediaDownloader.LooksLikeUrl("bonjour"));
    }

    /// <summary>Real network test, opt-in: set SPOKA_ONLINE_TESTS=1.</summary>
    [Fact]
    public async Task Downloader_ImportsYouTubeAudio_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;

        var dir = Path.Combine(Path.GetTempPath(), "spoka_online_test");
        var downloader = new MediaDownloader(dir);
        var items = await downloader.DownloadAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", "Sons", false, null, null, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.True(File.Exists(item.FilePath));
        Assert.Contains("zoo", item.Title, StringComparison.OrdinalIgnoreCase);
        var (reader, _) = AudioFileLoader.Open(item.FilePath, MixerCore.SampleRate);
        using (reader) Assert.True(reader.TotalTime > TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("https://www.myinstants.com/en/instant/vine-boom-sound-70972/")]
    [InlineData("https://soundcloud.com/forss/flickermood")]
    public async Task Downloader_ImportsOtherSites_Online(string url)
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;

        var downloader = new MediaDownloader(Path.Combine(Path.GetTempPath(), "spoka_online_test"));
        var items = await downloader.DownloadAsync(url, "Sons", false, null, null, CancellationToken.None);
        var item = Assert.Single(items);
        var (reader, _) = AudioFileLoader.Open(item.FilePath, MixerCore.SampleRate);
        using (reader) Assert.True(reader.TotalTime > TimeSpan.FromSeconds(0.3));
    }

    [Fact]
    public async Task Downloader_ConvertsUnsupportedFormats_Online()
    {
        if (Environment.GetEnvironmentVariable("SPOKA_ONLINE_TESTS") != "1") return;

        var downloader = new MediaDownloader(Path.Combine(Path.GetTempPath(), "spoka_online_test"));
        downloader.FormatOverride = "bestaudio[ext=webm]";
        var items = await downloader.DownloadAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", "Opus", false, null, null, CancellationToken.None);
        var item = Assert.Single(items);
        Assert.Equal(".mp3", Path.GetExtension(item.FilePath));
        var (reader, _) = AudioFileLoader.Open(item.FilePath, MixerCore.SampleRate);
        using (reader) Assert.True(reader.TotalTime > TimeSpan.FromSeconds(10));
    }
}
