using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Audio;

public enum EngineState
{
    Stopped,
    Running,
    Error,
}

/// <summary>
/// Owns the audio devices. The virtual-cable output (when present) is the master clock; the headphone
/// output is fed through a jitter buffer. Without a virtual cable, the headphones drive the mixer.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _lock = new();
    private MicInput? _mic;
    private WasapiOut? _mainOut;
    private WasapiOut? _monitorOut;
    private bool _stopping;

    public AudioEngine(VoiceEffectSettings voice, MixSettings mix)
    {
        Music = new MusicPlayer(MixerCore.SampleRate);
        Pads = new SoundPadPlayer(MixerCore.SampleRate);
        Core = new MixerCore(voice, mix, Music, Pads);
    }

    public MixerCore Core { get; }
    public MusicPlayer Music { get; }
    public SoundPadPlayer Pads { get; }

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusMessage { get; private set; } = "Arrêté";
    public string? MicName { get; private set; }
    public string? MainOutputName { get; private set; }
    public string? MonitorName { get; private set; }

    /// <summary>Raised (from any thread) when the state changes or a device fails.</summary>
    public event Action? StateChanged;

    public void Start(string? micId, string? virtualOutputId, string? monitorId)
    {
        lock (_lock)
        {
            StopUnlocked();
            _stopping = false;
            var warnings = new List<string>();

            try
            {
                if (monitorId != null && monitorId == virtualOutputId) monitorId = null;

                var micDevice = DeviceService.Open(micId);
                if (micDevice != null)
                {
                    try
                    {
                        _mic = new MicInput(micDevice);
                        _mic.Stopped += OnDeviceStopped;
                        Core.SetMicSource(_mic.CreateProvider(MixerCore.SampleRate));
                        _mic.Start();
                        MicName = _mic.DeviceName;
                    }
                    catch (Exception ex)
                    {
                        _mic?.Dispose();
                        _mic = null;
                        Core.SetMicSource(null);
                        warnings.Add("Micro indisponible : " + ex.Message);
                    }
                }
                else
                {
                    warnings.Add("Aucun micro sélectionné");
                }

                var mainDevice = DeviceService.Open(virtualOutputId);
                var monitorDevice = DeviceService.Open(monitorId);

                if (mainDevice != null)
                {
                    FloatRingBuffer? monitorRing = monitorDevice != null ? new FloatRingBuffer(MixerCore.SampleRate * 2) : null;
                    _mainOut = CreateOutput(mainDevice, new MainBusProvider(Core, monitorRing));
                    MainOutputName = mainDevice.FriendlyName;

                    if (monitorDevice != null)
                    {
                        _monitorOut = CreateOutput(monitorDevice,
                            new JitterBufferProvider(monitorRing!, MixerCore.SampleRate, 2, 30f, 120f));
                        MonitorName = monitorDevice.FriendlyName;
                    }
                }
                else if (monitorDevice != null)
                {
                    _monitorOut = CreateOutput(monitorDevice, new MonitorBusProvider(Core));
                    MonitorName = monitorDevice.FriendlyName;
                    warnings.Add("Pas de micro virtuel : seul vous entendez le son");
                }
                else
                {
                    throw new InvalidOperationException("Aucune sortie audio disponible.");
                }

                _mainOut?.Play();
                _monitorOut?.Play();

                State = EngineState.Running;
                StatusMessage = warnings.Count == 0 ? "En marche" : string.Join(" · ", warnings);
            }
            catch (Exception ex)
            {
                StopUnlocked();
                State = EngineState.Error;
                StatusMessage = "Erreur audio : " + ex.Message;
            }
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopUnlocked();
            State = EngineState.Stopped;
            StatusMessage = "Arrêté";
        }
        StateChanged?.Invoke();
    }

    private WasapiOut CreateOutput(MMDevice device, ISampleProvider provider)
    {
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        output.PlaybackStopped += (_, e) => OnDeviceStopped(e.Exception);
        output.Init(provider);
        return output;
    }

    private void OnDeviceStopped(Exception? ex)
    {
        if (_stopping || ex == null) return;
        lock (_lock)
        {
            if (_stopping) return;
            State = EngineState.Error;
            StatusMessage = "Périphérique perdu : " + ex.Message;
        }
        StateChanged?.Invoke();
    }

    private void StopUnlocked()
    {
        _stopping = true;
        Core.SetMicSource(null);
        DisposeQuietly(_mainOut);
        DisposeQuietly(_monitorOut);
        DisposeQuietly(_mic);
        _mainOut = null;
        _monitorOut = null;
        _mic = null;
        MicName = MainOutputName = MonitorName = null;
    }

    private static void DisposeQuietly(IDisposable? d)
    {
        try
        {
            if (d is IWavePlayer p) p.Stop();
            d?.Dispose();
        }
        catch
        {
            // Ignore teardown errors from vanished devices.
        }
    }

    public void Dispose()
    {
        Stop();
        Music.Dispose();
        Pads.Dispose();
    }

    /// <summary>Pulled by the virtual cable; also produces the monitor bus into a ring buffer.</summary>
    private sealed class MainBusProvider(MixerCore core, FloatRingBuffer? monitorRing) : ISampleProvider
    {
        private float[] _monitor = new float[4096];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MixerCore.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            try
            {
                if (monitorRing == null)
                {
                    core.Render(buffer.AsSpan(offset, frames * 2), Span<float>.Empty, frames);
                    return count;
                }

                if (_monitor.Length < frames * 2) _monitor = new float[frames * 2];
                core.Render(buffer.AsSpan(offset, frames * 2), _monitor.AsSpan(0, frames * 2), frames);
                monitorRing.Write(_monitor.AsSpan(0, frames * 2));
            }
            catch
            {
                // Never let a processing error stop the audio stream: output silence for this block.
                Array.Clear(buffer, offset, count);
            }
            return count;
        }
    }

    /// <summary>Used when no virtual cable exists: the headphones drive the mixer.</summary>
    private sealed class MonitorBusProvider(MixerCore core) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MixerCore.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            try
            {
                core.Render(Span<float>.Empty, buffer.AsSpan(offset, frames * 2), frames);
            }
            catch
            {
                Array.Clear(buffer, offset, count);
            }
            return count;
        }
    }
}
