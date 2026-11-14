// SpokaVoiceEngine - real-time voice conversion for SpokaSoundBoard.
// Copyright (C) 2026 Spokator. Port of Seed-VC's reference processing (https://github.com/Plachtaa/seed-vc).
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.ML.OnnxRuntime;

namespace SpokaVoiceEngine;

/// <summary>
/// Turns recordings of a voice into a voice imprint (same file format as the built-in voices): the timbre (CAMPPlus,
/// averaged over 3 s windows of all the speech), the prompt mel spectrogram and the prompt condition (first 3 s).
/// Runs once per new voice, on the CPU.
/// </summary>
public static class ImprintBuilder
{
    private const int PromptSeconds = 3;
    private const int Rate22 = 22050, Rate16 = 16000;
    private const int StyleWindow = 48000; // CAMPPlus is exported for 3 s at 16 kHz (298 fbank frames)
    private const int StyleFrames = 1 + (StyleWindow - 400) / 160;

    public sealed record Result(int Frames, int StyleWindows, double SpeechSeconds);

    /// <param name="seedVcDir">Folder of the voice changer models (content.onnx, regulator.onnx).</param>
    /// <param name="creatorDir">Folder of the voice creation models (campplus.onnx, fbank.bin, melbasis.bin).</param>
    /// <param name="speech22">Speech only, mono 22.05 kHz, the best 3 s first.</param>
    /// <param name="speech16">The same speech at 16 kHz.</param>
    public static Result Build(string seedVcDir, string creatorDir, float[] speech22, float[] speech16, string output)
    {
        if (speech16.Length < StyleWindow) throw new InvalidDataException("Il faut au moins 3 secondes de voix.");
        var cpu = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };

        // Timbre: average of CAMPPlus over 3 s windows (half overlapping) of all the speech.
        var banks = ReadFloats(Path.Combine(creatorDir, "fbank.bin"));
        var style = new float[192];
        var windows = 0;
        using (var campplus = new InferenceSession(Path.Combine(creatorDir, "campplus.onnx"), cpu))
        {
            for (var start = 0; start + StyleWindow <= speech16.Length && windows < 20; start += StyleWindow / 2)
            {
                var fbank = KaldiFbank(speech16.AsSpan(start, StyleWindow), banks);
                using var input = OrtValue.CreateTensorValueFromMemory(fbank, [1, StyleFrames, 80]);
                using var output2 = campplus.Run(new RunOptions(), ["fbank"], [input], ["style"]);
                var s = output2[0].GetTensorDataAsSpan<float>();
                for (var i = 0; i < 192; i++) style[i] += s[i];
                windows++;
            }
        }
        for (var i = 0; i < 192; i++) style[i] /= windows;

        // Prompt: the first 3 s, as mel spectrogram and as content condition.
        var prompt22 = speech22.AsSpan(0, Math.Min(speech22.Length, PromptSeconds * Rate22)).ToArray();
        var prompt16 = speech16.AsSpan(0, Math.Min(speech16.Length, PromptSeconds * Rate16)).ToArray();
        var melBasis = ReadFloats(Path.Combine(creatorDir, "melbasis.bin"));
        var (mel, frames) = MelSpectrogram(prompt22, melBasis);

        float[] content;
        int contentFrames;
        using (var session = new InferenceSession(Path.Combine(seedVcDir, "content.onnx"), cpu))
        using (var input = OrtValue.CreateTensorValueFromMemory(prompt16, [1, prompt16.Length]))
        using (var output2 = session.Run(new RunOptions(), ["wav"], [input], ["features"]))
        {
            contentFrames = (int)output2[0].GetTensorTypeAndShape().Shape[1];
            content = output2[0].GetTensorDataAsSpan<float>().ToArray();
        }
        var interp = new float[frames * 1024];
        for (var i = 0; i < frames; i++)
        {
            var src = (int)Math.Floor(i * (contentFrames / (double)frames));
            Array.Copy(content, src * 1024, interp, i * 1024, 1024);
        }
        float[] prompt;
        using (var session = new InferenceSession(Path.Combine(seedVcDir, "regulator.onnx"), cpu))
        using (var input = OrtValue.CreateTensorValueFromMemory(interp, [1, frames, 1024]))
        using (var output2 = session.Run(new RunOptions(), ["s"], [input], ["cond"]))
            prompt = output2[0].GetTensorDataAsSpan<float>().ToArray();

