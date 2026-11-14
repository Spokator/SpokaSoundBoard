using System.Diagnostics;
using System.Text.Json;
using SpokaSoundBoard.Core.Dsp;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>Settings of the conversion: block length, diffusion steps, contexts, playback margin.</summary>
public sealed record VoiceChangerProfile(int BlockMs, int Steps, double PromptSeconds, double ContentContextSeconds, int MarginMs)
{
    /// <summary>Measured on real French speech: same intelligibility as <see cref="Stable"/>, timbre a hair less close.</summary>
    public static readonly VoiceChangerProfile Reactive = new(200, 3, 1.5, 1.5, 120);

    public static readonly VoiceChangerProfile Stable = new(300, 4, 2.0, 2.5, 150);

    public static VoiceChangerProfile For(Models.VoiceChangerMode mode) => mode == Models.VoiceChangerMode.Stable ? Stable : Reactive;
}

/// <summary>The engine is loaded: which graphics card, and how fast it converts.</summary>
public sealed record VoiceChangerReady(string Gpu, int BlockMs, double MsPerBlock, bool TooSlow);

/// <summary>Speed of the last converted block.</summary>
public sealed record VoiceChangerStats(double Ms, double Load, int BlockMs, bool Silent, bool Reconfigured);

/// <summary>A problem reported by the engine (or detected here). Fatal: the voice changer stopped.</summary>
public sealed record VoiceChangerError(bool Fatal, string Code, string Message);

/// <summary>
/// Runs the AI voice changer: starts SpokaVoiceEngine (a separate GPL-3.0 program), sends it the microphone at
/// 16 kHz through its standard input and plays back what it returns at 22.05 kHz, resampled to 48 kHz, through
/// <see cref="Link"/>. Two background threads; the audio thread only touches the link's ring buffers.
/// </summary>
public sealed class VoiceChangerSession : IDisposable
{
    // Protocol, see engine/README.md: uint32 type, uint32 length, payload (little endian).
    internal const uint Init = 1, Audio = 2, Voice = 3, Reset = 4, Quit = 5;
    internal const uint ReadyMessage = 101, AudioOut = 102, Stats = 103, Error = 104;

    /// <summary>Safety margin played on top of one block, against the jitter of the GPU.</summary>
    public int MarginMs { get; private set; } = VoiceChangerProfile.Reactive.MarginMs;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _enginePath;
    private readonly string _modelDirectory;
    private readonly object _writeLock = new();
    private Process? _process;
    private Stream? _stdin;
    private Thread? _feeder, _reader;
    private volatile bool _stopping;
    private TaskCompletionSource<VoiceChangerReady>? _ready;

    public VoiceChangerSession(string enginePath, string modelDirectory)
    {
        _enginePath = enginePath;
        _modelDirectory = modelDirectory;
    }

    public VoiceChangerLink Link { get; } = new();

    public bool IsRunning => _process is { HasExited: false } && !_stopping;

    /// <summary>Last known block length (it grows when the GPU is busy).</summary>
    public int BlockMs { get; private set; }

    public event Action<VoiceChangerStats>? StatsReceived;
    public event Action<VoiceChangerError>? Failed;

    /// <summary>Total delay between the microphone and the converted voice, about.</summary>
    public int EstimatedDelayMs(double msPerBlock) => BlockMs + (int)msPerBlock + MarginMs;

    /// <summary>Starts the engine, loads the models on the GPU and waits until it is ready (up to 2 minutes).</summary>
    public async Task<VoiceChangerReady> StartAsync(string voiceFile, CancellationToken ct = default) =>
        await StartAsync(voiceFile, VoiceChangerProfile.Reactive, ct).ConfigureAwait(false);

