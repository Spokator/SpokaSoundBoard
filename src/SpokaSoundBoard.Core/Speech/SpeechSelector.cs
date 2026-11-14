namespace SpokaSoundBoard.Core.Speech;

/// <summary>What was kept from recordings to make a voice.</summary>
/// <param name="Speech">Speech only (silences removed), the best 3 s first.</param>
/// <param name="Clipped">Share of the kept speech that is saturated (a hint: too loud recordings).</param>
public sealed record SelectedSpeech(float[] Speech, int SampleRate, double Seconds, double Clipped)
{
    public double TotalSeconds => Speech.Length / (double)SampleRate;
}

/// <summary>
/// Picks the speech in recordings or imported files to build a voice from: silences and breathing out, the best
/// 3 seconds first (steady, loud enough, not saturated: they become the voice's « prompt »), at most a minute.
/// </summary>
public static class SpeechSelector
{
    public const double MinimumSeconds = 5;
    public const double RecommendedSeconds = 20;
    public const double MaximumSeconds = 60;
    private const double FrameSeconds = 0.02;

    public static SelectedSpeech Select(IReadOnlyList<float[]> clips, int sampleRate)
    {
        var frame = (int)(sampleRate * FrameSeconds);
        var kept = new List<float>();
        foreach (var clip in clips) AppendSpeech(clip, sampleRate, frame, kept);
        var max = (int)(MaximumSeconds * sampleRate);
        if (kept.Count > max) kept.RemoveRange(max, kept.Count - max);
        var speech = kept.ToArray();

        // Best 3 s: most frames with steady level, no saturation. Moved first.
        var window = Math.Min(speech.Length, 3 * sampleRate);
        var best = 0;
        if (speech.Length > window)
        {
            var frames = speech.Length / frame;
            var score = new double[frames];
            for (var f = 0; f < frames; f++)
            {
                double sum = 0;
                var clipped = false;
                for (var i = 0; i < frame; i++)
                {
                    var v = speech[f * frame + i];
                    sum += v * v;
                    if (MathF.Abs(v) > 0.98f) clipped = true;
                }
                var db = 10 * Math.Log10(sum / frame + 1e-12);
                score[f] = clipped ? -1 : db > -40 ? 1 : 0;
            }
            // Around a saturated frame (fades, the end of a shout) is not clean either.
            var near = (int[])[.. Enumerable.Range(0, frames).Where(f => score[f] < 0)];
            foreach (var f in near)
                for (var g = Math.Max(0, f - 3); g <= Math.Min(frames - 1, f + 3); g++)
                    score[g] = -1;
            var span = window / frame;
            double running = 0, bestScore = double.MinValue;
            for (var f = 0; f < frames; f++)
            {
                running += score[f];
                if (f >= span) running -= score[f - span];
                if (f >= span - 1 && running > bestScore)
                {
                    bestScore = running;
                    best = (f - span + 1) * frame;
                }
            }
        }
        var ordered = new float[speech.Length];
        Array.Copy(speech, best, ordered, 0, window);
        Array.Copy(speech, 0, ordered, window, best);
        Array.Copy(speech, best + window, ordered, window + best, speech.Length - best - window);

        var saturated = speech.Count(v => MathF.Abs(v) > 0.98f);
        return new SelectedSpeech(ordered, sampleRate, speech.Length / (double)sampleRate,
            speech.Length == 0 ? 0 : saturated / (double)speech.Length);
    }

    /// <summary>Speech of one clip: frames well above its noise floor, with 100 ms around, joined with short fades.</summary>
    private static void AppendSpeech(float[] clip, int sampleRate, int frame, List<float> output)
    {
        var frames = clip.Length / frame;
        if (frames == 0) return;
        var db = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = 0; i < frame; i++)
            {
                var v = clip[f * frame + i];
                sum += v * v;
            }
            db[f] = 10 * Math.Log10(sum / frame + 1e-12);
        }
        var sorted = db.Order().ToArray();
        var floor = sorted[(int)(sorted.Length * 0.1)];
        var loud = sorted[(int)(sorted.Length * 0.95)];
        // Well above the noise floor, but never more than 12 dB under the loud parts: a clip with no silence at all
        // (an imported dialogue) is kept whole instead of being cut at its own « floor ».
        var threshold = Math.Min(Math.Max(floor + 10, loud - 35), loud - 12);
        if (loud < -55) return; // nothing but silence

        var speech = new bool[frames];
        const int pad = 5; // 100 ms
        for (var f = 0; f < frames; f++)
            if (db[f] > threshold)
                for (var g = Math.Max(0, f - pad); g <= Math.Min(frames - 1, f + pad); g++) speech[g] = true;

        var fade = Math.Min(frame, sampleRate / 100); // 10 ms
        for (var f = 0; f < frames;)
        {
            if (!speech[f])
            {
                f++;
                continue;
            }
            var start = f;
            while (f < frames && speech[f]) f++;
            if (f - start < 10) continue; // under 200 ms: a click, not speech
            var from = start * frame;
            var to = f * frame;
            for (var i = from; i < to; i++)
            {
                var gain = 1f;
                if (i - from < fade) gain = (i - from) / (float)fade;
                else if (to - i <= fade) gain = (to - i - 1) / (float)fade;
                output.Add(clip[i] * gain);
            }
        }
    }
}
