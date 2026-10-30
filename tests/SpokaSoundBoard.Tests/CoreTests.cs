using NAudio.Wave;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Dsp;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

public class DspTests
{
    private const int Rate = 48000;

    private static float[] Sine(float freq, float amp, int samples, int rate = Rate) =>
        Enumerable.Range(0, samples).Select(i => amp * MathF.Sin(2 * MathF.PI * freq * i / rate)).ToArray();

    private static float Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (var s in x) sum += s * s;
        return (float)Math.Sqrt(sum / Math.Max(1, x.Length));
    }

    [Fact]
    public void Biquad_LowPass_AttenuatesHighFrequencies()
    {
        var f = new Biquad();
        f.SetLowPass(Rate, 1000f);
        var output = Sine(8000, 1f, Rate / 4).Select(f.Process).ToArray();
        Assert.True(Rms(output.AsSpan(2000)) < 0.05f);
    }

    [Fact]
    public void PitchShifter_OctaveUp_DoublesDominantFrequency()
    {
        var p = new PitchShifter(Rate);
        p.SetSemitones(12);
        var output = Sine(200, 0.5f, Rate).Select(p.Process).ToArray();

        // Count zero crossings over the last half second: 200 Hz → ~200, 400 Hz → ~400.
        var crossings = 0;
        for (var i = Rate / 2 + 1; i < Rate; i++)
            if (output[i - 1] < 0 && output[i] >= 0) crossings++;
        Assert.InRange(crossings, 170, 230);
        Assert.All(output, s => Assert.True(float.IsFinite(s)));
    }

    [Theory]
    [InlineData(0.6f)]
    [InlineData(0.95f)]
    public void Reverb_StaysStableAndFinite(float size)
    {
        var r = new ReverbEffect(Rate) { RoomSize = size, Mix = 1f };
        var noise = new NoiseGenerator(1);
        for (var i = 0; i < Rate * 3; i++)
        {
            var y = r.Process(i < Rate ? noise.Next() : 0f);
            Assert.True(float.IsFinite(y) && MathF.Abs(y) < 10f);
        }
    }

    [Fact]
    public void Limiter_KeepsOutputBelowCeiling()
    {
        var lim = new Limiter(Rate);
        var data = Sine(440, 4f, Rate).SelectMany(s => new[] { s, s }).ToArray();
        lim.ProcessStereo(data);
        Assert.True(data.Max(MathF.Abs) <= 1f);
    }

    [Fact]
    public void Ducker_LowersThenRestoresGain()
    {
        var d = new Ducker(Rate);
        d.Configure(50, 300);
        for (var i = 0; i < Rate / 2; i++) d.Next(true, 0.2f);
        Assert.InRange(d.Gain, 0.19f, 0.21f);
        for (var i = 0; i < Rate * 2; i++) d.Next(false, 0.2f);
        Assert.InRange(d.Gain, 0.99f, 1.0f);
    }

    [Fact]
    public void VoiceProcessor_DetectsVoiceAboveThreshold()
    {
        var settings = new VoiceEffectSettings { VoiceThresholdDb = -40f };
        var vp = new VoiceProcessor(Rate, settings);
        var output = new float[480];

        vp.Process(new float[480], output, true, false, false);
        Assert.False(vp.VoiceActive);

        var loud = Sine(300, 0.3f, 480);
        for (var i = 0; i < 5; i++) vp.Process(loud, output, true, false, false);
        Assert.True(vp.VoiceActive);
        Assert.True(vp.Transmitting);
    }

    [Fact]
    public void VoiceProcessor_PushToTalk_BlocksVoiceWhenKeyReleased()
    {
        var vp = new VoiceProcessor(Rate, new VoiceEffectSettings());
        var loud = Sine(300, 0.3f, Rate / 2);
        var output = new float[loud.Length];

        vp.Process(loud, output, true, true, false);
        Assert.True(Rms(output.AsSpan(loud.Length / 2)) < 0.001f);
        Assert.False(vp.Transmitting);

        vp.Process(loud, output, true, true, true);
        Assert.True(Rms(output.AsSpan(loud.Length / 2)) > 0.05f);
        Assert.True(vp.Transmitting);
    }

    [Fact]
    public void VoiceProcessor_AllPresets_ProduceFiniteAudio()
    {
        foreach (var preset in BuiltInPresets.All)
        {
            var settings = preset.Settings.Clone();
            var vp = new VoiceProcessor(Rate, settings);
            var input = Sine(220, 0.5f, Rate);
            var output = new float[input.Length];
            vp.Process(input, output, true, false, false);
            Assert.All(output, s => Assert.True(float.IsFinite(s), preset.Name));
            Assert.True(Rms(output) > 0.01f, $"{preset.Name} est silencieux");
        }
    }

    [Fact]
    public void RadioPreset_AddsSquelchAndBeepAfterTransmission()
    {
        var settings = BuiltInPresets.All.First(p => p.Name == "Pilote d'hélico").Settings.Clone();
        var vp = new VoiceProcessor(Rate, settings);
        var output = new float[Rate];
        vp.Process(Sine(300, 0.3f, Rate), output, true, false, false);

        // After the voice stops (and the hold expires) the radio must emit its closing squelch / beep.
        var tail = new float[Rate];
        vp.Process(new float[Rate], tail, true, false, false);
        Assert.True(tail.Max(MathF.Abs) > 0.05f);
        Assert.False(vp.Transmitting);
    }
}

