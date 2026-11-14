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

    private static readonly int[] BlockSteps = [300, 400, 500, 600];

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--list-gpus")
            {
                foreach (var a in Gpu.List()) Console.WriteLine($"{a.Index}: {a.Name} ({a.DedicatedMemory / (1 << 20)} Mo){(a.IsUsable ? "" : " [logiciel]")}");
                return 0;
            }
            if (args.Length >= 5 && args[0] == "--convert") return ConvertFile(args[1], args[2], args[3], args[4], args.Length > 5 ? int.Parse(args[5]) : 300, args.Length > 6 ? ulong.Parse(args[6]) : 1234);
            return Serve();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ---- Offline conversion of a file (tests, diagnostics) ----------------------------------------------------

    private static int ConvertFile(string models, string voice, string input, string output, int blockMs, ulong seed)
    {
        var gpu = Gpu.Best() ?? throw new InvalidOperationException("Aucune carte graphique DirectX 12.");
        using var converter = new VoiceConverter(models, new ConverterOptions(BlockMs: blockMs), gpu.Index, seed);
        converter.SetVoice(VoiceImprint.Load(voice, (int)(2.0 * VoiceConverter.OutputRate / 256)));
        var samples = Wav.Read(input, out var rate);
        if (rate != 16000) throw new InvalidDataException("Entrée attendue à 16 kHz.");
        var outSamples = new List<float>();
        var times = new List<double>();
        for (var pos = 0; pos + converter.Block16 <= samples.Length; pos += converter.Block16)
        {
            var sw = Stopwatch.StartNew();
            outSamples.AddRange(converter.Process(samples.AsSpan(pos, converter.Block16), out _));
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Wav.Write(output, outSamples.ToArray(), VoiceConverter.OutputRate);
        var steady = times.Skip(3).DefaultIfEmpty(0).ToList();
        Console.WriteLine(JsonSerializer.Serialize(new ConvertResult(gpu.Name, converter.BlockMs, Math.Round(steady.Average(), 1),
            Math.Round(steady.Order().ElementAt((int)(steady.Count * 0.95)), 1), Math.Round(steady.Average() / converter.BlockMs, 3)),
            EngineJson.Default.ConvertResult));
        return 0;
    }

    // ---- Real-time service for SpokaSoundBoard ---------------------------------------------------------------

    private static int Serve()
    {
        var input = new BinaryReader(Console.OpenStandardInput());
        var output = new BinaryWriter(Console.OpenStandardOutput());
        VoiceConverter? converter = null;
        InitMessage? init = null;
        var pending = new List<float>(48000);
        var recent = new Queue<double>();
        var blockIndex = 0;

        void Send(uint type, ReadOnlySpan<byte> payload)
        {
            output.Write(type);
            output.Write((uint)payload.Length);
            output.Write(payload);
            output.Flush();
        }

        void SendJson<T>(uint type, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
            Send(type, JsonSerializer.SerializeToUtf8Bytes(value, info));

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
                        var gpu = Gpu.Best();
                        if (gpu == null)
                        {
                            SendJson(Error, new ErrorMessage(true, "no-gpu", "Aucune carte graphique compatible DirectX 12."), EngineJson.Default.ErrorMessage);
                            return 2;
                        }
                        var sw = Stopwatch.StartNew();
                        blockIndex = Math.Max(0, Array.IndexOf(BlockSteps, init.BlockMs));
                        converter?.Dispose();
                        converter = new VoiceConverter(init.Models, new ConverterOptions(BlockSteps[blockIndex], init.Steps, init.PromptSeconds), gpu.Index);
                        converter.SetVoice(VoiceImprint.Load(init.Voice, (int)(init.PromptSeconds * VoiceConverter.OutputRate / 256)));
                        var loadMs = sw.ElapsedMilliseconds;

                        // Warm-up on noise: the first runs compile the GPU kernels; also measures the real speed.
                        var noise = new float[converter.Block16];
                        var rng = new Random(1);
                        double warm = 0;
                        for (var i = 0; i < 4; i++)
                        {
                            for (var j = 0; j < noise.Length; j++) noise[j] = (float)(rng.NextDouble() - 0.5) * 0.2f;
                            var t = Stopwatch.StartNew();
                            converter.Process(noise, out _);
                            warm = t.Elapsed.TotalMilliseconds;
                        }
                        // Too slow for this block length: take a longer one before starting.
                        while (init.AutoBlock && warm > converter.BlockMs * 0.8 && blockIndex < BlockSteps.Length - 1)
                        {
                            converter.Configure(BlockSteps[++blockIndex]);
                            var t = Stopwatch.StartNew();
                            converter.Process(new float[converter.Block16].Select((_, j) => (float)Math.Sin(j * 0.05) * 0.1f).ToArray(), out _);
                            warm = t.Elapsed.TotalMilliseconds;
                        }
                        converter.Reset();
                        pending.Clear();
                        recent.Clear();
                        SendJson(Ready, new ReadyMessage(gpu.Name, gpu.DedicatedMemory / (1 << 20), loadMs, converter.BlockMs, converter.Block16,
                            converter.BlockOut, VoiceConverter.OutputRate, Math.Round(warm, 1), warm > converter.BlockMs * 0.95), EngineJson.Default.ReadyMessage);
                        break;
                    }
                    case Voice:
                    {
                        if (converter == null || init == null) break;
                        var message = JsonSerializer.Deserialize(payload, EngineJson.Default.VoiceMessage)!;
                        converter.SetVoice(VoiceImprint.Load(message.Voice, (int)(init.PromptSeconds * VoiceConverter.OutputRate / 256)));
                        break;
                    }
                    case Reset:
                        converter?.Reset();
                        pending.Clear();
                        break;
                    case Audio:
                    {
                        if (converter == null) break;
                        for (var i = 0; i + 4 <= payload.Length; i += 4) pending.Add(BitConverter.ToSingle(payload, i));
                        // Too far behind (GPU busy): drop the oldest audio rather than pile up delay.
                        var max = converter.Block16 * 3;
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
                            SendJson(Stats, new StatsMessage(Math.Round(ms, 1), silent, converter.BlockMs,
                                Math.Round(recent.DefaultIfEmpty(0).Average() / converter.BlockMs, 3), null, null, false), EngineJson.Default.StatsMessage);
                            // Steadily too slow (a game took the GPU): longer blocks, a bit more delay but no gaps.
                            if (init!.AutoBlock && recent.Count == 10 && recent.Average() > converter.BlockMs * 0.85 && blockIndex < BlockSteps.Length - 1)
                            {
                                converter.Configure(BlockSteps[++blockIndex]);
                                recent.Clear();
                                pending.Clear();
                                SendJson(Stats, new StatsMessage(0, false, converter.BlockMs, 0, converter.Block16, converter.BlockOut, true), EngineJson.Default.StatsMessage);
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
            Console.Error.WriteLine(ex);
            try
            {
                SendJson(Error, new ErrorMessage(true, "engine", ex.Message), EngineJson.Default.ErrorMessage);
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

public sealed record InitMessage(string Models, string Voice, int BlockMs = 300, int Steps = 4, double PromptSeconds = 2.0, bool AutoBlock = true);

public sealed record VoiceMessage(string Voice);

public sealed record ReadyMessage(string Gpu, long VramMb, long LoadMs, int BlockMs, int Block16, int BlockOut, int OutputRate, double MsPerBlock, bool TooSlow);

public sealed record StatsMessage(double Ms, bool Silent, int BlockMs, double Load, int? Block16, int? BlockOut, bool Reconfigured);

public sealed record ErrorMessage(bool Fatal, string Code, string Message);

public sealed record ConvertResult(string Gpu, int BlockMs, double MsMean, double MsP95, double Rtf);

/// <summary>Source-generated JSON (camelCase, like the web): safe with trimming.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[System.Text.Json.Serialization.JsonSerializable(typeof(InitMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(VoiceMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ReadyMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(StatsMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ErrorMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ConvertResult))]
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
