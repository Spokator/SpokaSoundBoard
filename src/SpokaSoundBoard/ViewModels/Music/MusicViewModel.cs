using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Music;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Services;

namespace SpokaSoundBoard.ViewModels.Music;

/// <summary>
/// The music player: library, play queue, on-demand download, preloading / crossfade, history and autoplay.
/// Split in several files: playback (here), library browsing, online features.
/// </summary>
public partial class MusicViewModel : ObservableObject
{
    public const string DownloadFolder = "Musique";
    private const int MaxConsecutiveFailures = 3;

    private readonly MainViewModel _main;
    private readonly LibraryStore _libraryStore;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, Task<bool>> _downloads = [];
    private readonly Stopwatch _listenClock = new();
    private readonly DispatcherTimer _prepareTimer;
    private readonly DispatcherTimer _librarySaveTimer;
    private HistoryEntry? _currentEntry;
    private int _playToken;
    private int _consecutiveFailures;
    private TrackItem? _preparedNext;
    private bool _fetchingAutoplay;

    public MusicViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _libraryStore = new LibraryStore(main.DataDirectory);
        Library = new MusicLibrary(_libraryStore.Load());
        if (LibraryStore.Migrate(main.Settings, Library))
        {
            main.Settings.Playlist.Clear();
            _libraryStore.Save(Library.Data);
            // The main view model is still being built: save the settings once it is ready.
            _dispatcher.BeginInvoke(main.Save, DispatcherPriority.Background);
        }

        Queue = new PlayQueue { Repeat = main.Settings.Repeat, Consume = Settings.ConsumeQueue };
        Queue.Shuffle = main.Settings.Shuffle;
        _repeat = Queue.Repeat;
        _shuffle = Queue.Shuffle;

        Player.Effects = Settings.Effects;
        Player.CrossfadeDuration = TimeSpan.FromSeconds(Settings.CrossfadeSeconds);
        Player.TrackAdvanced += path => _dispatcher.BeginInvoke(() => { if (!IsExternalPlayback) OnTrackAdvanced(path); });
        Player.TrackEnded += () => _dispatcher.BeginInvoke(() =>
        {
            if (IsExternalPlayback) ExternalTrackEnded?.Invoke();
            else OnTrackEnded();
        });
        Player.NextTrackFailed += (path, _) => _dispatcher.BeginInvoke(() => { if (!IsExternalPlayback) OnNextTrackFailed(path); });

        Settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MusicSettings.CrossfadeSeconds):
                    Player.CrossfadeDuration = TimeSpan.FromSeconds(Settings.CrossfadeSeconds);
                    break;
                case nameof(MusicSettings.ConsumeQueue):
                    Queue.Consume = Settings.ConsumeQueue;
                    break;
                case nameof(MusicSettings.Autoplay):
                    SchedulePrepareNext();
                    break;
            }
            _main.Save();
        };
        main.Mix.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MixSettings.NormalizeLoudness))
            {
                ApplyGain();
                SchedulePrepareNext();
            }
        };

        _prepareTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _prepareTimer!.Stop();
            _ = PrepareNextAsync();
        }, _dispatcher);
        _prepareTimer.Stop();
        _librarySaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            _librarySaveTimer!.Stop();
            SaveLibrary();
        }, _dispatcher);
        _librarySaveTimer.Stop();

        Queue.Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(QueueIsEmpty));
            OnPropertyChanged(nameof(UpNextCount));
            SchedulePrepareNext();
        };
        Queue.CurrentChanged += SchedulePrepareNext;
        Library.Changed += ScheduleLibrarySave;

        Effects = new MusicEffectsViewModel(this);
        Lyrics = new LyricsViewModel(this);
        Search = new MusicSearchViewModel(this);
        InitializeLibraryBrowsing();
    }

    // ---- Shared services ---------------------------------------------------------------------------------

    internal MainViewModel Main => _main;
    internal MusicPlayer Player => _main.Engine.Music;
    internal MediaDownloader Downloader => _main.Downloader;
    public MusicSettings Settings => _main.Settings.Music;
    public MusicLibrary Library { get; }
    public PlayQueue Queue { get; }
    public MusicEffectsViewModel Effects { get; }
    public LyricsViewModel Lyrics { get; }
    public MusicSearchViewModel Search { get; }

    public bool QueueIsEmpty => Queue.Count == 0;
    public int UpNextCount => Math.Max(0, Queue.Count - Queue.CurrentIndex - 1);

    // ---- Now playing --------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTitle), nameof(CurrentSubtitle), nameof(HasTrack))]
    private TrackItem? _currentTrack;

    [ObservableProperty] private bool _isPlaying;

    /// <summary>The current track is being downloaded / resolved before it can play.</summary>
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatIcon), nameof(RepeatTooltip))]
    private RepeatMode _repeat;

    [ObservableProperty] private bool _shuffle;

    /// <summary>Right-hand panel of the music page: "queue", "lyrics", "effects" or "" (hidden).</summary>
    [ObservableProperty] private string _sidePanel = "queue";

    /// <summary>True while the user drags the seek bar (position updates are paused).</summary>
    public bool IsSeeking { get; set; }

    public string CurrentTitle => CurrentTrack?.Title ?? "Aucune musique";
    public string CurrentSubtitle => CurrentTrack == null ? "" : IsLoading ? CurrentTrack.StateMessage ?? "Chargement…" : CurrentTrack.Subtitle;
    public bool HasTrack => CurrentTrack != null;
    public string RepeatIcon => Repeat == RepeatMode.One ? "" : "";
    public string RepeatTooltip => Repeat switch
    {
        RepeatMode.Off => "Répétition : désactivée",
        RepeatMode.All => "Répétition : toute la file",
        _ => "Répétition : ce morceau",
    };

    /// <summary>Raised when the playing track changes (lyrics, media controls, visual tint…).</summary>
    public event Action<TrackItem?>? NowPlayingChanged;

    partial void OnRepeatChanged(RepeatMode value)
    {
        Queue.Repeat = value;
        SchedulePrepareNext();
    }

    partial void OnShuffleChanged(bool value)
    {
        Queue.Shuffle = value;
        SchedulePrepareNext();
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CurrentSubtitle));

    [RelayCommand]
    private void ShowPanel(string panel) => SidePanel = SidePanel == panel ? "" : panel;

    // ---- Transport ---------------------------------------------------------------------------------------

    [RelayCommand]
    public void PlayPause()
    {
        if (IsExternalPlayback) return;
        if (Player.HasTrack)
        {
            if (Player.IsPlaying) Pause(); else Resume();
            return;
        }
        if (Queue.Current != null)
        {
            _ = StartAsync(Queue.Current, resumeAt: _resumePosition);
            _resumePosition = 0;
            return;
        }
        if (Queue.Count > 0)
        {
            _ = StartAsync(Queue.JumpTo(0)!);
            return;
        }
        // Nothing queued: play what the page shows.
        if (VisibleTracks.Count > 0) PlayList(VisibleTracks, 0);
    }

    public void Pause()
    {
        Player.Pause();
        IsPlaying = false;
        _listenClock.Stop();
        MediaControls?.Update();
    }

    public void Resume()
    {
        Player.Play();
        IsPlaying = true;
        _listenClock.Start();
        MediaControls?.Update();
    }

    [RelayCommand]
    public void Stop()
    {
        _playToken++;
        FinishHistoryEntry();
        Player.Stop();
        IsPlaying = false;
        IsLoading = false;
        Position = 0;
        PositionText = "0:00";
        MediaControls?.Update();
    }

    [RelayCommand]
    public void Next()
    {
        if (IsExternalPlayback) return;
        var next = Queue.Advance(auto: false);
        if (next != null) _ = StartAsync(next);
        else _ = AutoplayOrStopAsync();
    }

    [RelayCommand]
    public void Previous()
    {
        if (IsExternalPlayback) return;
        if (Player.HasTrack && Player.Position.TotalSeconds > 3)
        {
            Player.Seek(TimeSpan.Zero);
            return;
        }
        var previous = Queue.GoBack();
        if (previous != null) _ = StartAsync(previous);
    }

    [RelayCommand]
    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    [RelayCommand]
    private void ToggleFavoriteCurrent()
    {
        if (CurrentTrack != null) ToggleFavorite(CurrentTrack);
    }

    public void SeekTo(double seconds)
    {
        Player.Seek(TimeSpan.FromSeconds(seconds));
        Position = seconds;
        PositionText = Format(TimeSpan.FromSeconds(seconds));
        MediaControls?.Update();
    }

    public void SeekBy(double seconds) => SeekTo(Math.Clamp(Player.Position.TotalSeconds + seconds, 0, Player.Duration.TotalSeconds));

    // ---- Queue commands ---------------------------------------------------------------------------------

    /// <summary>Replaces the queue with <paramref name="tracks"/> and starts at <paramref name="index"/>.</summary>
    public void PlayList(IEnumerable<TrackItem> tracks, int index, bool? shuffle = null)
    {
        if (shuffle is { } s) Shuffle = s;
        var list = tracks.ToList();
        if (list.Count == 0) return;
        Queue.Replace(list, shuffle == true ? -1 : index);
        if (Queue.Current is { } first) _ = StartAsync(first);
    }

    [RelayCommand]
    private void PlayNow(TrackItem? track)
    {
        if (track == null) return;
        Queue.PlayNext(track);
        var next = Queue.Advance(auto: false);
        if (next != null) _ = StartAsync(next);
    }

    [RelayCommand]
    private void PlayNextInQueue(TrackItem? track)
    {
        if (track == null) return;
        Queue.PlayNext(track);
        _main.ShowToast($"Lu ensuite : {track.Title}");
        _ = EnsureLocalAsync(track);
    }

    [RelayCommand]
    private void AddToQueue(TrackItem? track)
    {
        if (track == null) return;
        Queue.Enqueue(track);
        _main.ShowToast($"Ajouté à la file : {track.Title}");
    }

    public void AddToQueue(IReadOnlyCollection<TrackItem> tracks)
    {
        if (tracks.Count == 0) return;
        Queue.Enqueue(tracks);
        _main.ShowToast(tracks.Count == 1 ? $"Ajouté à la file : {tracks.First().Title}" : $"{tracks.Count} morceaux ajoutés à la file");
    }

    [RelayCommand]
    private void JumpToQueueItem(TrackItem? track)
    {
        if (track == null) return;
        var index = Queue.Items.IndexOf(track);
        if (index >= 0 && Queue.JumpTo(index) is { } t) _ = StartAsync(t);
    }

    [RelayCommand]
    private void RemoveFromQueue(TrackItem? track)
    {
        if (track == null) return;
        var wasCurrent = track == Queue.Current;
        var index = Queue.Items.IndexOf(track);
        if (index < 0) return;
        Queue.RemoveAt(index);
        if (wasCurrent)
        {
            // Removing the playing track: continue with what followed it.
            if (index < Queue.Count && Queue.JumpTo(index) is { } next) _ = StartAsync(next);
            else Stop();
        }
    }

    public void MoveQueueItem(int from, int to) => Queue.Move(from, to);

    [RelayCommand]
    private void ClearQueue()
    {
        Queue.ClearUpcoming();
        _main.ShowToast("File d'attente vidée");
    }

    [RelayCommand]
    private void RemoveQueueDuplicates()
    {
        var n = Queue.RemoveDuplicates();
        _main.ShowToast(n == 0 ? "Aucun doublon dans la file" : $"{n} doublon(s) retiré(s)");
    }

    [RelayCommand]
    private void SaveQueueAsPlaylist()
    {
        if (Queue.Count == 0) return;
        var playlist = Library.CreatePlaylist($"File du {DateTime.Now:dd/MM}", Queue.Items);
        _main.ShowToast($"Playlist « {playlist.Name} » créée ({playlist.TrackIds.Count} morceaux)");
    }

    // ---- External playback (blind test…) ------------------------------------------------------------------

    /// <summary>Another feature (e.g. the blind test) is driving the music player: the queue ignores player events.</summary>
    public bool IsExternalPlayback { get; private set; }

    /// <summary>Raised instead of the normal end-of-track handling while <see cref="IsExternalPlayback"/>.</summary>
    public event Action? ExternalTrackEnded;

    private double _externalResumeAt;
    private TrackItem? _externalResumeTrack;
    private bool _externalWasPlaying;

    /// <summary>
    /// Lends the music player (which goes into the microphone) to another feature. The current track is paused and
    /// remembered; call <see cref="EndExternalPlayback"/> to give it back.
    /// </summary>
    public MusicPlayer BeginExternalPlayback()
    {
        if (!IsExternalPlayback)
        {
            _playToken++;
            FinishHistoryEntry();
            _externalResumeTrack = CurrentTrack;
            _externalWasPlaying = Player.IsPlaying;
            _externalResumeAt = Player.HasTrack ? Player.Position.TotalSeconds : 0;
            Player.SetNext(null, 1f);
            _preparedNext = null;
            Player.Stop();
            IsPlaying = false;
            IsExternalPlayback = true;
        }
        return Player;
    }

    /// <summary>Gives the player back to the queue; resumes the interrupted track if it was playing.</summary>
    public void EndExternalPlayback()
    {
        if (!IsExternalPlayback) return;
        IsExternalPlayback = false;
        Player.Stop();
        if (_externalResumeTrack != null && _externalWasPlaying) _ = StartAsync(_externalResumeTrack, _externalResumeAt);
        else _resumePosition = _externalResumeAt;
        _externalResumeTrack = null;
    }

    // ---- Playback core -------------------------------------------------------------------------------------

    private double _resumePosition;

    /// <summary>Downloads the track if needed, then plays it. Stale requests (the user clicked elsewhere) are dropped.</summary>
    private async Task StartAsync(TrackItem track, double resumeAt = 0)
    {
        var token = ++_playToken;
        FinishHistoryEntry();
        SetCurrent(track);

        if (!track.IsDownloaded)
        {
            IsLoading = true;
            var ok = await EnsureLocalAsync(track);
            if (token != _playToken) return;
            IsLoading = false;
            if (!ok)
            {
                _main.ShowToast($"Lecture impossible de « {track.Title} » : {track.StateMessage}");
                if (++_consecutiveFailures < MaxConsecutiveFailures && Queue.Advance(auto: false) is { } skip && skip != track)
                    _ = StartAsync(skip);
                else
                    Stop();
                return;
            }
        }

        try
        {
            Player.Load(track.FilePath);
            if (resumeAt > 1) Player.Seek(TimeSpan.FromSeconds(resumeAt));
        }
        catch (Exception ex)
        {
            track.State = TrackState.Error;
            track.StateMessage = ex.Message;
            _main.ShowToast($"Lecture impossible de « {track.Title} » : {ex.Message}");
            if (++_consecutiveFailures < MaxConsecutiveFailures && Queue.Advance(auto: false) is { } skip && skip != track)
                _ = StartAsync(skip);
            return;
        }

        _consecutiveFailures = 0;
        _preparedNext = null;
        ApplyGain();
        OnStarted(track);
        SchedulePrepareNext();
    }

    /// <summary>Common bookkeeping once a track is actually audible.</summary>
    private void OnStarted(TrackItem track)
    {
        SetCurrent(track);
        IsLoading = false;
        IsPlaying = true;
        Duration = Math.Max(1, Player.Duration.TotalSeconds);
        DurationText = Format(Player.Duration);
        if (track.DurationSeconds is null or <= 0) track.DurationSeconds = Player.Duration.TotalSeconds;
        _currentEntry = Library.RecordPlay(track);
        _listenClock.Restart();
        NowPlayingChanged?.Invoke(track);
        MediaControls?.Update();
    }

    private void SetCurrent(TrackItem? track)
    {
        if (CurrentTrack == track) return;
        if (CurrentTrack != null) CurrentTrack.IsCurrent = false;
        CurrentTrack = track;
        if (track != null) track.IsCurrent = true;
    }

    private void OnTrackAdvanced(string path)
    {
        // The player crossfaded into the track we preloaded: move the queue along.
        FinishHistoryEntry();
        var expected = _preparedNext;
        _preparedNext = null;
        var next = Queue.Advance(auto: true);
        if (next == null || !string.Equals(next.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            // The queue changed between the preload and the transition: trust the queue.
            if (next != null) _ = StartAsync(next);
            else if (expected != null) OnStarted(expected);
            return;
        }
        ApplyGain();
        OnStarted(next);
        SchedulePrepareNext();
    }

    private void OnTrackEnded()
    {
        FinishHistoryEntry();
        IsPlaying = false;
        var next = Queue.Advance(auto: true);
        if (next != null) _ = StartAsync(next);
        else _ = AutoplayOrStopAsync();
    }

    private void OnNextTrackFailed(string path)
    {
        if (_preparedNext is { } t && string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            t.State = TrackState.Error;
            t.StateMessage = "Fichier illisible";
        }
        _preparedNext = null;
    }

    private void ApplyGain() => Player.Gain = _main.NormalizationGain(CurrentTrack?.NormalizationDb);

    private void SchedulePrepareNext()
    {
        OnPropertyChanged(nameof(UpNextCount));
        _prepareTimer.Stop();
        _prepareTimer.Start();
    }

    /// <summary>Makes sure the next track is on disk and hands it to the player for a seamless transition.</summary>
    private async Task PrepareNextAsync()
    {
        if (CurrentTrack == null || !Player.HasTrack) return;
        var next = Queue.PeekNext();
        if (next == null && Settings.Autoplay && Repeat == RepeatMode.Off && !_fetchingAutoplay)
        {
            // End of the queue in sight: fetch similar tracks now so the transition stays seamless.
            await AppendAutoplayAsync();
            next = Queue.PeekNext();
        }

        if (next == null)
        {
            _preparedNext = null;
            Player.SetNext(null, 1f);
            return;
        }
        if (next == _preparedNext && string.Equals(Player.NextFile, next.FilePath, StringComparison.OrdinalIgnoreCase)) return;

        if (!next.IsDownloaded && !await EnsureLocalAsync(next)) return;
        if (Queue.PeekNext() != next) return;
        _preparedNext = next;
        Player.SetNext(next.FilePath, _main.NormalizationGain(next.NormalizationDb));
    }

    private async Task AutoplayOrStopAsync()
    {
        if (Settings.Autoplay && await AppendAutoplayAsync() && Queue.Advance(auto: false) is { } next)
        {
            _ = StartAsync(next);
            return;
        }
        Stop();
        SetCurrent(null);
        NowPlayingChanged?.Invoke(null);
    }

    // ---- Downloads -------------------------------------------------------------------------------------------

    /// <summary>Makes the track playable (download, or search then download for imported tracks). Never throws.</summary>
    public async Task<bool> EnsureLocalAsync(TrackItem track)
    {
        if (track.IsDownloaded)
        {
            track.State = TrackState.Ready;
            return true;
        }
        if (_downloads.TryGetValue(track.Id, out var running)) return await running;
        var task = DownloadAsync(track);
        _downloads[track.Id] = task;
        try
        {
            return await task;
        }
        finally
        {
            _downloads.Remove(track.Id);
        }
    }

    private async Task<bool> DownloadAsync(TrackItem track)
    {
        track.State = TrackState.Downloading;
        track.StateMessage = "Recherche…";
        var progress = new Progress<DownloadProgress>(p =>
        {
            track.StateMessage = p.Percent > 0 ? $"Téléchargement {p.Percent:0} %" : "Téléchargement…";
            if (track == CurrentTrack) OnPropertyChanged(nameof(CurrentSubtitle));
        });
        try
        {
            if (string.IsNullOrEmpty(track.SourceUrl))
            {
                var found = await Resolver.ResolveAsync(track.Artist, track.DisplayTitle ?? track.SearchQuery ?? track.Title,
                    track.DurationSeconds is > 0 ? TimeSpan.FromSeconds(track.DurationSeconds.Value) : null, CancellationToken.None);
                if (found == null) throw new DownloadException("introuvable sur YouTube et SoundCloud");
                track.SourceUrl = found.PageUrl;
                track.ThumbnailUrl ??= found.ThumbnailUrl;
            }

            DownloadedMedia media;
            try
            {
                media = await Downloader.DownloadTrackAsync(track.SourceUrl!, DownloadFolder, progress, CancellationToken.None);
            }
            catch (DownloadException) when (track.SearchQuery != null || track.Artist != null)
            {
                // Blocked / removed video: look for the same song elsewhere.
                var failed = new SearchResult(track.Title, track.Artist ?? "", track.SourceUrl!, null,
                    track.DurationSeconds is > 0 ? TimeSpan.FromSeconds(track.DurationSeconds.Value) : null, track.ThumbnailUrl, SearchSource.YouTube, null);
                var alternative = await Resolver.FindAlternativeAsync(failed, CancellationToken.None)
                                  ?? throw new DownloadException("vidéo indisponible et aucune alternative trouvée");
                track.SourceUrl = alternative.PageUrl;
                media = await Downloader.DownloadTrackAsync(alternative.PageUrl, DownloadFolder, progress, CancellationToken.None);
            }

            track.FilePath = media.FilePath;
            if (string.IsNullOrWhiteSpace(track.DisplayTitle)) track.DisplayTitle = media.Title;
            track.Artist ??= media.Artist;
            track.ThumbnailUrl ??= media.ThumbnailUrl;
            if (track.DurationSeconds is null or <= 0) track.DurationSeconds = media.DurationSeconds;
            track.State = TrackState.Ready;
            track.StateMessage = null;
            AnalyzeLoudness(track);
            ScheduleLibrarySave();
            return true;
        }
        catch (Exception ex)
        {
            track.State = TrackState.Error;
            track.StateMessage = ex is DownloadException ? ex.Message : "erreur : " + ex.Message;
            return false;
        }
    }

    // ---- Loudness, history, persistence ------------------------------------------------------------------------

    public void AnalyzeMissingLoudness()
    {
        foreach (var track in Library.Tracks.Where(t => t.NormalizationDb == null && t.IsDownloaded).ToList())
            AnalyzeLoudness(track);
    }

    private void AnalyzeLoudness(TrackItem track)
    {
        if (track.NormalizationDb != null || !track.IsDownloaded) return;
        _main.Loudness.Enqueue(track.FilePath, db =>
        {
            track.NormalizationDb = db;
            if (track == CurrentTrack) ApplyGain();
            if (track == _preparedNext) _preparedNext = null;
            ScheduleLibrarySave();
            SchedulePrepareNext();
        });
    }

    private void FinishHistoryEntry()
    {
        if (_currentEntry == null) return;
        _listenClock.Stop();
        _currentEntry.ListenedSeconds += _listenClock.Elapsed.TotalSeconds * Math.Max(0.1, Settings.Effects.Enabled ? Settings.Effects.Speed : 1f);
        _listenClock.Reset();
        _currentEntry = null;
        ScheduleLibrarySave();
    }

    /// <summary>Called ~30 times a second by the main timer.</summary>
    public void RefreshPosition()
    {
        if (IsExternalPlayback) return;
        var playing = Player.IsPlaying;
        if (playing != IsPlaying)
        {
            IsPlaying = playing;
            if (playing) _listenClock.Start(); else _listenClock.Stop();
        }
        if (IsSeeking || !Player.HasTrack) return;
        var pos = Player.Position;
        Position = pos.TotalSeconds;
        PositionText = Format(pos);
        if (Math.Abs(Duration - Math.Max(1, Player.Duration.TotalSeconds)) > 0.5)
        {
            Duration = Math.Max(1, Player.Duration.TotalSeconds);
            DurationText = Format(Player.Duration);
        }
        Lyrics.Update(pos);
    }

    /// <summary>Restores the queue (and position) saved at the previous exit, without starting playback.</summary>
    public void RestoreState()
    {
        foreach (var t in Library.Tracks) t.IsCurrent = false;
        var tracks = Library.Data.SavedQueue.Select(Library.Find).OfType<TrackItem>().ToList();
        if (tracks.Count == 0) return;
        Queue.Restore(tracks, Library.Data.SavedQueueIndex);
        SetCurrent(Queue.Current);
        _resumePosition = Library.Data.SavedPositionSeconds;
        if (CurrentTrack != null)
        {
            Duration = Math.Max(1, CurrentTrack.DurationSeconds ?? 1);
            DurationText = CurrentTrack.DurationText;
            Position = _resumePosition;
            PositionText = Format(TimeSpan.FromSeconds(_resumePosition));
            NowPlayingChanged?.Invoke(CurrentTrack);
        }
    }

    private void ScheduleLibrarySave()
    {
        _librarySaveTimer.Stop();
        _librarySaveTimer.Start();
    }

    public void SaveLibrary()
    {
        try
        {
            var data = Library.Data;
            data.SavedQueue = Queue?.Items.Select(t => t.Id).ToList() ?? data.SavedQueue;
            data.SavedQueueIndex = Queue?.CurrentIndex ?? -1;
            data.SavedPositionSeconds = Player.HasTrack ? Player.Position.TotalSeconds : _resumePosition;
            // Queue entries must exist in the library to be restored.
            if (Queue != null)
                foreach (var t in Queue.Items) Library.AddOrGet(t);
            _libraryStore.Save(data);
        }
        catch (Exception ex)
        {
            _main.ShowToast("Impossible d'enregistrer la bibliothèque : " + ex.Message);
        }
    }

    public void Shutdown()
    {
        FinishHistoryEntry();
        Library.PruneTransient(60, Queue.Items.Select(t => t.Id).ToHashSet());
        SaveLibrary();
        MediaControls?.Dispose();
    }

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>Windows media overlay / media keys (set by the main window once it has a handle).</summary>
    public MediaControlsService? MediaControls { get; set; }
}
