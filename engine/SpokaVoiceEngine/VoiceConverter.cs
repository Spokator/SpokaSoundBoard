// SpokaVoiceEngine - real-time voice conversion for SpokaSoundBoard.
// Copyright (C) 2026 Spokator. Port of Seed-VC's real-time inference (https://github.com/Plachtaa/seed-vc).
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace SpokaVoiceEngine;

/// <summary>A target voice: style vector, prompt mel and prompt condition (see tools/make_voices.py).</summary>
public sealed class VoiceImprint
{
    public required float[] Style { get; init; }

    /// <summary>[80, Frames], mel bin first.</summary>
    public required float[] Mel { get; init; }

    /// <summary>[Frames, 384], frame first.</summary>
    public required float[] Prompt { get; init; }

    public int Frames { get; init; }

    public static VoiceImprint Load(string path, int maxFrames)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || bytes[0] != 'S' || bytes[1] != 'V' || bytes[2] != 'C' || bytes[3] != 'V')
            throw new InvalidDataException("Fichier de voix invalide.");
        var frames = BitConverter.ToInt32(bytes, 8);
        var floats = new float[(bytes.Length - 12) / 4];
        Buffer.BlockCopy(bytes, 12, floats, 0, floats.Length * 4);
        if (floats.Length != 192 + 80 * frames + 384 * frames) throw new InvalidDataException("Fichier de voix tronqué.");

        var keep = Math.Clamp(maxFrames, 1, frames);
        var mel = new float[80 * keep];
        for (var b = 0; b < 80; b++) Array.Copy(floats, 192 + b * frames, mel, b * keep, keep);
        var prompt = new float[384 * keep];
        Array.Copy(floats, 192 + 80 * frames, prompt, 0, prompt.Length);
        return new VoiceImprint { Style = floats[..192], Mel = mel, Prompt = prompt, Frames = keep };
    }
}

/// <summary>Engine settings sent by SpokaSoundBoard.</summary>
public sealed record ConverterOptions(int BlockMs = 300, int Steps = 4, double PromptSeconds = 2.0, double CfgRate = 0.7);

/// <summary>
/// Seed-VC (xlsr-tiny) streaming conversion, block by block: 16 kHz in, 22.05 kHz out. Same buffers as Seed-VC's
/// real-time GUI: 2.5 s of content context, 0.5 s of DiT context, 20 ms of look-ahead, SOLA cross-fade.
/// </summary>
public sealed class VoiceConverter : IDisposable
{
    public const int OutputRate = 22050;
    private const int Hop = 256;
    private const int Zc = OutputRate / 50; // 441: one content frame (20 ms) at the output rate
    private const int CeDitFrames = 100;    // content context beyond the DiT context: 2 s at 50 frames/s

    private readonly InferenceSession _content, _regulator, _dit, _f0, _hift;
    private readonly float[] _linearWeight;
    private readonly float _linearBias;
    private readonly int _harmonics;
    private readonly float[] _window = new float[16];

    // 16-point DFT twiddles: Cos[k * 16 + m] = cos(2π k m / 16), k = 0..8.
    private static readonly double[] Cos = Twiddles(Math.Cos);
    private static readonly double[] Sin = Twiddles(Math.Sin);

    private static double[] Twiddles(Func<double, double> fn)
    {
        var t = new double[9 * 16];
        for (var k = 0; k < 9; k++)
            for (var m = 0; m < 16; m++) t[k * 16 + m] = fn(2 * Math.PI * k * m / 16);
        return t;
    }
    private readonly Rng _rng;
    private readonly ConverterOptions _options;
    private readonly RunOptions _run = new();

    private float[] _input16 = [];
    private float[] _solaBuffer = [];
    private float[] _fadeIn = [];
    private int _solaLength, _solaSearch, _skipTail, _returnLength, _targetFrames;
    private VoiceImprint? _voice;
    private int _silentBlocks;

