using System.Diagnostics;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>What <see cref="RenderMonitor"/> measured since the audio started (or since <see cref="RenderMonitor.Reset"/>).</summary>
/// <param name="Blocks">Audio blocks rendered.</param>
/// <param name="AverageLoad">Average render time / block duration (0.05 = 5 % of the time budget).</param>
/// <param name="PeakLoad">Worst block, same unit. Above 1 the block was late.</param>
/// <param name="LateBlocks">Blocks whose render took longer than their own duration: almost certainly heard as a gap.</param>
/// <param name="SlowBlocks">Blocks that used more than half of their budget: harmless alone, a warning sign when frequent.</param>
/// <param name="Stalls">Times the device waited much longer than usual for a block (the whole PC was busy).</param>
/// <param name="DecodeUnderruns">Times a sound or a song was not decoded in time (disk too slow).</param>
public sealed record RenderHealth(long Blocks, double AverageLoad, double PeakLoad, long LateBlocks, long SlowBlocks, long Stalls, long DecodeUnderruns)
{
    /// <summary>Something the user may have heard.</summary>
    public long Glitches => LateBlocks + Stalls + DecodeUnderruns;
}

/// <summary>
/// Measures the audio thread: how long each block takes to render compared with how long it lasts, and how
/// regularly the device asks for blocks. Audio thread side (<see cref="Begin"/> / <see cref="End"/>) is lock-free and
/// allocation-free; <see cref="Snapshot"/> can be called from any thread.
/// </summary>
public sealed class RenderMonitor
{
    /// <summary>Blocks ignored by the stall detection after a (re)start.</summary>
    public const int WarmUpBlocks = 20;

    /// <summary>Counted by every decoder when it falls behind (shared by all players).</summary>
    private static long _decodeUnderruns;

    private readonly int _sampleRate;
    private readonly double _tickSeconds = 1.0 / Stopwatch.Frequency;
    private long _blocks, _late, _slow, _stalls;
    private long _busyTicks, _budgetTicks;
    private long _peakPermille;
    private long _lastStart;
    private long _underrunBase;

    public RenderMonitor(int sampleRate)
    {
        _sampleRate = sampleRate;
        _underrunBase = Interlocked.Read(ref _decodeUnderruns);
    }

    public static void ReportDecodeUnderrun() => Interlocked.Increment(ref _decodeUnderruns);

    public static long DecodeUnderrunsTotal => Interlocked.Read(ref _decodeUnderruns);

    /// <summary>Audio thread, before rendering. Returns the timestamp to give back to <see cref="End"/>.</summary>
    public long Begin() => Stopwatch.GetTimestamp();

    /// <summary>Audio thread, after rendering <paramref name="frames"/> frames.</summary>
    public void End(long start, int frames) => Record(start, Stopwatch.GetTimestamp(), frames);

    /// <summary>Core of the measure, with explicit timestamps (in <see cref="Stopwatch"/> ticks) for the tests.</summary>
    public void Record(long start, long end, int frames)
    {
        if (frames <= 0) return;
        var budget = (long)(frames / (double)_sampleRate / _tickSeconds);
        if (budget <= 0) return;
        var busy = Math.Max(0, end - start);

        // A stall: the device came back much later than one block (plus scheduling slack) after the previous one.
        // The first blocks after a (re)start are irregular by nature (the device fills its buffer): not counted.
        var last = _lastStart;
        if (last != 0 && Interlocked.Read(ref _blocks) >= WarmUpBlocks)
        {
            var interval = start - last;
            var slack = (long)(0.020 / _tickSeconds);
            if (interval > 3 * budget + slack) Interlocked.Increment(ref _stalls);
        }
        _lastStart = start;

        Interlocked.Increment(ref _blocks);
        Interlocked.Add(ref _busyTicks, busy);
        Interlocked.Add(ref _budgetTicks, budget);
        if (busy > budget) Interlocked.Increment(ref _late);
        else if (busy * 2 > budget) Interlocked.Increment(ref _slow);

        var permille = busy * 1000 / budget;
        long peak;
        while (permille > (peak = Interlocked.Read(ref _peakPermille)))
            if (Interlocked.CompareExchange(ref _peakPermille, permille, peak) == peak) break;
    }

    public RenderHealth Snapshot()
    {
        var budget = Interlocked.Read(ref _budgetTicks);
        return new RenderHealth(
            Interlocked.Read(ref _blocks),
            budget > 0 ? Interlocked.Read(ref _busyTicks) / (double)budget : 0,
            Interlocked.Read(ref _peakPermille) / 1000.0,
            Interlocked.Read(ref _late),
            Interlocked.Read(ref _slow),
            Interlocked.Read(ref _stalls),
            Interlocked.Read(ref _decodeUnderruns) - Interlocked.Read(ref _underrunBase));
    }

    /// <summary>Starts counting again (when the audio restarts).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _blocks, 0);
        Interlocked.Exchange(ref _late, 0);
        Interlocked.Exchange(ref _slow, 0);
        Interlocked.Exchange(ref _stalls, 0);
        Interlocked.Exchange(ref _busyTicks, 0);
        Interlocked.Exchange(ref _budgetTicks, 0);
        Interlocked.Exchange(ref _peakPermille, 0);
        Interlocked.Exchange(ref _underrunBase, Interlocked.Read(ref _decodeUnderruns));
        _lastStart = 0;
    }
}
