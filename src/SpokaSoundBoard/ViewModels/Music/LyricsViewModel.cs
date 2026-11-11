using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels.Music;

public sealed partial class LyricLineViewModel(TimeSpan time, string text) : ObservableObject
{
    public TimeSpan Time { get; } = time;
    public string Text { get; } = string.IsNullOrWhiteSpace(text) ? "♪" : text;

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPast;
}

/// <summary>Synchronised lyrics (karaoke) of the playing track, from lrclib.net.</summary>
public partial class LyricsViewModel : ObservableObject
{
    private readonly MusicViewModel _music;
    private readonly LyricsService _service;
    private IReadOnlyList<LyricLine> _synced = [];
    private CancellationTokenSource? _cts;
    private TrackItem? _track;

    public LyricsViewModel(MusicViewModel music)
    {
        _music = music;
        _service = new LyricsService(Path.Combine(music.Main.DataDirectory, "Lyrics"));
        music.NowPlayingChanged += OnTrackChanged;
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = [];

    [ObservableProperty] private string? _plainText;
    [ObservableProperty] private string? _status = "Lancez une musique pour afficher ses paroles.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSynced;
    [ObservableProperty] private int _currentIndex = -1;

    /// <summary>Lyrics are shown slightly ahead so the singer can read the line before it starts.</summary>
    public TimeSpan Lead { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Raised when the highlighted line changes (the view scrolls it into the centre).</summary>
    public event Action<LyricLineViewModel>? CurrentLineChanged;

    private void OnTrackChanged(TrackItem? track)
    {
        if (track == _track) return;
        _track = track;
        _ = LoadAsync(track);
    }

    [RelayCommand]
    private Task Retry() => LoadAsync(_track, retry: true);

    private async Task LoadAsync(TrackItem? track, bool retry = false)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Lines.Clear();
        _synced = [];
        PlainText = null;
        IsSynced = false;
        CurrentIndex = -1;
        if (track == null)
        {
            Status = "Lancez une musique pour afficher ses paroles.";
            return;
        }

        IsLoading = true;
        Status = "Recherche des paroles…";
        try
        {
            var (artist, title) = track.Artist != null
                ? (track.Artist, track.Title)
                : ArtistTitle.Split(track.Title, null);
            var duration = track.DurationSeconds is > 0 ? TimeSpan.FromSeconds(track.DurationSeconds.Value) : (TimeSpan?)null;
            var lyrics = await _service.GetAsync(title, artist, duration, ct, retry);
            if (ct.IsCancellationRequested) return;

            if (lyrics == null)
            {
                Status = "Paroles introuvables pour ce morceau.";
            }
            else if (lyrics.IsInstrumental)
            {
                Status = "Morceau instrumental ♪";
            }
            else if (lyrics.Synced.Count > 0)
            {
                _synced = lyrics.Synced;
                foreach (var l in lyrics.Synced) Lines.Add(new LyricLineViewModel(l.Time, l.Text));
                IsSynced = true;
                Status = null;
            }
            else
            {
                PlainText = lyrics.Plain;
                Status = string.IsNullOrWhiteSpace(lyrics.Plain) ? "Paroles introuvables pour ce morceau." : null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = "Paroles indisponibles : " + Core.FriendlyErrors.Describe(ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoading = false;
        }
    }

    /// <summary>Called by the player tick with the playback position.</summary>
    public void Update(TimeSpan position)
    {
        if (_synced.Count == 0) return;
        var index = LyricsService.LineAt(_synced, position + Lead);
        if (index == CurrentIndex) return;
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].IsCurrent = i == index;
            Lines[i].IsPast = i < index;
        }
        CurrentIndex = index;
        if (index >= 0 && index < Lines.Count) CurrentLineChanged?.Invoke(Lines[index]);
    }

    /// <summary>Click on a line: jump there.</summary>
    [RelayCommand]
    private void SeekToLine(LyricLineViewModel? line)
    {
        if (line != null) _music.SeekTo(Math.Max(0, line.Time.TotalSeconds - 0.2));
    }
}
