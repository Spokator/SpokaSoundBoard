using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Audio;

public sealed record AudioDiagnostics(int MicDropouts, int DiscordDropouts, int HeadphoneDropouts, int VolumeCorrections, double DiscordClockRatio);

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
    private readonly JitterBufferProvider?[] _busBuffers = new JitterBufferProvider?[3];
    private JitterBufferProvider? _micBuffer;
    private int _volumeCorrections;
    private bool _stopping;
    private readonly Timer _guardTimer;
    private string[] _protectedDevices = [];

    public AudioEngine(VoiceEffectSettings voice, MixSettings mix)
    {
        Music = new MusicPlayer(MixerCore.SampleRate);
        Pads = new SoundPadPlayer(MixerCore.SampleRate);
        Preview = new MusicPlayer(MixerCore.SampleRate);
        PrivatePads = new SoundPadPlayer(MixerCore.SampleRate);
        Core = new MixerCore(voice, mix, Music, Pads, Preview, PrivatePads);
        _guardTimer = new Timer(_ => GuardSessions(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public MusicPlayer Preview { get; }

    /// <summary>Raised (from a worker thread) when another app turned down the music sent to the virtual mics.</summary>
    public event Action<int>? VolumeCorrected;

    private void GuardSessions()
    {
        var corrections = 0;
        foreach (var id in Volatile.Read(ref _protectedDevices))
            corrections += AudioSessionGuard.Protect(id);
        if (corrections > 0)
        {
            Interlocked.Add(ref _volumeCorrections, corrections);
            VolumeCorrected?.Invoke(corrections);
        }
    }

    public MixerCore Core { get; }
    public MusicPlayer Music { get; }
    public SoundPadPlayer Pads { get; }

    /// <summary>Soundboard sounds heard in the headphones only.</summary>
    public SoundPadPlayer PrivatePads { get; }

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
                        Core.SetMicSource(_mic.CreateProvider(MixerCore.SampleRate, out _micBuffer));
                        _mic.Start();
                        MicName = _mic.DeviceName;
                    }
                    catch (Exception ex)
                    {
                        _mic?.Dispose();
                        _mic = null;
                        Core.SetMicSource(null);
                        warnings.Add("Micro indisponible : " + FriendlyErrors.Describe(ex));
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
                    ISampleProvider provider;
                    if (i == master)
                    {
                        provider = new MasterBusProvider(Core, master, rings);
                    }
                    else
                    {
                        var jb = new JitterBufferProvider(rings[i]!, MixerCore.SampleRate, 2, 40f, 150f);
                        _busBuffers[i] = jb;
                        provider = jb;
                    }
                    _outputs[i] = CreateOutput(device, provider);
                }

                MainOutputName = devices[0]?.FriendlyName;
                DiscordOutputName = devices[1]?.FriendlyName;
                MonitorName = devices[2]?.FriendlyName;

                // Virtual cables must never be turned down by Windows / Discord ducking.
                var protectedIds = new List<string>();
                if (devices[0] != null) protectedIds.Add(devices[0]!.ID);
                if (devices[1] != null) protectedIds.Add(devices[1]!.ID);
                Volatile.Write(ref _protectedDevices, protectedIds.ToArray());

                // Ducking opt-out is best applied before the streams start.
                GuardSessions();
                foreach (var output in _outputs) output?.Play();
                GuardSessions();
                _guardTimer.Change(400, 400);

                State = EngineState.Running;
                StatusMessage = warnings.Count == 0 ? "En marche" : string.Join(" · ", warnings);
            }
            catch (Exception ex)
            {
                StopUnlocked();
                State = EngineState.Error;
                StatusMessage = "Erreur audio : " + FriendlyErrors.Describe(ex);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>Drop-out counters since the audio was (re)started, for the diagnostics panel.</summary>
    public AudioDiagnostics GetDiagnostics() => new(
        _micBuffer?.Underruns ?? 0,
        _busBuffers[1]?.Underruns ?? 0,
        _busBuffers[2]?.Underruns ?? 0,
        Volatile.Read(ref _volumeCorrections),
        _busBuffers[1]?.Ratio ?? 1.0);

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
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
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
            StatusMessage = "Périphérique perdu : " + FriendlyErrors.Describe(ex);
        }
        StateChanged?.Invoke();
    }

    private void StopUnlocked()
    {
        _stopping = true;
        _guardTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Volatile.Write(ref _protectedDevices, []);
        Core.SetMicSource(null);
        for (var i = 0; i < _outputs.Length; i++)
        {
            DisposeQuietly(_outputs[i]);
            _outputs[i] = null;
            _busBuffers[i] = null;
        }
        _micBuffer = null;
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
        _guardTimer.Dispose();
        Music.Dispose();
        Pads.Dispose();
        PrivatePads.Dispose();
        Preview.Dispose();
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
