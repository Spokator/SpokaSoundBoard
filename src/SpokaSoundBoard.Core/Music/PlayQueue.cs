using System.Collections.ObjectModel;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Core.Music;

/// <summary>
/// The play queue ("À suivre"). Items keep their visible order; enabling shuffle physically reorders the
/// upcoming items (Fisher-Yates, no repeats) and disabling it restores the original order, like Spotify.
/// Every method must be called from the UI thread.
/// </summary>
public sealed class PlayQueue
{
    private const int MaxBackStack = 200;

    private readonly Random _random;
    private readonly List<TrackItem> _backStack = [];

    /// <summary>Original (unshuffled) order of the items, used to restore it when shuffle is disabled.</summary>
    private List<TrackItem> _originalOrder = [];

    private bool _shuffle;

    public PlayQueue(Random? random = null) => _random = random ?? new Random();

    public ObservableCollection<TrackItem> Items { get; } = [];

    /// <summary>Index of the current track in <see cref="Items"/> (-1 = nothing).</summary>
    public int CurrentIndex { get; private set; } = -1;

    public TrackItem? Current => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    public RepeatMode Repeat { get; set; } = RepeatMode.All;

    /// <summary>Remove each track from the queue once it has been played.</summary>
    public bool Consume { get; set; }

    public int Count => Items.Count;

    /// <summary>Tracks after the current one, in play order.</summary>
    public IEnumerable<TrackItem> Upcoming => Items.Skip(CurrentIndex + 1);

