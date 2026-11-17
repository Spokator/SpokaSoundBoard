// SpokaVoiceEngine - real-time voice conversion for SpokaSoundBoard.
// Copyright (C) 2026 Spokator.
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
// later version. This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public
// License for more details: https://www.gnu.org/licenses/gpl-3.0.html

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SpokaVoiceEngine;

/// <summary>
/// Protocol on standard input / output, little endian: every message is uint32 type, uint32 length, payload.
/// From SpokaSoundBoard: 1 Init (JSON), 2 Audio (float32 at 16 kHz), 3 Voice (JSON), 4 Reset, 5 Quit.
/// To SpokaSoundBoard: 101 Ready (JSON), 102 Audio (float32 at 22.05 kHz), 103 Stats (JSON), 104 Error (JSON).
/// Diagnostics go to standard error.
/// </summary>
public static class Program
{
    public const uint Init = 1, Audio = 2, Voice = 3, Reset = 4, Quit = 5;
    public const uint Ready = 101, AudioOut = 102, Stats = 103, Error = 104;

    // Block lengths tried in order when the GPU cannot keep up (reactive mode starts at 200 ms, stable at 300 ms).
    private static readonly int[] BlockSteps = [200, 250, 300, 400, 500, 600];

    public static int Main(string[] args)
    {
        // Messages are French: UTF-8 on stderr whatever the console code page (SpokaSoundBoard reads it as UTF-8).
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        // Ahead of the game on the graphics card (see Gpu.SetSchedulingPriority). SPOKA_VC_GPU_PRIORITY=2 for tests.
        var priority = int.TryParse(Environment.GetEnvironmentVariable("SPOKA_VC_GPU_PRIORITY"), out var p) ? p : Gpu.PriorityHigh;
        var status = Gpu.SetSchedulingPriority(priority);
        if (status != 0 && priority > Gpu.PriorityAboveNormal) status = Gpu.SetSchedulingPriority(Gpu.PriorityAboveNormal);
        if (status != 0) Console.Error.WriteLine($"Priorité graphique non modifiée (0x{status:x8}).");
        try
        {
            if (args.Length > 0 && args[0] == "--list-gpus")
            {
                foreach (var a in Gpu.List()) Console.WriteLine($"{a.Index}: {a.Name} ({a.DedicatedMemory / (1 << 20)} Mo){(a.IsUsable ? "" : " [logiciel]")}");
                return 0;
            }
            if (args.Length >= 6 && args[0] == "--imprint")
            {
                // --imprint <voice changer models> <voice creation models> <speech 22.05 kHz.wav> <speech 16 kHz.wav> <out.bin>
                var s22 = Wav.Read(args[3], out var r22);
                var s16 = Wav.Read(args[4], out var r16);
                if (r22 != 22050 || r16 != 16000) throw new InvalidDataException("Entrées attendues à 22,05 kHz et 16 kHz.");
                var result = ImprintBuilder.Build(args[1], args[2], s22, s16, args[5]);
                Console.WriteLine(JsonSerializer.Serialize(new ImprintResult(result.Frames, result.StyleWindows, Math.Round(result.SpeechSeconds, 1)),
                    EngineJson.Default.ImprintResult));
                return 0;
            }
            if (args.Length >= 5 && args[0] == "--convert") return ConvertFile(args[1], args[2], args[3], args[4], args.Length > 5 ? int.Parse(args[5]) : 300, args.Length > 6 ? ulong.Parse(args[6]) : 1234,
                args.Length > 7 ? double.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture) : 2.5,
                args.Length > 8 ? double.Parse(args[8], System.Globalization.CultureInfo.InvariantCulture) : 2.0,
                args.Length > 9 ? int.Parse(args[9]) : 4);
            return Serve();
        }
        catch (Exception ex)
        {
            // First line: what SpokaSoundBoard shows. Then the details for error.log.
            Console.Error.WriteLine(Describe(ex));
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>A French sentence for the user; the runtime missing is the one failure they can act on.</summary>
    internal static string Describe(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is DllNotFoundException or BadImageFormatException ||
                (e is TypeInitializationException t && t.TypeName?.Contains("OnnxRuntime", StringComparison.Ordinal) == true))
                return "ONNX Runtime n'a pas pu démarrer (fichiers du moteur incomplets ou runtime Visual C++ manquant) : réinstallez SpokaSoundBoard.";
        return ex.Message;
    }