    public VoiceConverter(string modelDir, ConverterOptions options, int deviceId, ulong seed = 1234)
    {
        _options = options;
        _rng = new Rng(seed);
        _content = Open(modelDir, "content.onnx", deviceId);
        _regulator = Open(modelDir, "regulator.onnx", deviceId);
        _dit = Open(modelDir, "dit.onnx", deviceId);
        _f0 = Open(modelDir, "f0.onnx", deviceId);
        _hift = Open(modelDir, "hift.onnx", deviceId);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "source.json")));
        var root = json.RootElement;
        _linearWeight = root.GetProperty("linear_weight").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        _linearBias = (float)root.GetProperty("linear_bias").GetDouble();
        _harmonics = root.GetProperty("harmonics").GetInt32() + 1;
        for (var i = 0; i < 16; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 16)); // periodic Hann
        Configure(options.BlockMs);
    }

    /// <summary>Samples at 16 kHz expected per call of <see cref="Process"/>.</summary>
    public int Block16 { get; private set; }

    /// <summary>Samples at 22.05 kHz returned per call.</summary>
    public int BlockOut { get; private set; }

    public int BlockMs { get; private set; }

    private static InferenceSession Open(string dir, string name, int deviceId)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,                  // required by DirectML
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        options.AppendExecutionProvider_DML(deviceId);
        return new InferenceSession(Path.Combine(dir, name), options);
    }

    /// <summary>Block geometry for a block length (rounded to 20 ms); clears the stream state.</summary>
    public void Configure(int blockMs)
    {
        static int Round(double seconds) => (int)Math.Round(seconds * OutputRate / Zc, MidpointRounding.ToEven) * Zc;
        var block = Round(blockMs / 1000.0);
        BlockOut = block;
        BlockMs = block * 1000 / OutputRate;
        Block16 = 320 * block / Zc;
        var crossfade = Round(0.05);
        _solaLength = Math.Min(crossfade, 4 * Zc);
        _solaSearch = Zc;
        var extra = Round(2.5);
        var extraRight = Round(0.02);
        var skipHead = extra / Zc;
        _skipTail = extraRight / Zc;
        _returnLength = (block + _solaLength + _solaSearch) / Zc;
        _targetFrames = (int)Math.Floor((skipHead + _returnLength + _skipTail - CeDitFrames) / 50.0 * OutputRate / Hop);
        var total = extra + crossfade + _solaSearch + block + extraRight;
        _input16 = new float[320 * total / Zc];
        _solaBuffer = new float[_solaLength];
        _fadeIn = new float[_solaLength];
        for (var i = 0; i < _solaLength; i++)
        {
            var t = _solaLength == 1 ? 1.0 : i / (double)(_solaLength - 1);
            var s = Math.Sin(0.5 * Math.PI * t);
            _fadeIn[i] = (float)(s * s);
        }
        _silentBlocks = 0;
    }

    public void SetVoice(VoiceImprint voice) => _voice = voice;

    /// <summary>Forgets the audio heard so far (new session, other voice).</summary>
    public void Reset()
    {
        Array.Clear(_input16);
        Array.Clear(_solaBuffer);
        _silentBlocks = 0;
    }

    /// <summary>Converts one block. Returns <see cref="BlockOut"/> samples at 22.05 kHz.</summary>
    public float[] Process(ReadOnlySpan<float> block16, out bool silent)
    {
        if (block16.Length != Block16) throw new ArgumentException("bad block length");
        var voice = _voice ?? throw new InvalidOperationException("no voice");
        Array.Copy(_input16, Block16, _input16, 0, _input16.Length - Block16);
        block16.CopyTo(_input16.AsSpan(_input16.Length - Block16));

        // Nothing said for two blocks: no need to run the models (the GPU stays free for the game).
        var peak = 0f;
        foreach (var s in block16) peak = Math.Max(peak, Math.Abs(s));
        _silentBlocks = peak < 0.003f ? _silentBlocks + 1 : 0;
        silent = _silentBlocks >= 2;
        float[] infer;
        if (silent)
        {
            infer = new float[_returnLength * OutputRate / 50];
        }
        else
        {
            var wave = Convert(voice);
            var outLen = _returnLength * OutputRate / 50;
            var tail = _skipTail * OutputRate / 50;
            infer = wave[(wave.Length - outLen - tail)..(wave.Length - tail)];
        }
        return Sola(infer);
    }

    private float[] Convert(VoiceImprint v)
    {
        // Content features of the whole window, minus the part only used as context by the content encoder.
        float[] content;
        int contentFrames;
        using (var input = OrtValue.CreateTensorValueFromMemory(_input16, [1, _input16.Length]))
        using (var output = _content.Run(_run, ["wav"], [input], ["features"]))
        {
            var shape = output[0].GetTensorTypeAndShape().Shape;
            var all = output[0].GetTensorDataAsSpan<float>();
            contentFrames = (int)shape[1] - CeDitFrames;
            content = all[(CeDitFrames * 1024)..].ToArray();
        }

        // Nearest interpolation to the mel frame rate, then the length regulator.
        var t = _targetFrames;
        var interp = new float[t * 1024];
        for (var i = 0; i < t; i++)
        {
            var src = (int)Math.Floor(i * (contentFrames / (double)t));
            Array.Copy(content, src * 1024, interp, i * 1024, 1024);
        }
        float[] cond;
        using (var input = OrtValue.CreateTensorValueFromMemory(interp, [1, t, 1024]))
        using (var output = _regulator.Run(_run, ["s"], [input], ["cond"]))
            cond = output[0].GetTensorDataAsSpan<float>().ToArray();

        // Flow matching with classifier-free guidance: batch of 2 (with / without the condition).
        var p = v.Frames;
        var T = p + t;
        var x = new float[80 * T];
        for (var b = 0; b < 80; b++)
            for (var f = p; f < T; f++) x[b * T + f] = _rng.NextGaussian();

        var promptX = new float[2 * 80 * T];
        for (var b = 0; b < 80; b++) Array.Copy(v.Mel, b * p, promptX, b * T, p);
        var mu = new float[2 * T * 384];
        Array.Copy(v.Prompt, 0, mu, 0, p * 384);
        Array.Copy(cond, 0, mu, p * 384, t * 384);
        var style = new float[2 * 192];
        Array.Copy(v.Style, style, 192);
        var lens = new long[] { T, T };
        var xs = new float[2 * 80 * T];
        var ts = new float[2];
        var steps = _options.Steps;
        var cfg = (float)_options.CfgRate;

        using var promptValue = OrtValue.CreateTensorValueFromMemory(promptX, [2, 80, T]);
        using var muValue = OrtValue.CreateTensorValueFromMemory(mu, [2, T, 384]);
        using var styleValue = OrtValue.CreateTensorValueFromMemory(style, [2, 192]);
        using var lensValue = OrtValue.CreateTensorValueFromMemory(lens, [2]);
        for (var k = 1; k <= steps; k++)
        {
            var t0 = (k - 1) / (float)steps;
            var dt = 1f / steps;
            Array.Copy(x, 0, xs, 0, x.Length);
            Array.Copy(x, 0, xs, x.Length, x.Length);
            ts[0] = ts[1] = t0;
            using var xValue = OrtValue.CreateTensorValueFromMemory(xs, [2, 80, T]);
            using var tValue = OrtValue.CreateTensorValueFromMemory(ts, [2]);
            using var output = _dit.Run(_run, ["x", "prompt_x", "x_lens", "t", "style", "mu"],
                [xValue, promptValue, lensValue, tValue, styleValue, muValue], ["dphi"]);
            var d = output[0].GetTensorDataAsSpan<float>();
            var half = 80 * T;
            for (var i = 0; i < half; i++)
                x[i] += dt * ((1f + cfg) * d[i] - cfg * d[half + i]);
            for (var b = 0; b < 80; b++) Array.Clear(x, b * T, p);
        }

        var mel = new float[80 * t];
        for (var b = 0; b < 80; b++) Array.Copy(x, b * T + p, mel, b * t, t);
        return Vocode(mel, t);
    }

    // ---- HiFT vocoder: its neural parts run in ONNX, the signal processing here ---------------------------------

    private float[] Vocode(float[] mel, int frames)
    {
        float[] f0;
        using (var melValue = OrtValue.CreateTensorValueFromMemory(mel, [1, 80, frames]))
        using (var output = _f0.Run(_run, ["mel"], [melValue], ["f0"]))
            f0 = output[0].GetTensorDataAsSpan<float>().ToArray();

        var source = HarmonicSource(f0);
        var (sStft, stftFrames) = Stft(source);
        float[] h;
        int hFrames;
        using (var melValue = OrtValue.CreateTensorValueFromMemory(mel, [1, 80, frames]))
        using (var sValue = OrtValue.CreateTensorValueFromMemory(sStft, [1, 18, stftFrames]))
        using (var output = _hift.Run(_run, ["mel", "s_stft"], [melValue, sValue], ["x"]))
        {
            hFrames = (int)output[0].GetTensorTypeAndShape().Shape[2];
            h = output[0].GetTensorDataAsSpan<float>().ToArray();
        }
        return InverseStft(h, hFrames);
    }

    /// <summary>HiFT's neural source filter: 9 harmonics of the predicted F0, noise where unvoiced, Linear + tanh.</summary>
    private float[] HarmonicSource(float[] f0)
    {
        var n = f0.Length * Hop;
        var output = new float[n];
        var phase = new double[_harmonics];
        var offset = new double[_harmonics];
        for (var i = 1; i < _harmonics; i++) offset[i] = (_rng.NextDouble() * 2 - 1) * Math.PI;
        for (var s = 0; s < n; s++)
        {
            var f = f0[s / Hop];
            var voiced = f > 10f;
            var noiseAmp = voiced ? 0.003 : 0.1 / 3;
            double acc = _linearBias;
            for (var i = 0; i < _harmonics; i++)
            {
                phase[i] += f * (i + 1) / (double)OutputRate;
                phase[i] -= Math.Floor(phase[i]);
                var sine = voiced ? 0.1 * Math.Sin(2 * Math.PI * phase[i] + offset[i]) : 0.0;
                acc += _linearWeight[i] * (sine + noiseAmp * _rng.NextGaussian());
            }
            output[s] = (float)Math.Tanh(acc);
        }
        return output;
    }

    /// <summary>torch.stft(n_fft=16, hop=4, Hann, center=True, reflect): [1, 18, frames], real parts then imaginary.</summary>
    private (float[] Spec, int Frames) Stft(float[] x)
    {
        var padded = new float[x.Length + 16];
        for (var i = 0; i < padded.Length; i++)
        {
            var j = i - 8;
            if (j < 0) j = -j;
            else if (j >= x.Length) j = 2 * (x.Length - 1) - j;
            padded[i] = x[j];
        }
        var frames = 1 + (padded.Length - 16) / 4;
        var spec = new float[18 * frames];
        for (var f = 0; f < frames; f++)
        {
            for (var k = 0; k < 9; k++)
            {
                double re = 0, im = 0;
                for (var m = 0; m < 16; m++)
                {
                    var v = padded[4 * f + m] * _window[m];
                    re += v * Cos[k * 16 + m];
                    im -= v * Sin[k * 16 + m];
                }
                spec[k * frames + f] = (float)re;
                spec[(9 + k) * frames + f] = (float)im;
            }
        }
        return (spec, frames);
    }

    /// <summary>exp(magnitude) and sin(phase) as HiFT does, then torch.istft(n_fft=16, hop=4, Hann, center=True).</summary>
    private float[] InverseStft(float[] h, int frames)
    {
        var n = 16 + 4 * (frames - 1);
        var output = new double[n];
        var envelope = new double[n];
        var frame = new double[16];
        for (var f = 0; f < frames; f++)
        {
            Array.Clear(frame);
            for (var k = 0; k < 9; k++)
            {
                var mag = Math.Min(Math.Exp(h[k * frames + f]), 100.0);
                var ph = Math.Sin(h[(9 + k) * frames + f]);
                var re = mag * Math.Cos(ph);
                var im = mag * Math.Sin(ph);
                // Real inverse DFT of a one-sided spectrum (bins 0 and 8 count once, the others twice).
                var edge = k == 0 || k == 8;
                var weight = edge ? 1.0 : 2.0;
                for (var m = 0; m < 16; m++)
                    frame[m] += weight * (re * Cos[k * 16 + m] - (edge ? 0 : im * Sin[k * 16 + m]));
            }
            for (var m = 0; m < 16; m++)
            {
                output[4 * f + m] += frame[m] / 16 * _window[m];
                envelope[4 * f + m] += _window[m] * _window[m];
            }
        }
        var result = new float[n - 16];
        for (var i = 0; i < result.Length; i++)
        {
            var e = envelope[i + 8];
            var v = e > 1e-11 ? output[i + 8] / e : output[i + 8];
            result[i] = (float)Math.Clamp(v, -0.99, 0.99);
        }
        return result;
    }

    // ---- SOLA: joins consecutive blocks at the best-matching offset, with a cross-fade ------------------------

    private float[] Sola(float[] infer)
    {
        var n = _solaLength;
        var best = 0;
        var bestScore = double.NegativeInfinity;
        for (var o = 0; o <= _solaSearch; o++)
        {
            double nom = 0, den = 0;
            for (var i = 0; i < n; i++)
            {
                var v = infer[o + i];
                nom += v * _solaBuffer[i];
                den += v * v;
            }
            var score = nom / Math.Sqrt(den + 1e-8);
            if (score > bestScore)
            {
                bestScore = score;
                best = o;
            }
        }
        var block = BlockOut;
        var output = new float[block];
        for (var i = 0; i < block; i++)
        {
            var v = infer[best + i];
            if (i < n) v = v * _fadeIn[i] + _solaBuffer[i] * (1 - _fadeIn[i]);
            output[i] = v;
        }
        Array.Copy(infer, best + block, _solaBuffer, 0, n);
        return output;
    }

    public void Dispose()
    {
        _content.Dispose();
        _regulator.Dispose();
        _dit.Dispose();
        _f0.Dispose();
        _hift.Dispose();
        _run.Dispose();
    }

    /// <summary>xorshift128+ with Box-Muller: fast, allocation-free, good enough for diffusion noise.</summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _a = seed ^ 0x9E3779B97F4A7C15UL, _b = (seed * 0xBF58476D1CE4E5B9UL) | 1;
        private double _spare;
        private bool _hasSpare;

        private ulong Next()
        {
            var x = _a;
            var y = _b;
            _a = y;
            x ^= x << 23;
            _b = x ^ y ^ (x >> 17) ^ (y >> 26);
            return _b + y;
        }

        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

        public float NextGaussian()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return (float)_spare;
            }
            double u, v, s;
            do
            {
                u = NextDouble() * 2 - 1;
                v = NextDouble() * 2 - 1;
                s = u * u + v * v;
            } while (s >= 1 || s == 0);
            var m = Math.Sqrt(-2 * Math.Log(s) / s);
            _spare = v * m;
            _hasSpare = true;
            return (float)(u * m);
        }
    }
}
