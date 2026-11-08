using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

public sealed record RankedItem(string Name, string? Detail, int Plays, double Minutes);

/// <summary>Listening statistics over a period (and the yearly "Rétrospective").</summary>
public sealed record ListeningReport(
    DateTime From,
    DateTime To,
    int Plays,
    int DistinctTracks,
    int DistinctArtists,
    double TotalMinutes,
    IReadOnlyList<RankedItem> TopTracks,
    IReadOnlyList<RankedItem> TopArtists,
    IReadOnlyList<double> MinutesByHour,
    IReadOnlyList<double> MinutesByWeekday,
    DateTime? BiggestDay,
    double BiggestDayMinutes,
    int LongestStreakDays,
    HistoryEntry? First,
    HistoryEntry? Last)
{
    public bool IsEmpty => Plays == 0;

    /// <summary>Hour of the day (0–23) with the most listening.</summary>
    public int PeakHour => MinutesByHour.Count == 0 ? 0 : MinutesByHour.Select((m, h) => (m, h)).MaxBy(x => x.m).h;
}

public static class ListeningStats
{
    /// <summary>A play shorter than this is a skip: it does not count in the rankings.</summary>
    public const double MinCountedSeconds = 30;

    /// <param name="durationOf">Fallback duration (seconds) when a history entry has no listened time yet.</param>
    public static ListeningReport Compute(IEnumerable<HistoryEntry> history, DateTime from, DateTime to, int top = 10,
        Func<Guid, double?>? durationOf = null)
    {
        var entries = history.Where(h => h.PlayedAt >= from && h.PlayedAt < to).OrderBy(h => h.PlayedAt).ToList();
        double Minutes(HistoryEntry h) =>
            (h.ListenedSeconds > 0 ? h.ListenedSeconds : durationOf?.Invoke(h.TrackId) ?? 0) / 60.0;

        var counted = entries.Where(h => h.ListenedSeconds <= 0 || h.ListenedSeconds >= MinCountedSeconds).ToList();

        var topTracks = counted
            .GroupBy(h => h.TrackId)
            .Select(g => new RankedItem(g.Last().Title, g.Last().Artist, g.Count(), g.Sum(Minutes)))
            .OrderByDescending(r => r.Plays).ThenByDescending(r => r.Minutes)
            .Take(top).ToList();

        var withArtist = counted.Where(h => !string.IsNullOrWhiteSpace(h.Artist)).ToList();
        var topArtists = withArtist
            .GroupBy(h => MusicLibrary.Normalize(h.Artist))
            .Select(g => new RankedItem(g.GroupBy(h => h.Artist!).MaxBy(x => x.Count())!.Key, null, g.Count(), g.Sum(Minutes)))
            .OrderByDescending(r => r.Plays).ThenByDescending(r => r.Minutes)
            .Take(top).ToList();

        var byHour = new double[24];
        var byWeekday = new double[7];
        foreach (var h in entries)
        {
            var m = Minutes(h);
            byHour[h.PlayedAt.Hour] += m;
            // Monday first.
            byWeekday[((int)h.PlayedAt.DayOfWeek + 6) % 7] += m;
        }

        var byDay = entries.GroupBy(h => h.PlayedAt.Date).Select(g => (Day: g.Key, Minutes: g.Sum(Minutes))).ToList();
        var biggest = byDay.Count > 0 ? byDay.MaxBy(d => d.Minutes) : default;

        var streak = 0;
        var best = 0;
        DateTime? previous = null;
        foreach (var day in byDay.Select(d => d.Day).OrderBy(d => d))
        {
            streak = previous is { } p && (day - p).TotalDays == 1 ? streak + 1 : 1;
            best = Math.Max(best, streak);
            previous = day;
        }

        return new ListeningReport(
            from, to,
            entries.Count,
            entries.Select(h => h.TrackId).Distinct().Count(),
            withArtist.Select(h => MusicLibrary.Normalize(h.Artist)).Distinct().Count(),
            entries.Sum(Minutes),
            topTracks, topArtists, byHour, byWeekday,
            byDay.Count > 0 ? biggest.Day : null, byDay.Count > 0 ? biggest.Minutes : 0,
            best,
            entries.FirstOrDefault(), entries.LastOrDefault());
    }

    public static ListeningReport ForYear(IEnumerable<HistoryEntry> history, int year, Func<Guid, double?>? durationOf = null) =>
        Compute(history, new DateTime(year, 1, 1), new DateTime(year + 1, 1, 1), 5, durationOf);

    public static ListeningReport LastDays(IEnumerable<HistoryEntry> history, int days, DateTime? now = null, Func<Guid, double?>? durationOf = null)
    {
        var end = (now ?? DateTime.Now).Date.AddDays(1);
        return Compute(history, end.AddDays(-days), end, 10, durationOf);
    }

    /// <summary>The artists the user listens to the most recently (seeds for recommendations).</summary>
    public static IReadOnlyList<string> TopArtists(IEnumerable<HistoryEntry> history, int days, int count, DateTime? now = null) =>
        LastDays(history, days, now).TopArtists.Take(count).Select(a => a.Name).ToList();
}