    /// <summary>Raised whenever <see cref="Current"/> or <see cref="CurrentIndex"/> may have changed.</summary>
    public event Action? CurrentChanged;

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            if (value) ShuffleUpcoming();
            else RestoreOrder();
        }
    }

    /// <summary>Replaces the whole queue (e.g. "play this playlist") and makes <paramref name="startIndex"/> current.</summary>
    /// <param name="startIndex">Track to start with; -1 starts with a random track when shuffle is on, else the first.</param>
    public void Replace(IEnumerable<TrackItem> tracks, int startIndex = 0)
    {
        var list = tracks.ToList();
        Items.Clear();
        _backStack.Clear();
        _originalOrder = [.. list];
        if (list.Count == 0)
        {
            SetCurrent(-1);
            return;
        }

        if (startIndex < 0 || startIndex >= list.Count) startIndex = _shuffle ? _random.Next(list.Count) : 0;
        var first = list[startIndex];
        if (_shuffle)
        {
            // The chosen track first, then everything else shuffled.
            list.RemoveAt(startIndex);
            FisherYates(list);
            list.Insert(0, first);
            startIndex = 0;
        }
        foreach (var t in list) Items.Add(t);
        SetCurrent(startIndex);
    }

    /// <summary>Adds tracks at the end of the queue.</summary>
    public void Enqueue(IEnumerable<TrackItem> tracks)
    {
        foreach (var t in tracks)
        {
            Items.Add(t);
            _originalOrder.Add(t);
        }
        if (CurrentIndex < 0 && Items.Count > 0) CurrentChanged?.Invoke();
    }

    public void Enqueue(TrackItem track) => Enqueue([track]);

    /// <summary>Inserts tracks right after the current one ("Lire ensuite").</summary>
    public void PlayNext(IEnumerable<TrackItem> tracks)
    {
        var insertAt = CurrentIndex + 1;
        var originalAt = Current != null ? _originalOrder.IndexOf(Current) + 1 : _originalOrder.Count;
        foreach (var t in tracks)
        {
            Items.Insert(insertAt++, t);
            _originalOrder.Insert(Math.Clamp(originalAt++, 0, _originalOrder.Count), t);
        }
    }

    public void PlayNext(TrackItem track) => PlayNext([track]);

    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count || to < 0 || to >= Items.Count || from == to) return;
        var current = Current;
        Items.Move(from, to);
        CurrentIndex = current != null ? Items.IndexOf(current) : -1;
        if (!_shuffle)
        {
            // Without shuffle, the visible order is the reference order.
            _originalOrder = [.. Items];
        }
        CurrentChanged?.Invoke();
    }

    /// <summary>Removes one occurrence. Removing the current track leaves the queue without a current track.</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        var track = Items[index];
        Items.RemoveAt(index);
        _originalOrder.Remove(track);
        _backStack.RemoveAll(t => t == track);
        if (index < CurrentIndex) CurrentIndex--;
        else if (index == CurrentIndex) CurrentIndex = -1;
        CurrentChanged?.Invoke();
    }

    public void Remove(TrackItem track)
    {
        int i;
        while ((i = Items.IndexOf(track)) >= 0) RemoveAt(i);
    }

    /// <summary>Empties the queue. With <paramref name="keepCurrent"/>, the playing track stays (alone).</summary>
    public void Clear(bool keepCurrent = false)
    {
        var current = keepCurrent ? Current : null;
        Items.Clear();
        _originalOrder.Clear();
        _backStack.Clear();
        if (current != null)
        {
            Items.Add(current);
            _originalOrder.Add(current);
            SetCurrent(0);
        }
        else
        {
            SetCurrent(-1);
        }
    }

    /// <summary>Removes the upcoming tracks only (keeps what has been played and the current track).</summary>
    public void ClearUpcoming()
    {
        for (var i = Items.Count - 1; i > CurrentIndex; i--)
        {
            _originalOrder.Remove(Items[i]);
            Items.RemoveAt(i);
        }
        CurrentChanged?.Invoke();
    }

    /// <summary>Removes duplicate entries of the same track (keeps the first one, and the current one).</summary>
    public int RemoveDuplicates()
    {
        var seen = new HashSet<Guid>();
        if (Current != null) seen.Add(Current.Id);
        var removed = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            if (i == CurrentIndex) continue;
            if (seen.Add(Items[i].Id)) continue;
            RemoveAt(i);
            i--;
            removed++;
        }
        return removed;
    }

    /// <summary>Makes <paramref name="index"/> the current track (double-click in the queue).</summary>
    public TrackItem? JumpTo(int index)
    {
        if (index < 0 || index >= Items.Count) return null;
        PushBack(Current);
        SetCurrent(index);
        return Current;
    }

    /// <summary>The track that <see cref="Advance(bool)"/> would return for a natural end (used to preload it).</summary>
    public TrackItem? PeekNext()
    {
        if (Items.Count == 0) return null;
        if (Repeat == RepeatMode.One) return Current ?? Items[0];
        var next = CurrentIndex + 1;
        if (next < Items.Count) return Items[next];
        return Repeat == RepeatMode.All ? Items[0] : null;
    }

    /// <summary>Moves to the next track and returns it (null = end of the queue).</summary>
    /// <param name="auto">True when the previous track ended by itself (repeat-one applies), false for the Next button.</param>
    public TrackItem? Advance(bool auto)
    {
        if (Items.Count == 0)
        {
            SetCurrent(-1);
            return null;
        }
        if (auto && Repeat == RepeatMode.One && Current != null)
        {
            CurrentChanged?.Invoke();
            return Current;
        }

        var previous = Current;
        var next = CurrentIndex + 1;
        if (Consume && previous != null)
        {
            // The played track leaves the queue; what followed it now sits at its index.
            RemoveAt(CurrentIndex);
            next = Math.Max(0, next - 1);
            previous = null;
        }

        if (next >= Items.Count)
        {
            if (Items.Count == 0 || Repeat == RepeatMode.Off && auto)
            {
                PushBack(previous);
                SetCurrent(-1);
                return null;
            }
            next = 0;
            if (_shuffle && Items.Count > 2)
            {
                // New lap: reshuffle everything except the first item, which PeekNext() announced
                // (and the player may already have preloaded).
                var first = Items[0];
                var rest = Items.Skip(1).ToList();
                FisherYates(rest);
                Items.Clear();
                Items.Add(first);
                foreach (var t in rest) Items.Add(t);
            }
        }

        PushBack(previous);
        SetCurrent(next);
        return Current;
    }

    /// <summary>Goes back to the track played before the current one (history first, then the previous item).</summary>
    public TrackItem? GoBack()
    {
        while (_backStack.Count > 0)
        {
            var last = _backStack[^1];
            _backStack.RemoveAt(_backStack.Count - 1);
            var index = Items.IndexOf(last);
            if (index >= 0)
            {
                SetCurrent(index);
                return Current;
            }
        }
        if (Items.Count == 0) return null;
        var previous = CurrentIndex - 1;
        if (previous < 0) previous = Repeat == RepeatMode.All ? Items.Count - 1 : 0;
        SetCurrent(previous);
        return Current;
    }

    /// <summary>Restores a queue saved on exit.</summary>
    public void Restore(IEnumerable<TrackItem> tracks, int currentIndex)
    {
        Items.Clear();
        _backStack.Clear();
        foreach (var t in tracks) Items.Add(t);
        _originalOrder = [.. Items];
        SetCurrent(currentIndex >= 0 && currentIndex < Items.Count ? currentIndex : Items.Count > 0 ? 0 : -1);
    }

    private void ShuffleUpcoming()
    {
        _originalOrder = [.. Items];
        var start = CurrentIndex + 1;
        if (Items.Count - start < 2) return;
        var upcoming = Items.Skip(start).ToList();
        FisherYates(upcoming);
        for (var i = 0; i < upcoming.Count; i++) Items[start + i] = upcoming[i];
    }

    private void RestoreOrder()
    {
        var current = Current;
        var present = Items.ToHashSet();
        var ordered = _originalOrder.Where(present.Contains).ToList();
        // Tracks added while shuffled but missing from the reference order go to the end.
        ordered.AddRange(Items.Where(t => !ordered.Contains(t)));
        Items.Clear();
        foreach (var t in ordered) Items.Add(t);
        _originalOrder = [.. ordered];
        CurrentIndex = current != null ? Items.IndexOf(current) : -1;
        CurrentChanged?.Invoke();
    }

    private void FisherYates(List<TrackItem> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void PushBack(TrackItem? track)
    {
        if (track == null) return;
        _backStack.Add(track);
        if (_backStack.Count > MaxBackStack) _backStack.RemoveAt(0);
    }

    private void SetCurrent(int index)
    {
        CurrentIndex = index;
        CurrentChanged?.Invoke();
    }
}
