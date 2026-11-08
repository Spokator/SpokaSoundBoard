using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Music;

namespace SpokaSoundBoard.ViewModels.Music;

public sealed record StatsPeriod(string Label, int Days, int? Year);

public sealed record BarItem(string Label, double Value, double Ratio, string ValueText);

/// <summary>The « Statistiques » page: top tracks / artists, listening habits and the yearly retrospective.</summary>
public partial class StatsViewModel : ObservableObject
{
    private readonly MusicLibrary _library;

    public StatsViewModel(MusicLibrary library)
    {
        _library = library;
        var year = DateTime.Now.Year;
        Periods =
        [
            new StatsPeriod("7 derniers jours", 7, null),
            new StatsPeriod("30 derniers jours", 30, null),
            new StatsPeriod($"Année {year}", 0, year),
            new StatsPeriod($"Année {year - 1}", 0, year - 1),
            new StatsPeriod("Depuis toujours", 36500, null),
        ];
        _period = Periods[1];
        Compute();
    }

    public IReadOnlyList<StatsPeriod> Periods { get; }

    [ObservableProperty] private StatsPeriod _period;
    [ObservableProperty] private ListeningReport? _report;
    [ObservableProperty] private IReadOnlyList<BarItem> _hours = [];
    [ObservableProperty] private IReadOnlyList<BarItem> _weekdays = [];
    [ObservableProperty] private IReadOnlyList<string> _highlights = [];

    public bool IsEmpty => Report?.IsEmpty ?? true;

    partial void OnPeriodChanged(StatsPeriod value) => Compute();

    [RelayCommand]
    private void SelectPeriod(StatsPeriod? period)
    {
        if (period != null) Period = period;
    }

    public void Refresh() => Compute();

    private void Compute()
    {
        double? DurationOf(Guid id) => _library.Find(id)?.DurationSeconds;
        var r = Period.Year is { } y
            ? ListeningStats.ForYear(_library.History, y, DurationOf)
            : ListeningStats.LastDays(_library.History, Period.Days, null, DurationOf);
        Report = r;

        var maxHour = Math.Max(1, r.MinutesByHour.Max());
        Hours = r.MinutesByHour.Select((m, h) => new BarItem(h % 3 == 0 ? $"{h}h" : "", m, m / maxHour, $"{h} h : {m:0} min")).ToList();
        string[] days = ["Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim"];
        var maxDay = Math.Max(1, r.MinutesByWeekday.Max());
        Weekdays = r.MinutesByWeekday.Select((m, d) => new BarItem(days[d], m, m / maxDay, $"{days[d]} : {m:0} min")).ToList();

        var lines = new List<string>();
        if (!r.IsEmpty)
        {
            lines.Add($"{FormatMinutes(r.TotalMinutes)} d'écoute, {r.Plays} lecture{(r.Plays > 1 ? "s" : "")}, {r.DistinctTracks} morceau{(r.DistinctTracks > 1 ? "x" : "")} différent{(r.DistinctTracks > 1 ? "s" : "")}.");
            if (r.TopArtists.Count > 0) lines.Add($"Artiste préféré : {r.TopArtists[0].Name} ({r.TopArtists[0].Plays} écoutes).");
            if (r.TopTracks.Count > 0) lines.Add($"Morceau le plus écouté : {r.TopTracks[0].Name} ({r.TopTracks[0].Plays} fois).");
            if (r.BiggestDay is { } day) lines.Add($"Plus grosse journée : le {day:d MMMM yyyy} avec {FormatMinutes(r.BiggestDayMinutes)}.");
            if (r.LongestStreakDays > 1) lines.Add($"Série record : {r.LongestStreakDays} jours d'écoute d'affilée.");
            lines.Add($"Votre heure de pointe : {r.PeakHour} h.");
            if (r.First != null && Period.Year != null) lines.Add($"Premier morceau de l'année : {r.First.Title}.");
        }
        Highlights = lines;
        OnPropertyChanged(nameof(IsEmpty));
    }

    public static string FormatMinutes(double minutes) =>
        minutes >= 120 ? $"{minutes / 60:0.#} heures" : $"{minutes:0} minutes";
}
