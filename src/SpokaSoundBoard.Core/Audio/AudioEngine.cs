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
    private readonly WasapiOut?[] _outputs = new WasapiOut?[3];
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
    public string? DiscordOutputName { get; private set; }
    public string? MonitorName { get; private set; }

    /// <summary>Raised (from any thread) when the state changes or a device fails.</summary>
    public event Action? StateChanged;

    public void Start(string? micId, string? virtualOutputId, string? monitorId) =>
        Start(micId, virtualOutputId, null, monitorId);

    /// <param name="gameOutputId">Main virtual cable (game microphone).</param>
    /// <param name="discordOutputId">Optional second virtual cable (Discord microphone).</param>
    /// <param name="monitorId">Headphones.</param>
    public void Start(string? micId, string? gameOutputId, string? discordOutputId, string? monitorId)
    {
        lock (_lock)
        {
            StopUnlocked();
            _stopping = false;
            var warnings = new List<string>();

            try
            {
                if (discordOutputId == gameOutputId) discordOutputId = null;
                if (monitorId == gameOutputId || monitorId == discordOutputId) monitorId = null;

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

                // Bus order: game, Discord, headphones. The first available device is the master clock;
                // the others are fed through jitter buffers.
                var devices = new[] { DeviceService.Open(gameOutputId), DeviceService.Open(discordOutputId), DeviceService.Open(monitorId) };
                var master = Array.FindIndex(devices, d => d != null);
                if (master < 0) throw new InvalidOperationException("Aucune sortie audio disponible.");
                if (devices[0] == null) warnings.Add("Pas de micro virtuel pour le jeu : seul vous entendez le son");

                var rings = new FloatRingBuffer?[3];
                for (var i = 0; i < 3; i++)
                    if (i != master && devices[i] != null)
                        rings[i] = new FloatRingBuffer(MixerCore.SampleRate * 2);

                for (var i = 0; i < 3; i++)
                {
                    var device = devices[i];
                    if (device == null) continue;
                    ISampleProvider provider = i == master
                        ? new MasterBusProvider(Core, master, rings)
                        : new JitterBufferProvider(rings[i]!, MixerCore.SampleRate, 2, 30f, 120f);
                    _outputs[i] = CreateOutput(device, provider);
                }

                MainOutputName = devices[0]?.FriendlyName;
                DiscordOutputName = devices[1]?.FriendlyName;
                MonitorName = devices[2]?.FriendlyName;

                foreach (var output in _outputs) output?.Play();

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
        for (var i = 0; i < _outputs.Length; i++)
        {
            DisposeQuietly(_outputs[i]);
            _outputs[i] = null;
        }
        DisposeQuietly(_mic);
        _mic = null;
        MicName = MainOutputName = DiscordOutputName = MonitorName = null;
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

    /// <summary>
    /// Pulled by the master device. Renders all buses in one pass, writes its own bus to the device
    /// and the other buses into their ring buffers.
    /// </summary>
    private sealed class MasterBusProvider(MixerCore core, int master, FloatRingBuffer?[] rings) : ISampleProvider
    {
        private readonly float[][] _temp = [new float[4096], new float[4096], new float[4096]];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(MixerCore.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            try
            {
                for (var i = 0; i < 3; i++)
                    if (_temp[i].Length < frames * 2)
                        _temp[i] = new float[frames * 2];

                Span<float> Bus(int i) => i == master
                    ? buffer.AsSpan(offset, frames * 2)
                    : rings[i] != null ? _temp[i].AsSpan(0, frames * 2) : Span<float>.Empty;

                core.Render(Bus(0), Bus(1), Bus(2), frames);

                for (var i = 0; i < 3; i++)
                    if (i != master && rings[i] != null)
                        rings[i]!.Write(_temp[i].AsSpan(0, frames * 2));
            }
            catch
            {
                // Never let a processing error stop the audio stream: output silence for this block.
                Array.Clear(buffer, offset, count);
            }
            return count;
        }
    }
}
