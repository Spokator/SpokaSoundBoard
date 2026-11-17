using NAudio.Wave;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Speech;

namespace SpokaSoundBoard.Tests;

/// <summary>
/// 2.16.3: .ogg files that hold Opus (voice messages, recorders, games) could not be imported (« Could not initialize
/// container! »): only Vorbis was read. Small real files in Assets: a 2 s, 440 Hz tone with a syllable-like envelope.
/// </summary>
public sealed class OggImportTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static float[] Decode(string file)
    {
        var (reader, provider) = AudioFileLoader.Open(file, 48000);
        using (reader)
        {
            var all = new List<float>();
            var buffer = new float[4800];
            int n;
            while ((n = provider.Read(buffer, 0, buffer.Length)) > 0) all.AddRange(buffer.Take(n));
            return [.. all];
        }
    }

    /// <summary>Dominant frequency of the left channel, from zero crossings.</summary>
    private static double Frequency(float[] stereo)
    {
        var crossings = 0;
        for (var i = 2; i < stereo.Length; i += 2)
            if (stereo[i - 2] < 0 && stereo[i] >= 0) crossings++;
        return crossings / (stereo.Length / 2 / 48000.0);
    }

    [Theory]
    [InlineData("tone440_opus_mono.ogg")]
    [InlineData("tone440_opus_stereo.opus")]
    [InlineData("tone440_vorbis.ogg")]
    public void OggAndOpusFiles_DecodeToTheRightSound(string name)
    {
        var file = Asset(name);
        var stereo = Decode(file);
        Assert.InRange(stereo.Length / 2 / 48000.0, 1.95, 2.1);
        Assert.InRange(Frequency(stereo), 430, 450);
        Assert.InRange(stereo.Max(MathF.Abs), 0.2f, 0.4f);
    }

    [Fact]
    public void OpusIsRecognised_ByItsContent_NotItsExtension()
    {
        Assert.True(OpusWaveReader.IsOpus(Asset("tone440_opus_mono.ogg")));
        Assert.True(OpusWaveReader.IsOpus(Asset("tone440_opus_stereo.opus")));
        Assert.False(OpusWaveReader.IsOpus(Asset("tone440_vorbis.ogg")));
        Assert.True(AudioFileLoader.IsSupported("réplique.opus"));
    }

    [Fact]
    public void Opus_StereoKeepsItsChannels_AndSeeks()
    {
        var stereo = Decode(Asset("tone440_opus_stereo.opus"));
        var left = stereo.Where((_, i) => i % 2 == 0).Max(MathF.Abs);
        var right = stereo.Where((_, i) => i % 2 == 1).Max(MathF.Abs);
        Assert.InRange(right / left, 0.4f, 0.6f);                 // written at half the level

        using var reader = new OpusWaveReader(Asset("tone440_opus_mono.ogg"));
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.95, 2.1);
        reader.CurrentTime = TimeSpan.FromSeconds(1.5);
        var rest = new byte[48000 * 2 * 4];
        var read = reader.Read(rest, 0, rest.Length);
        Assert.InRange(read / 8 / 48000.0, 0.4, 0.6);              // about half a second left
    }

    [Fact]
    public void SeveralOpusFiles_MakeOneVoice()
    {
        // What « Créer une voix » does with the imported files: decode each, then keep the speech of all of them.
        var files = new[] { Asset("tone440_opus_mono.ogg"), Asset("tone440_opus_stereo.opus"), Asset("tone440_vorbis.ogg") };
        var clips = VoiceImprintMaker.Decode(files);
        Assert.Equal(3, clips.Count);
        var speech = SpeechSelector.Select(clips, 48000);
        Assert.InRange(speech.Seconds, 5.5, 6.2);
    }
}
