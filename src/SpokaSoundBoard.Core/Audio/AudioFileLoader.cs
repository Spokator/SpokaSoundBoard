using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpokaSoundBoard.Core.Audio;

public static class AudioFileLoader
{
    public static readonly string[] SupportedExtensions =
        [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4"];

    public static string DialogFilter =>
        "Fichiers audio|" + string.Join(";", SupportedExtensions.Select(e => "*" + e)) + "|Tous les fichiers|*.*";

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens an audio file and converts it to stereo float at the requested sample rate.</summary>
    public static (WaveStream Reader, ISampleProvider Provider) Open(string path, int sampleRate)
    {
        WaveStream reader = Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);

        try
        {
            var provider = reader as ISampleProvider ?? reader.ToSampleProvider();
            provider = provider.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(provider),
                2 => provider,
                _ => new FirstTwoChannelsProvider(provider),
            };
            if (provider.WaveFormat.SampleRate != sampleRate)
                provider = new WdlResamplingSampleProvider(provider, sampleRate);
            return (reader, provider);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private sealed class FirstTwoChannelsProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var frames = count / 2;
            if (_buffer.Length < frames * channels) _buffer = new float[frames * channels];
            var read = source.Read(_buffer, 0, frames * channels) / channels;
            for (var i = 0; i < read; i++)
            {
                buffer[offset + i * 2] = _buffer[i * channels];
                buffer[offset + i * 2 + 1] = _buffer[i * channels + 1];
            }
            return read * 2;
        }
    }
}
