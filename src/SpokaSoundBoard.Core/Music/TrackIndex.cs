using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

/// <summary>
/// Dictionaries over the library tracks (by id, file, web source and search text), kept in step with the collection
/// and with the tracks' own changes. Same answers as scanning the list in order (the first matching track wins), but
/// in constant time: importing a playlist of 1 000 songs into a library of 5 000 no longer compares millions of pairs.
/// A change that cannot be applied incrementally (removal, move, key edited) simply marks the index stale: it is
/// rebuilt on the next lookup. UI thread only, like <see cref="MusicLibrary"/>.
/// </summary>
internal sealed class TrackIndex
{
    private readonly ObservableCollection<TrackItem> _tracks;
    private readonly Dictionary<Guid, TrackItem> _byId = [];
    private readonly Dictionary<string, TrackItem> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackItem> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackItem> _bySearch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<TrackItem> _watched = new(ReferenceEqualityComparer.Instance);
    private bool _stale = true;

    public TrackIndex(ObservableCollection<TrackItem> tracks)
    {
        _tracks = tracks;
        _tracks.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>Times the index was rebuilt from scratch (for the tests).</summary>
    public int Rebuilds { get; private set; }

    public TrackItem? ById(Guid id) => Fresh()._byId.GetValueOrDefault(id);
    public TrackItem? ByFile(string path) => Fresh()._byFile.GetValueOrDefault(path);
    public TrackItem? BySource(string sourceKey) => Fresh()._bySource.GetValueOrDefault(sourceKey);
    public TrackItem? BySearch(string query) => Fresh()._bySearch.GetValueOrDefault(query);

    private TrackIndex Fresh()
    {
        if (!_stale) return this;
        _byId.Clear();
        _byFile.Clear();
        _bySource.Clear();
        _bySearch.Clear();
        foreach (var t in _watched) t.PropertyChanged -= OnTrackChanged;
        _watched.Clear();
        foreach (var t in _tracks) Add(t);
        _stale = false;
        Rebuilds++;
        return this;
    }

    private void Add(TrackItem t)
    {
        // TryAdd: the first track in list order keeps the key, as a scan would find it.
        _byId.TryAdd(t.Id, t);
        if (!string.IsNullOrEmpty(t.FilePath)) _byFile.TryAdd(t.FilePath, t);
        if (!string.IsNullOrEmpty(t.SourceUrl)) _bySource.TryAdd(MusicLibrary.SourceKey(t.SourceUrl), t);
        if (!string.IsNullOrWhiteSpace(t.SearchQuery)) _bySearch.TryAdd(t.SearchQuery, t);
        if (_watched.Add(t)) t.PropertyChanged += OnTrackChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_stale) return;
        // Appending keeps the « first in order » rule: index the new tracks right away.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && e.NewStartingIndex == _tracks.Count - e.NewItems.Count)
        {
            foreach (TrackItem t in e.NewItems) Add(t);
            return;
        }
        _stale = true;
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackItem.FilePath) or nameof(TrackItem.SourceUrl) or nameof(TrackItem.SearchQuery))
            _stale = true;
    }
}
