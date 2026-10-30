using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>Captures a microphone with WASAPI and exposes it as a mono 48 kHz sample provider.</summary>
public sealed class MicInput : IDisposable
{
    private static readonly Guid PcmSubType = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly WasapiCapture _capture;
    private readonly FloatRingBuffer _ring;
    private readonly WaveFormat _format;
    private readonly bool _isFloat;
    private float[] _mono = new float[4096];

    public MicInput(MMDevice device)
    {
        _capture = new WasapiCapture(device, true, 20);
        _format = _capture.WaveFormat;
        _isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat
                   || _format is WaveFormatExtensible ext && ext.SubFormat == FloatSubType;
        if (!_isFloat && _format.Encoding != WaveFormatEncoding.Pcm
            && !(_format is WaveFormatExtensible pcm && pcm.SubFormat == PcmSubType))
            throw new NotSupportedException($"Format de micro non supporté : {_format}");

        _ring = new FloatRingBuffer(_format.SampleRate);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
        DeviceName = device.FriendlyName;
    }

    public string DeviceName { get; }

    public event Action<Exception?>? Stopped;

    public void Start() => _capture.StartRecording();

    /// <summary>Mono, 48 kHz provider reading the captured audio with bounded latency.</summary>
    public ISampleProvider CreateProvider(int sampleRate)
    {
        ISampleProvider provider = new JitterBufferProvider(_ring, _format.SampleRate, 1, 20f, 80f);
        if (_format.SampleRate != sampleRate)
            provider = new WdlResamplingSampleProvider(provider, sampleRate);
        return provider;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var channels = _format.Channels;
        var bytesPerSample = _format.BitsPerSample / 8;
        var frames = e.BytesRecorded / (bytesPerSample * channels);
        if (frames <= 0) return;
        if (_mono.Length < frames) _mono = new float[frames];

        var data = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += ReadSample(data, (f * channels + c) * bytesPerSample, bytesPerSample);
            _mono[f] = sum / channels;
        }
        _ring.Write(_mono.AsSpan(0, frames));
    }

    private float ReadSample(ReadOnlySpan<byte> data, int index, int bytesPerSample)
    {
        if (_isFloat)
            return bytesPerSample == 8
                ? (float)BitConverter.ToDouble(data.Slice(index, 8))
                : BitConverter.ToSingle(data.Slice(index, 4));

        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(data.Slice(index, 2)) / 32768f,
            3 => ((data[index] << 8) | (data[index + 1] << 16) | (data[index + 2] << 24)) / 2147483648f,
            4 => BitConverter.ToInt32(data.Slice(index, 4)) / 2147483648f,
            _ => (data[index] - 128) / 128f,
        };
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        try { _capture.StopRecording(); } catch { /* device may already be gone */ }
        _capture.Dispose();
    }
}
