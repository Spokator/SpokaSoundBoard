namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Measures how loud a file sounds (gated RMS over 400 ms windows, close to EBU R128 integrated loudness)
/// and returns the gain that brings it to a common level, so YouTube clips, MyInstants buttons and local
/// files all play at the same perceived volume.
/// </summary>
public static class LoudnessAnalyzer
{
    public const double TargetDb = -18.0;
    private const double MaxBoostDb = 12.0;
    private const double MaxCutDb = -15.0;
    private const double AbsoluteGateDb = -55.0;
    private const double RelativeGateDb = -15.0;

    /// <summary>Returns the correction in dB, or null when the file cannot be analysed (silent, unreadable).</summary>
    public static double? AnalyzeFile(string path, CancellationToken ct = default, TimeSpan? maxDuration = null)
    {
        try
        {
            var (reader, provider) = AudioFileLoader.Open(path, MixerCore.SampleRate);
            using (reader)
            {
                var limit = (long)((maxDuration ?? TimeSpan.FromMinutes(10)).TotalSeconds * MixerCore.SampleRate * 2);
                var window = MixerCore.SampleRate * 2 * 4 / 10; // 400 ms of stereo samples
                var buffer = new float[window];
                var windows = new List<double>();
                long total = 0;
                while (total < limit)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = 0;
                    while (read < window)
                    {
                        var n = provider.Read(buffer, read, window - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < window / 4) break;
                    double sum = 0;
                    for (var i = 0; i < read; i++) sum += buffer[i] * buffer[i];
                    windows.Add(sum / read);
                    total += read;
                    if (read < window) break;
                }
                return GainFor(windows);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <param name="windowPowers">Mean square value of each 400 ms window.</param>
    internal static double? GainFor(IReadOnlyCollection<double> windowPowers)
    {
        static double ToDb(double power) => 10 * Math.Log10(Math.Max(power, 1e-12));

        var audible = windowPowers.Where(p => ToDb(p) > AbsoluteGateDb).ToList();
        if (audible.Count == 0) return null;
        var relativeGate = ToDb(audible.Average()) + RelativeGateDb;
        var gated = audible.Where(p => ToDb(p) > relativeGate).ToList();
        if (gated.Count == 0) gated = audible;

        var loudness = ToDb(gated.Average());
        return Math.Round(Math.Clamp(TargetDb - loudness, MaxCutDb, MaxBoostDb), 1);
    }
}