    // ---- Offline conversion of a file (tests, diagnostics) ----------------------------------------------------

    private static int ConvertFile(string models, string voice, string input, string output, int blockMs, ulong seed, double contentContext, double promptSeconds, int steps)
    {
        var gpu = Gpu.Best() ?? throw new InvalidOperationException("Aucune carte graphique DirectX 12.");
        // Experiments (file mode only): SPOKA_VC_LOOKAHEAD, SPOKA_VC_CFG, SPOKA_VC_DITCTX in seconds / rate.
        static double Env(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var options = new ConverterOptions(BlockMs: blockMs, Steps: steps, PromptSeconds: promptSeconds, ContentContextSeconds: contentContext,
            CfgRate: Env("SPOKA_VC_CFG", 0.7), DitContextSeconds: Env("SPOKA_VC_DITCTX", 0.5), LookAheadSeconds: Env("SPOKA_VC_LOOKAHEAD", 0.02));
        using var converter = new VoiceConverter(models, options, gpu.Index, seed);
        converter.SetVoice(VoiceImprint.Load(voice, (int)(promptSeconds * VoiceConverter.OutputRate / 256)));
        var samples = Wav.Read(input, out var rate);
        if (rate != 16000) throw new InvalidDataException("Entrée attendue à 16 kHz.");
        // SPOKA_VC_SIZES=1: first one block at each length of BlockSteps, like the live start-up check (diagnostics).
        if (Environment.GetEnvironmentVariable("SPOKA_VC_SIZES") == "1")
        {
            foreach (var size in BlockSteps)
            {
                converter.Configure(size);
                converter.Process(new float[converter.Block16], out _);
            }
            converter.Configure(blockMs);
            converter.Reset();
        }
        var outSamples = new List<float>();
        var times = new List<double>();
        for (var pos = 0; pos + converter.Block16 <= samples.Length; pos += converter.Block16)
        {
            var sw = Stopwatch.StartNew();
            outSamples.AddRange(converter.Process(samples.AsSpan(pos, converter.Block16), out _));
            times.Add(sw.Elapsed.TotalMilliseconds);
            // SPOKA_VC_PACE=1: one block per block length, like a live microphone (diagnostics).
            if (Environment.GetEnvironmentVariable("SPOKA_VC_PACE") == "1" && sw.Elapsed.TotalMilliseconds < converter.BlockMs)
                Task.Delay(converter.BlockMs - (int)sw.Elapsed.TotalMilliseconds).Wait();
        }
        Wav.Write(output, outSamples.ToArray(), VoiceConverter.OutputRate);
        if (Environment.GetEnvironmentVariable("SPOKA_VC_PROFILE") == "1")
        {
            var profile = converter.TakeProfile();
            Console.Error.WriteLine(string.Join("  ", profile.Select(kv => $"{kv.Key} {kv.Value / Math.Max(1, times.Count):0.0}")));
        }
        var steady = times.Skip(3).DefaultIfEmpty(0).ToList();
        Console.WriteLine(JsonSerializer.Serialize(new ConvertResult(gpu.Name, converter.BlockMs, Math.Round(steady.Average(), 1),
            Math.Round(steady.Order().ElementAt((int)(steady.Count * 0.95)), 1), Math.Round(steady.Average() / converter.BlockMs, 3)),
            EngineJson.Default.ConvertResult));
        return 0;
    }

    // ---- Real-time service for SpokaSoundBoard ---------------------------------------------------------------

    /// <summary>Below this the microphone is silent (about -50 dBFS): nothing is lost when such audio is dropped.</summary>
    private const float SilencePeak16 = 0.003f;

    private static float Peak(List<float> samples, int count)
    {
        var peak = 0f;
        for (var i = 0; i < count && i < samples.Count; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
        return peak;
    }

    /// <summary>Fewest diffusion steps used when the GPU is busy (below, the voice gets noticeably rougher).</summary>
    private const int MinSteps = 2;

    private static int Serve()
    {
        var input = new BinaryReader(Console.OpenStandardInput());
        var output = new BinaryWriter(Console.OpenStandardOutput());
        VoiceConverter? converter = null;
        Task<VoiceConverter>? next = null;  // a converter for another block length, prepared in the background
        InitMessage? init = null;
        GpuAdapter? gpu = null;
        VoiceImprint? voice = null;
        var pending = new List<float>(48000);
        var recent = new Queue<double>();
        var blockIndex = 0;   // block length of the running converter (index in BlockSteps)
        var startIndex = 0;   // block length asked for
        var nextIndex = 0;    // block length of the one being prepared
        var sinceChange = 0;  // blocks since the last change (steps or block length)

        void Send(uint type, ReadOnlySpan<byte> payload)
        {
            output.Write(type);
            output.Write((uint)payload.Length);
            output.Write(payload);
            output.Flush();
        }

        void SendJson<T>(uint type, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
            Send(type, JsonSerializer.SerializeToUtf8Bytes(value, info));

        // A converter has one block length for its whole life: under GPU contention, sessions that have run several
        // shapes stay about three times slower (measured). Changing the length means fresh sessions.
        VoiceConverter Create(int index, int steps)
        {
            var c = new VoiceConverter(init!.Models, new ConverterOptions(BlockSteps[index], steps, init.PromptSeconds,
                ContentContextSeconds: init.ContentContextSeconds), gpu!.Index);
            c.SetVoice(voice!);
            return c;
        }

        // A few blocks of noise: the first run prepares the GPU kernels and is always slow; the speed is the best of
        // the others.
        static double WarmUp(VoiceConverter c, int runs)
        {
            var noise = new float[c.Block16];
            var rng = new Random(1);
            var best = double.MaxValue;
            for (var i = 0; i < runs; i++)
            {
                for (var j = 0; j < noise.Length; j++) noise[j] = (float)(rng.NextDouble() - 0.5) * 0.2f;
                var t = Stopwatch.StartNew();
                c.Process(noise, out _);
                if (i > 0) best = Math.Min(best, t.Elapsed.TotalMilliseconds);
            }
            c.Reset();
            return best;
        }

        Task<VoiceConverter> Prepare(int index, int steps) => Task.Run(() =>
        {
            var c = Create(index, steps);
            WarmUp(c, 3);
            return c;
        });

        void Changed()
        {
            recent.Clear();
            sinceChange = 0;
        }

        try
        {
            while (true)
            {
                var type = input.ReadUInt32();
                var length = input.ReadUInt32();
                var payload = input.ReadBytes((int)length);
                switch (type)
                {
                    case Init:
                    {
                        init = JsonSerializer.Deserialize(payload, EngineJson.Default.InitMessage)!;
                        gpu = Gpu.Best();
                        if (gpu == null)
                        {
                            SendJson(Error, new ErrorMessage(true, "no-gpu", "Aucune carte graphique compatible DirectX 12."), EngineJson.Default.ErrorMessage);
                            return 2;
                        }
                        var sw = Stopwatch.StartNew();
                        voice = VoiceImprint.Load(init.Voice, (int)(init.PromptSeconds * VoiceConverter.OutputRate / 256));
                        startIndex = blockIndex = Math.Max(0, Array.IndexOf(BlockSteps, init.BlockMs));
                        converter?.Dispose();
                        converter = Create(blockIndex, init.Steps);
                        var loadMs = sw.ElapsedMilliseconds;
                        var warm = WarmUp(converter, 4);
                        // Too slow right now (a game is running): fewer diffusion steps first (free), then a longer
                        // block. It comes back to what was asked once the GPU is free.
                        while (init.AutoBlock && warm > converter.BlockMs * 0.8)
                        {
                            if (converter.Steps > MinSteps)
                            {
                                converter.Steps--;
                            }
                            else
                            {
                                if (blockIndex >= BlockSteps.Length - 1) break;
                                converter.Dispose();
                                converter = Create(++blockIndex, MinSteps);
                            }
                            warm = WarmUp(converter, 3);
                        }
                        pending.Clear();
                        Changed();
                        SendJson(Ready, new ReadyMessage(gpu.Name, gpu.DedicatedMemory / (1 << 20), loadMs, converter.BlockMs, converter.Block16,
                            converter.BlockOut, VoiceConverter.OutputRate, Math.Round(warm, 1), warm > converter.BlockMs * 0.95), EngineJson.Default.ReadyMessage);
                        break;
                    }
                    case Voice:
                    {
                        if (converter == null || init == null) break;
                        var message = JsonSerializer.Deserialize(payload, EngineJson.Default.VoiceMessage)!;
                        voice = VoiceImprint.Load(message.Voice, (int)(init.PromptSeconds * VoiceConverter.OutputRate / 256));
                        converter.SetVoice(voice); // one being prepared takes it over in ContinueFrom
                        break;
                    }
                    case Reset:
                        converter?.Reset();
                        pending.Clear();
                        break;
                    case Audio:
                    {
                        if (converter == null) break;
                        // A converter for another block length is ready: it takes over the stream, seamlessly.
                        if (next is { IsCompleted: true } done)
                        {
                            next = null;
                            if (done.IsCompletedSuccessfully)
                            {
                                done.Result.ContinueFrom(converter);
                                converter.Dispose();
                                converter = done.Result;
                                blockIndex = nextIndex;
                                Changed();
                                SendJson(Stats, new StatsMessage(0, false, converter.BlockMs, 0, converter.Block16, converter.BlockOut, true, converter.Steps),
                                    EngineJson.Default.StatsMessage);
                            }
                            else
                            {
                                Console.Error.WriteLine(done.Exception?.GetBaseException().Message);
                            }
                        }

                        for (var i = 0; i + 4 <= payload.Length; i += 4) pending.Add(BitConverter.ToSingle(payload, i));
                        // Behind (the GPU was busy): catch up through silence first; speech is only dropped when
                        // hopelessly late, since fewer steps or a longer block (below) catch up without losing words.
                        while (pending.Count > converter.Block16 * 3 && Peak(pending, converter.Block16) < SilencePeak16)
                            pending.RemoveRange(0, converter.Block16);
                        var max = converter.Block16 * 6;
                        if (pending.Count > max) pending.RemoveRange(0, pending.Count - max);
                        while (pending.Count >= converter.Block16)
                        {
                            var block = pending.GetRange(0, converter.Block16).ToArray();
                            pending.RemoveRange(0, converter.Block16);
                            var sw = Stopwatch.StartNew();
                            var result = converter.Process(block, out var silent);
                            var ms = sw.Elapsed.TotalMilliseconds;
                            var bytes = new byte[result.Length * 4];
                            Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
                            Send(AudioOut, bytes);
                            if (!silent)
                            {
                                recent.Enqueue(ms);
                                if (recent.Count > 10) recent.Dequeue();
                            }
                            sinceChange++;
                            SendJson(Stats, new StatsMessage(Math.Round(ms, 1), silent, converter.BlockMs,
                                Math.Round(recent.DefaultIfEmpty(0).Average() / converter.BlockMs, 3), null, null, false, converter.Steps),
                                EngineJson.Default.StatsMessage);
                            if (!init!.AutoBlock || recent.Count < 10 || next != null) continue;
                            var average = recent.Average();
                            var behind = pending.Count >= converter.Block16;
                            if (average > converter.BlockMs * 0.85 || behind && average > converter.BlockMs * 0.6)
                            {
                                // Steadily too slow (a game took the GPU): fewer steps first, then longer blocks.
                                if (converter.Steps > MinSteps) converter.Steps--;
                                else if (blockIndex < BlockSteps.Length - 1) next = Prepare(nextIndex = blockIndex + 1, converter.Steps);
                                else continue;
                                Changed();
                            }
                            else if (sinceChange >= Math.Max(10, 6000 / converter.BlockMs) && recent.Max() < converter.BlockMs * 0.5)
                            {
                                // The GPU is free again (the game closed), for about 6 s: back to what was asked, steps
                                // first, then straight to the shortest block that fits (not one length at a time).
                                if (converter.Steps < init.Steps)
                                {
                                    converter.Steps++;
                                }
                                else
                                {
                                    var target = startIndex;
                                    while (target < blockIndex && recent.Max() >= BlockSteps[target] * 0.5) target++;
                                    if (target >= blockIndex) continue;
                                    next = Prepare(nextIndex = target, converter.Steps);
                                }
                                Changed();
                            }
                        }
                        break;
                    }
                    case Quit:
                        return 0;
                }
            }
        }
        catch (EndOfStreamException)
        {
            return 0; // SpokaSoundBoard closed the pipe
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Describe(ex));
            Console.Error.WriteLine(ex);
            try
            {
                SendJson(Error, new ErrorMessage(true, "engine", Describe(ex)), EngineJson.Default.ErrorMessage);
            }
            catch
            {
                // The pipe is gone too.
            }
            return 1;
        }
        finally
        {
            converter?.Dispose();
        }
    }
}

public sealed record InitMessage(string Models, string Voice, int BlockMs = 300, int Steps = 4, double PromptSeconds = 2.0, bool AutoBlock = true,
    double ContentContextSeconds = 2.5);

public sealed record VoiceMessage(string Voice);

public sealed record ReadyMessage(string Gpu, long VramMb, long LoadMs, int BlockMs, int Block16, int BlockOut, int OutputRate, double MsPerBlock, bool TooSlow);

public sealed record StatsMessage(double Ms, bool Silent, int BlockMs, double Load, int? Block16, int? BlockOut, bool Reconfigured, int? Steps = null);

public sealed record ErrorMessage(bool Fatal, string Code, string Message);

public sealed record ConvertResult(string Gpu, int BlockMs, double MsMean, double MsP95, double Rtf);

public sealed record ImprintResult(int Frames, int StyleWindows, double SpeechSeconds);

/// <summary>Source-generated JSON (camelCase, like the web): safe with trimming.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[System.Text.Json.Serialization.JsonSerializable(typeof(InitMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(VoiceMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReadyMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(StatsMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ErrorMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ConvertResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ImprintResult))]
public sealed partial class EngineJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Minimal mono WAV reader / writer (16-bit PCM or float32) for the file mode.</summary>
public static class Wav
{
    public static float[] Read(string path, out int rate)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadUInt32();
        r.ReadBytes(4);
        int channels = 1, bits = 16, format = 1;
        rate = 16000;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            var size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                var frames = size / (bits / 8) / channels;
                var data = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++)
                        sum += format == 3 ? r.ReadSingle() : r.ReadInt16() / 32768f;
                    data[i] = sum / channels;
                }
                return data;
            }
            else
            {
                r.ReadBytes(size);
            }
        }
        throw new InvalidDataException("no audio data");
    }

    public static void Write(string path, float[] samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples.Length * 4);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)3);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 4);
        w.Write((short)4);
        w.Write((short)32);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(samples.Length * 4);
        foreach (var s in samples) w.Write(s);
    }
}