    public async Task<VoiceChangerReady> StartAsync(string voiceFile, VoiceChangerProfile profile, CancellationToken ct = default)
    {
        MarginMs = profile.MarginMs;
        if (!File.Exists(_enginePath)) throw new FileNotFoundException("Le moteur du changeur de voix est introuvable.", _enginePath);
        _stopping = false;
        Link.Reset();
        var psi = new ProcessStartInfo(_enginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_enginePath)!,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Le moteur du changeur de voix n'a pas démarré.");
        try
        {
            _process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
            // Not allowed: normal priority is fine.
        }
        _stdin = _process.StandardInput.BaseStream;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) LastEngineLog = e.Data;
        };
        _process.BeginErrorReadLine();

        _ready = new TaskCompletionSource<VoiceChangerReady>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SpokaSoundBoard voice changer (receive)" };
        _reader.Start();

        SendJson(Init, new
        {
            models = _modelDirectory, voice = voiceFile, blockMs = profile.BlockMs, steps = profile.Steps,
            promptSeconds = profile.PromptSeconds, contentContextSeconds = profile.ContentContextSeconds, autoBlock = true,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using (timeout.Token.Register(() => _ready.TrySetException(new TimeoutException("Le moteur du changeur de voix ne répond pas."))))
        {
            var ready = await _ready.Task.ConfigureAwait(false);
            _feeder = new Thread(FeedLoop) { IsBackground = true, Name = "SpokaSoundBoard voice changer (send)" };
            _feeder.Start();
            return ready;
        }
    }

    /// <summary>Last line the engine wrote to its error output (for the error log).</summary>
    public string? LastEngineLog { get; private set; }

    public void SetVoice(string voiceFile)
    {
        SendJson(Voice, new { voice = voiceFile });
    }

    // ---- Threads ----------------------------------------------------------------------------------------------

    private void FeedLoop()
    {
        var decimator = new Decimator();
        var block = new float[960];      // 20 ms at 48 kHz
        var out16 = new List<float>(4096);
        try
        {
            while (!_stopping)
            {
                var n = Link.Input.Read(block);
                if (n == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }
                decimator.Process(block.AsSpan(0, n), out16);
                if (out16.Count < 1600) continue; // send every 100 ms
                var bytes = new byte[out16.Count * 4];
                for (var i = 0; i < out16.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), out16[i]);
                out16.Clear();
                Send(Audio, bytes);
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Fail(new VoiceChangerError(true, "pipe", "Le lien avec le moteur du changeur de voix est rompu : " + ex.Message));
        }
        catch
        {
            // Stopping.
        }
    }

    private void ReadLoop()
    {
        var resampler = new StreamResampler(22050, MixerCore.SampleRate);
        var resampled = new List<float>(16384);
        try
        {
            var stdout = _process!.StandardOutput.BaseStream;
            var header = new byte[8];
            while (!_stopping)
            {
                if (!ReadExactly(stdout, header)) break;
                var type = BitConverter.ToUInt32(header, 0);
                var length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 64 << 20) throw new InvalidDataException("message trop long");
                var payload = new byte[length];
                if (!ReadExactly(stdout, payload)) break;
                switch (type)
                {
                    case ReadyMessage:
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var r = doc.RootElement;
                        BlockMs = r.GetProperty("blockMs").GetInt32();
                        Link.PrimeSamples = (BlockMs + MarginMs) * MixerCore.SampleRate / 1000;
                        _ready?.TrySetResult(new VoiceChangerReady(r.GetProperty("gpu").GetString() ?? "", BlockMs,
                            r.GetProperty("msPerBlock").GetDouble(), r.GetProperty("tooSlow").GetBoolean()));
                        break;
                    }
                    case AudioOut:
                    {
                        var samples = new float[length / 4];
                        Buffer.BlockCopy(payload, 0, samples, 0, samples.Length * 4);
                        resampled.Clear();
                        resampler.Process(samples, resampled);
                        Link.Output.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(resampled));
                        break;
                    }
                    case Stats:
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var r = doc.RootElement;
                        var reconfigured = r.TryGetProperty("reconfigured", out var re) && re.GetBoolean();
                        if (r.TryGetProperty("blockMs", out var b)) BlockMs = b.GetInt32();
                        if (reconfigured) Link.PrimeSamples = (BlockMs + MarginMs) * MixerCore.SampleRate / 1000;
                        StatsReceived?.Invoke(new VoiceChangerStats(
                            r.TryGetProperty("ms", out var ms) ? ms.GetDouble() : 0,
                            r.TryGetProperty("load", out var load) ? load.GetDouble() : 0,
                            BlockMs, r.TryGetProperty("silent", out var s) && s.GetBoolean(), reconfigured));
                        break;
                    }
                    case Error:
                    {
                        using var doc = JsonDocument.Parse(payload);
                        var r = doc.RootElement;
                        Fail(new VoiceChangerError(r.TryGetProperty("fatal", out var f) && f.GetBoolean(),
                            r.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                            r.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""));
                        break;
                    }
                }
            }
            if (!_stopping) Fail(new VoiceChangerError(true, "exited", "Le moteur du changeur de voix s'est arrêté. " + (LastEngineLog ?? "")));
        }
        catch (Exception ex) when (!_stopping)
        {
            Fail(new VoiceChangerError(true, "pipe", "Le lien avec le moteur du changeur de voix est rompu : " + ex.Message));
        }
        catch
        {
            // Stopping.
        }
    }

    private void Fail(VoiceChangerError error)
    {
        if (_ready is { Task.IsCompleted: false } && error.Fatal)
        {
            _ready.TrySetException(new VoiceChangerException(error));
            return;
        }
        Failed?.Invoke(error);
    }

    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private void Send(uint type, ReadOnlySpan<byte> payload)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("not started");
        lock (_writeLock)
        {
            Span<byte> header = stackalloc byte[8];
            BitConverter.TryWriteBytes(header, type);
            BitConverter.TryWriteBytes(header[4..], payload.Length);
            stdin.Write(header);
            stdin.Write(payload);
            stdin.Flush();
        }
    }

    private void SendJson(uint type, object value) => Send(type, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public void Stop()
    {
        if (_process == null) return;
        _stopping = true;
        try
        {
            Send(Quit, []);
        }
        catch
        {
            // Already gone.
        }
        try
        {
            if (!_process.WaitForExit(3000)) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
        _feeder?.Join(1000);
        _reader?.Join(1000);
        _process.Dispose();
        _process = null;
        _stdin = null;
        Link.Reset();
    }

    public void Dispose() => Stop();
}

/// <summary>The voice changer could not start (no GPU, models, engine…), with the engine's explanation.</summary>
public sealed class VoiceChangerException(VoiceChangerError error) : Exception(error.Message)
{
    public VoiceChangerError Error { get; } = error;
}