        using (var w = new BinaryWriter(File.Create(output)))
        {
            w.Write("SVCV"u8);
            w.Write(1);
            w.Write(frames);
            foreach (var v in style) w.Write(v);
            foreach (var v in mel) w.Write(v);
            foreach (var v in prompt) w.Write(v);
        }
        return new Result(frames, windows, speech16.Length / (double)Rate16);
    }

    private static float[] ReadFloats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
        return floats;
    }

    /// <summary>
    /// torchaudio.compliance.kaldi.fbank(num_mel_bins=80, dither=0, 16 kHz), then mean normalization is done by the
    /// CAMPPlus graph: 25 ms frames every 10 ms, DC removal, pre-emphasis 0.97, Povey window, 512-point power
    /// spectrum, Kaldi mel banks [80, 257], log with float epsilon. Returns [frames, 80].
    /// </summary>
    public static float[] KaldiFbank(ReadOnlySpan<float> wav, float[] banks)
    {
        const int size = 400, shift = 160, nfft = 512, bins = 257;
        var frames = 1 + (wav.Length - size) / shift;
        var window = new double[size];
        for (var n = 0; n < size; n++) window[n] = Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (size - 1)), 0.85);
        var result = new float[frames * 80];
        var re = new double[nfft];
        var im = new double[nfft];
        var power = new double[bins];
        var frame = new double[size];
        for (var f = 0; f < frames; f++)
        {
            double mean = 0;
            for (var n = 0; n < size; n++) mean += frame[n] = wav[f * shift + n];
            mean /= size;
            for (var n = 0; n < size; n++) frame[n] -= mean;
            for (var n = size - 1; n > 0; n--) frame[n] -= 0.97 * frame[n - 1];
            frame[0] -= 0.97 * frame[0];
            Array.Clear(re);
            Array.Clear(im);
            for (var n = 0; n < size; n++) re[n] = frame[n] * window[n];
            Fft(re, im);
            for (var k = 0; k < bins; k++) power[k] = re[k] * re[k] + im[k] * im[k];
            for (var m = 0; m < 80; m++)
            {
                double e = 0;
                for (var k = 0; k < bins; k++) e += banks[m * bins + k] * power[k];
                result[f * 80 + m] = (float)Math.Log(Math.Max(e, 1.1920928955078125e-07));
            }
        }
        return result;
    }

    /// <summary>Seed-VC's mel_spectrogram at 22.05 kHz: reflect padding, n_fft 1024, hop 256, Hann, log magnitude. [80, frames].</summary>
    public static (float[] Mel, int Frames) MelSpectrogram(float[] y, float[] basis)
    {
        const int nfft = 1024, hop = 256, bins = nfft / 2 + 1, pad = (nfft - hop) / 2;
        var padded = new float[y.Length + 2 * pad];
        for (var i = 0; i < padded.Length; i++)
        {
            var j = i - pad;
            if (j < 0) j = -j;
            else if (j >= y.Length) j = 2 * (y.Length - 1) - j;
            padded[i] = y[j];
        }
        var frames = 1 + (padded.Length - nfft) / hop;
        var window = new double[nfft];
        for (var n = 0; n < nfft; n++) window[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / nfft); // periodic Hann
        var mel = new float[80 * frames];
        var re = new double[nfft];
        var im = new double[nfft];
        var mag = new double[bins];
        for (var f = 0; f < frames; f++)
        {
            for (var n = 0; n < nfft; n++)
            {
                re[n] = padded[f * hop + n] * window[n];
                im[n] = 0;
            }
            Fft(re, im);
            for (var k = 0; k < bins; k++) mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k] + 1e-9);
            for (var m = 0; m < 80; m++)
            {
                double e = 0;
                for (var k = 0; k < bins; k++) e += basis[m * bins + k] * mag[k];
                mel[m * frames + f] = (float)Math.Log(Math.Max(e, 1e-5));
            }
        }
        return (mel, frames);
    }

    /// <summary>In-place radix-2 complex FFT (length a power of two).</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = re[i + k];
                    var ui = im[i + k];
                    var vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    var vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;
                    var t = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = t;
                }
            }
        }
    }
}