public class MixerTests
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

    private static string WriteToneFile(float seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"spoka_test_{Guid.NewGuid():N}.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        using var writer = new WaveFileWriter(path, format);
        var samples = (int)(44100 * seconds);
        for (var i = 0; i < samples; i++)
        {
            var s = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 44100f);
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

    [Fact]
    public void Music_IsDuckedWhileSpeaking_AndSentToBothBuses()
    {
        var path = WriteToneFile(4);
        try
        {
            var voice = new VoiceEffectSettings { VoiceThresholdDb = -40f, CompressorEnabled = false };
            var mix = new MixSettings { DuckingAmountDb = -20f, DuckingAttackMs = 20f, MediaToMicVolume = 1f, MusicVolume = 1f, MonitorVolume = 1f };
            using var music = new MusicPlayer(MixerCore.SampleRate);
            using var pads = new SoundPadPlayer(MixerCore.SampleRate);
            var core = new MixerCore(voice, mix, music, pads);
            music.Load(path);

            const int frames = 4800;
            var main = new float[frames * 2];
            var monitor = new float[frames * 2];

            // Silence on the mic: music at full level on both buses.
            core.Render(main, monitor, frames);
            core.Render(main, monitor, frames);
            var musicOnly = Peak(monitor.AsSpan(frames));
            Assert.InRange(musicOnly, 0.4f, 0.6f);
            Assert.InRange(Peak(main.AsSpan(frames)), 0.4f, 0.6f);
            Assert.InRange(core.Meters.DuckGain, 0.99f, 1.01f);

            // Speech on the mic: the music must drop by ~20 dB.
            var speech = Enumerable.Range(0, MixerCore.SampleRate * 2)
                .Select(i => 0.3f * MathF.Sin(2 * MathF.PI * 250 * i / MixerCore.SampleRate)).ToArray();
            core.SetMicSource(new ArraySource(speech));
            for (var i = 0; i < 5; i++) core.Render(main, monitor, frames);

            Assert.True(core.Meters.VoiceActive);
            Assert.InRange(core.Meters.DuckGain, 0.09f, 0.12f);
            Assert.True(Peak(monitor.AsSpan(frames)) < musicOnly * 0.15f);
            Assert.True(Peak(main) > 0.2f, "la voix doit passer dans le micro virtuel");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MediaToMicDisabled_KeepsMusicOutOfVirtualMic()
    {
        var path = WriteToneFile(1);
        try
        {
            var mix = new MixSettings { MediaToMicEnabled = false, MonitorVolume = 1f };
            using var music = new MusicPlayer(MixerCore.SampleRate);
            using var pads = new SoundPadPlayer(MixerCore.SampleRate);
            var core = new MixerCore(new VoiceEffectSettings(), mix, music, pads);
            music.Load(path);

            var main = new float[960];
            var monitor = new float[960];
            core.Render(main, monitor, 480);
            Assert.Equal(0f, Peak(main));
            Assert.True(Peak(monitor) > 0.1f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SoundPads_PlayAndFinish()
    {
        var path = WriteToneFile(0.2f);
        try
        {
            using var pads = new SoundPadPlayer(MixerCore.SampleRate);
            var id = Guid.NewGuid();
            await pads.PlayAsync(id, path, 1f, false);
            Assert.True(pads.IsPlaying(id));

            var buffer = new float[MixerCore.SampleRate * 2];
            pads.Read(buffer, 0, buffer.Length);
            Assert.True(Peak(buffer) > 0.3f);
            Assert.False(pads.IsPlaying(id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MusicPlayer_RaisesTrackEnded()
    {
        var path = WriteToneFile(0.1f);
        try
        {
            using var music = new MusicPlayer(MixerCore.SampleRate);
            var ended = false;
            music.TrackEnded += () => ended = true;
            music.Load(path);
            var buffer = new float[MixerCore.SampleRate];
            music.Read(buffer, 0, buffer.Length);
            Assert.True(ended);
            Assert.False(music.IsPlaying);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class InfrastructureTests
{
    [Fact]
    public void RingBuffer_DropsOldestWhenFull()
    {
        var ring = new FloatRingBuffer(4);
        ring.Write([1, 2, 3]);
        ring.Write([4, 5, 6]);
        var dest = new float[4];
        Assert.Equal(4, ring.Read(dest));
        Assert.Equal([3f, 4f, 5f, 6f], dest);
    }

    [Fact]
    public void JitterBuffer_PrimesThenLimitsLatency()
    {
        var ring = new FloatRingBuffer(48000);
        var jb = new JitterBufferProvider(ring, 48000, 1, 10f, 40f);
        var out480 = new float[480];

        ring.Write(Enumerable.Repeat(1f, 100).ToArray());
        jb.Read(out480, 0, 480);
        Assert.All(out480, s => Assert.Equal(0f, s)); // still priming

        ring.Write(Enumerable.Repeat(1f, 20000).ToArray());
        jb.Read(out480, 0, 480);
        Assert.All(out480, s => Assert.Equal(1f, s));
        Assert.True(ring.Count <= 480 + 1920 + 480, $"latence trop élevée : {ring.Count}");
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var s = new AppSettings();
        s.Voice.RadioEnabled = true;
        s.Voice.PitchSemitones = -3;
        s.Mix.DuckingAmountDb = -20;
        s.Pads.Add(new SoundPad { Name = "Alerte", FilePath = @"C:\x.mp3" });
        s.Pads[0].Hotkey.Set(0x70, HotkeyModifiers.Ctrl);
        s.Playlist.Add(new TrackItem { FilePath = @"C:\music\song.mp3" });
        s.Hotkeys.PushToTalk.Set(KeyNames.VkXButton1, HotkeyModifiers.None);
        s.CustomPresets.Add(new EffectPreset { Name = "Mon preset", Settings = s.Voice.Clone() });

        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));

        Assert.True(back.Voice.RadioEnabled);
        Assert.Equal(-3, back.Voice.PitchSemitones);
        Assert.Equal(-20, back.Mix.DuckingAmountDb);
        Assert.Equal("Alerte", back.Pads[0].Name);
        Assert.Equal(s.Pads[0].Id, back.Pads[0].Id);
        Assert.Equal(0x70, back.Pads[0].Hotkey.VirtualKey);
        Assert.Equal(HotkeyModifiers.Ctrl, back.Pads[0].Hotkey.Modifiers);
        Assert.Equal("song", back.Playlist[0].Title);
        Assert.Equal("Souris 4", back.Hotkeys.PushToTalk.Display);
        Assert.True(back.CustomPresets[0].Settings.RadioEnabled);
    }

    [Fact]
    public void DeviceService_DetectsVirtualCables()
    {
        Assert.True(DeviceService.LooksVirtual("CABLE Input (VB-Audio Virtual Cable)"));
        Assert.True(DeviceService.LooksVirtual("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        Assert.False(DeviceService.LooksVirtual("Casque (Realtek(R) Audio)"));
        Assert.False(DeviceService.LooksVirtual("NVIDIA Virtual Audio Device"));

        var outputs = new[]
        {
            new AudioDevice("1", "Haut-parleurs", false),
            new AudioDevice("2", "CABLE In 16ch (VB-Audio Virtual Cable)", true),
            new AudioDevice("3", "CABLE Input (VB-Audio Virtual Cable)", true),
        };
        Assert.Equal("3", DeviceService.FindVirtualCableOutput(outputs)?.Id);
    }
}
