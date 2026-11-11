using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;
using SpokaSoundBoard.Services;
using SpokaSoundBoard.ViewModels.Music;

namespace SpokaSoundBoard.ViewModels;

public enum BlindTestPhase
{
    Setup,
    Playing,
    Podium,
}

public enum BlindTestRoundState
{
    /// <summary>The song is being downloaded / opened.</summary>
    Loading,

    /// <summary>The excerpt plays, the countdown runs.</summary>
    Listening,

    /// <summary>End of the excerpt: waiting for the host to reveal.</summary>
    TimeUp,

    Revealed,
}

/// <summary>A song source offered on the setup screen.</summary>
public sealed record BlindTestSourceOption(BlindTestSource Kind, Guid? PlaylistId, string Label, string Icon, int Count)
{
    public string CountText => Kind == BlindTestSource.Theme ? "YouTube"
        : Count switch { 0 => "aucun morceau", 1 => "1 morceau", _ => $"{Count} morceaux" };
}

/// <summary>A player of the current game: committed score plus what they found in this round.</summary>
public sealed partial class BlindTestPlayer : ObservableObject
{
    public BlindTestPlayer(string name)
    {
        Name = name;
        _answer.PropertyChanged += OnAnswerPropertyChanged;
    }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Score))]
    private int _baseScore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Score))]
    private BlindTestAnswer _answer = new();

    [ObservableProperty] private int _rank = 1;

    public int Score => BaseScore + Answer.Points;

    /// <summary>Raised when the player's first find of the round is marked (title or artist).</summary>
    public event Action<BlindTestPlayer>? FirstFind;

    private bool _found;

    partial void OnAnswerChanged(BlindTestAnswer? oldValue, BlindTestAnswer newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnAnswerPropertyChanged;
        newValue.PropertyChanged += OnAnswerPropertyChanged;
        _found = newValue.Title || newValue.Artist;
    }

    private void OnAnswerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlindTestAnswer.Points)) OnPropertyChanged(nameof(Score));
        if (e.PropertyName is nameof(BlindTestAnswer.Title) or nameof(BlindTestAnswer.Artist))
        {
            var found = Answer.Title || Answer.Artist;
            var wasFound = _found;
            _found = found;
            if (found && !wasFound) FirstFind?.Invoke(this);
            // Nothing found any more: the speed bonus no longer applies either.
            else if (!found && wasFound) Answer.Fast = false;
        }
    }

    /// <summary>Adds this round's points to the score and starts a blank answer.</summary>
    public void CommitRound()
    {
        BaseScore += Answer.Points;
        Answer = new BlindTestAnswer();
    }

    public void ClearRound() => Answer = new BlindTestAnswer();
}

/// <summary>
/// « Blind test » page: the host plays song excerpts into the microphone (the music player is borrowed from the
/// music page), friends guess on Discord / in game, and the host scores each round here.
/// </summary>
public sealed partial class BlindTestViewModel : ObservableObject
{
    public const string PageName = "blindtest";

    private const double FadeInSeconds = 0.8;
    private const double FadeOutSeconds = 1.5;
    private const int MaxLoadAttempts = 4;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _excerptClock = new();
    private readonly Random _random = new();

    private MusicPlayer? _player;
    private List<TrackItem> _order = [];
    private int _nextIndex;
    private int _roundToken;
    private double _excerptStart;
    private bool _replayed;
    private bool _isLastRound;

    // Gain envelope (fade in / out, duck while the answer is announced), applied on every tick.
    private double _level;
    private double _fadeFrom;
    private double _fadeTo;
    private double _fadeStart;
    private double _fadeDuration;
    private bool _pauseAfterFade;
    private double _duck = 1;
    private double _duckUntil;

    // State saved when the user leaves the page during a game.
    private double _suspendedPosition;

    public BlindTestViewModel(MainViewModel main)
    {
        _main = main;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Stop();
        Music.ExternalTrackEnded += OnExternalTrackEnded;
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage)) OnPageChanged(main.CurrentPage);
        };
        // Natural voices downloaded or deleted while the page is open.
        Tts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextToSpeechViewModel.IsAvailable)) OnPropertyChanged(nameof(SpeechAvailable));
        };
        Settings.Players.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoPlayers));
            StartCommand.NotifyCanExecuteChanged();
        };
        RefreshRecords();
    }

    public MainViewModel Main => _main;
    private MusicViewModel Music => _main.Music;
    private TextToSpeechViewModel Tts => _main.Soundboard.Tts;
    public BlindTestSettings Settings => _main.Settings.BlindTest;
    public MixSettings Mix => _main.Mix;
    public bool SpeechAvailable => Tts.IsAvailable;

    // ---- Setup ---------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsPlaying), nameof(IsPodium))]
    private BlindTestPhase _phase = BlindTestPhase.Setup;

    public bool IsSetup => Phase == BlindTestPhase.Setup;
    public bool IsPlaying => Phase == BlindTestPhase.Playing;
    public bool IsPodium => Phase == BlindTestPhase.Podium;

    public ObservableCollection<BlindTestSourceOption> Sources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private BlindTestSourceOption? _selectedSource;

    partial void OnSelectedSourceChanged(BlindTestSourceOption? value)
    {
        if (value == null) return;
        Settings.Source = value.Kind;
        Settings.PlaylistId = value.PlaylistId;
        OnPropertyChanged(nameof(IsThemeSource));
    }

    /// <summary>The « thème » source is selected: its text box is shown.</summary>
    public bool IsThemeSource => SelectedSource?.Kind == BlindTestSource.Theme;

    [ObservableProperty] private bool _isPreparing;

    public string ThemeText
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme == value) return;
            Settings.Theme = value;
            OnPropertyChanged();
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Songs for a theme: a few YouTube searches, filtered down to single songs of normal length.</summary>
    private async Task<List<TrackItem>> ThemeCandidatesAsync(string theme)
    {
        var results = new List<SearchResult>();
        foreach (var query in BlindTestThemes.Queries(theme))
        {
            try
            {
                results.AddRange(await _main.Search.SearchAsync(SearchSource.YouTube, query, 25, CancellationToken.None));
            }
            catch (Exception ex) when (results.Count > 0)
            {
                // One search failing is fine if the others found songs.
                App.Log(ex);
            }
        }
        return BlindTestThemes.ToTracks(results);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPlayerCommand))]
    private string _newPlayerName = "";

    public bool HasNoPlayers => Settings.Players.Count == 0;

    /// <summary>Slider-friendly wrappers (the settings hold whole numbers).</summary>
    public double RoundsValue
    {
        get => Settings.Rounds;
        set
        {
            Settings.Rounds = Math.Clamp((int)Math.Round(value), BlindTestSettings.MinRounds, BlindTestSettings.MaxRounds);
            OnPropertyChanged();
        }
    }

    public double ExcerptValue
    {
        get => Settings.ExcerptSeconds;
        set
        {
            Settings.ExcerptSeconds = Math.Clamp((int)Math.Round(value), BlindTestSettings.MinExcerptSeconds, BlindTestSettings.MaxExcerptSeconds);
            OnPropertyChanged();
        }
    }

    /// <summary>All-time best players (top 10).</summary>
    public ObservableCollection<BlindTestRecord> Records { get; } = [];

    public bool HasRecords => Records.Count > 0;

    private bool CanAddPlayer() => BlindTestSettings.CleanName(NewPlayerName).Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddPlayer))]
    private void AddPlayer()
    {
        if (!Settings.AddPlayer(NewPlayerName))
        {
            _main.ShowToast(Settings.Players.Count >= BlindTestSettings.MaxPlayers
                ? $"{BlindTestSettings.MaxPlayers} joueurs au maximum."
                : "Ce joueur est déjà dans la liste.");
            return;
        }
        NewPlayerName = "";
        _main.Save();
    }

    [RelayCommand]
    private void RemovePlayer(string? name)
    {
        if (name != null && Settings.Players.Remove(name)) _main.Save();
    }

    [RelayCommand]
    private void ClearRecords()
    {
        if (System.Windows.MessageBox.Show("Effacer le classement de tous les temps du blind test ?", "Blind test",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;
        Settings.Records.Clear();
        RefreshRecords();
        _main.Save();
    }

    /// <summary>Rebuilds the source list (library, favourites, history, playlists) and reselects the saved one.</summary>
    public void RefreshSources()
    {
        var library = Music.Library;
        var options = new List<BlindTestSourceOption>
        {
            new(BlindTestSource.Library, null, "Toute ma bibliothèque", "\uE8D6", library.LibraryTracks.Count()),
            new(BlindTestSource.Favorites, null, "Mes favoris", "\uEB51", library.Favorites.Count()),
            new(BlindTestSource.History, null, "Mon historique d'écoute", "\uE81C", library.RecentlyPlayed(HistoryCount).Count),
        };
        options.AddRange(library.Playlists.Select(p => new BlindTestSourceOption(BlindTestSource.Playlist, p.Id, p.Name, "\uE8FD", library.TracksOf(p).Count)));
        // Always offered, even with an empty library: the songs come from YouTube.
        options.Add(new BlindTestSourceOption(BlindTestSource.Theme, null, "Un thème (années 80, rap français…)", "\uE721", 1));

        var kind = Settings.Source;
        var playlist = Settings.PlaylistId;
        Sources.Clear();
        foreach (var o in options) Sources.Add(o);
        SelectedSource = Sources.FirstOrDefault(o => o.Kind == kind && (kind != BlindTestSource.Playlist || o.PlaylistId == playlist))
                         ?? Sources.FirstOrDefault(o => o.Count > 0) ?? Sources[0];
    }

    private const int HistoryCount = 300;

    private List<TrackItem> Candidates(BlindTestSourceOption source)
    {
        var library = Music.Library;
        return source.Kind switch
        {
            BlindTestSource.Favorites => library.Favorites.ToList(),
            BlindTestSource.History => library.RecentlyPlayed(HistoryCount),
            BlindTestSource.Playlist => library.Playlists.FirstOrDefault(p => p.Id == source.PlaylistId) is { } p ? library.TracksOf(p) : [],
            _ => library.LibraryTracks.ToList(),
        };
    }

    private void RefreshRecords()
    {
        Records.Clear();
        foreach (var r in Settings.Records.Take(10)) Records.Add(r);
        OnPropertyChanged(nameof(HasRecords));
    }

    // ---- Game ----------------------------------------------------------------------------------------------

    public ObservableCollection<BlindTestPlayer> Players { get; } = [];

    /// <summary>Same players, best score first.</summary>
    public ObservableCollection<BlindTestPlayer> Leaderboard { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoundText), nameof(NextRoundText))]
    private int _roundNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoundText), nameof(NextRoundText))]
    private int _totalRounds;

    public string RoundText => $"Manche {RoundNumber} / {TotalRounds}";
    public string NextRoundText => RoundNumber >= TotalRounds ? "Voir le podium" : "Manche suivante";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsRevealed), nameof(CanReplay), nameof(IsListening))]
    private BlindTestRoundState _roundState;

    public bool IsLoading => RoundState == BlindTestRoundState.Loading;
    public bool IsListening => RoundState == BlindTestRoundState.Listening;
    public bool IsRevealed => RoundState == BlindTestRoundState.Revealed;
    public bool CanReplay => RoundState != BlindTestRoundState.Loading;

    /// <summary>Song of the round (shown only once revealed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnswerTitle), nameof(AnswerArtist))]
    private TrackItem? _currentTrack;

    public string AnswerTitle => BlindTestAnswerMatcher.StripDecorations(CurrentTrack?.Title);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HintText), nameof(HasHint), nameof(HintButtonText))]
    private int _hintLevel;

    partial void OnCurrentTrackChanged(TrackItem? value) => HintLevel = 0;

    public string HintText => CurrentTrack == null ? "" : BlindTestHints.For(AnswerTitle, CurrentTrack.Artist, HintLevel);
    public bool HasHint => HintLevel > 0;
    public string HintButtonText => HintLevel switch
    {
        0 => "Indice",
        < BlindTestHints.MaxLevel => "Indice suivant",
        _ => "Plus d'indice",
    };

    /// <summary>« Indice » : shape of the title, then its first letters, then the artist's initials.</summary>
    [RelayCommand]
    private void Hint()
    {
        if (CurrentTrack == null || IsRevealed || HintLevel >= BlindTestHints.MaxLevel) return;
        HintLevel++;
    }
    public string AnswerArtist => string.IsNullOrWhiteSpace(CurrentTrack?.Artist) ? "Artiste inconnu" : CurrentTrack!.Artist!;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _countdown;
    [ObservableProperty] private double _countdownProgress;
    [ObservableProperty] private bool _isPausedByHost;

    /// <summary>The user left the page during a game: the player was given back to the music page.</summary>
    [ObservableProperty] private bool _isSuspended;

    /// <summary>Optional typed guess (from the chat) checked against the hidden answer.</summary>
    [ObservableProperty] private string _guessText = "";
    [ObservableProperty] private string _guessResult = "";

    partial void OnGuessTextChanged(string value) => UpdateGuess();

    private void UpdateGuess()
    {
        if (string.IsNullOrWhiteSpace(GuessText) || CurrentTrack == null)
        {
            GuessResult = "";
            return;
        }
        var title = BlindTestAnswerMatcher.IsMatch(GuessText, CurrentTrack.Title);
        var artist = !string.IsNullOrWhiteSpace(CurrentTrack.Artist) && BlindTestAnswerMatcher.IsMatch(GuessText, CurrentTrack.Artist);
        GuessResult = (title, artist) switch
        {
            (true, true) => "Titre et artiste trouvés !",
            (true, false) => "Bon titre !",
            (false, true) => "Bon artiste !",
            _ => "Non, ce n'est pas ça.",
        };
    }

    private bool CanStart() => SelectedSource is { Count: > 0 } && Settings.Players.Count > 0 && !IsPreparing
                               && (SelectedSource.Kind != BlindTestSource.Theme || !string.IsNullOrWhiteSpace(Settings.Theme));

    partial void OnIsPreparingChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (SelectedSource == null) return;
        List<TrackItem> candidates;
        if (SelectedSource.Kind == BlindTestSource.Theme)
        {
            IsPreparing = true;
            try
            {
                candidates = await ThemeCandidatesAsync(Settings.Theme);
            }
            catch (Exception ex)
            {
                _main.ShowToast("Recherche du thème impossible : " + Core.FriendlyErrors.Describe(ex));
                return;
            }
            finally
            {
                IsPreparing = false;
            }
            if (candidates.Count < BlindTestSettings.MinRounds)
            {
                _main.ShowToast($"Trop peu de morceaux trouvés pour « {Settings.Theme.Trim()} » ({candidates.Count}). Essayez un thème plus large.");
                return;
            }
        }
        else
        {
            candidates = Candidates(SelectedSource);
        }
        _order = BlindTestPicker.Order(candidates, t => t.IsDownloaded, _random);
        if (_order.Count == 0)
        {
            _main.ShowToast("Aucun morceau jouable dans cette source.");
            return;
        }
        TotalRounds = Math.Min(Settings.Rounds, _order.Count);
        if (TotalRounds < Settings.Rounds)
            _main.ShowToast($"Seulement {_order.Count} morceaux disponibles : la partie aura {TotalRounds} manches.");
        _nextIndex = 0;
        RoundNumber = 0;

        foreach (var p in Players)
        {
            p.PropertyChanged -= OnPlayerChanged;
            p.FirstFind -= OnFirstFind;
        }
        Players.Clear();
        Leaderboard.Clear();
        foreach (var name in Settings.Players)
        {
            var player = new BlindTestPlayer(name);
            player.PropertyChanged += OnPlayerChanged;
            player.FirstFind += OnFirstFind;
            Players.Add(player);
            Leaderboard.Add(player);
        }
        UpdateRanks();

        _main.Save();
        IsSuspended = false;
        // A natural voice takes about a second to load: do it now rather than at the first reveal.
        if (Settings.AnnounceAnswer) Tts.WarmUp();
        Borrow();
        Phase = BlindTestPhase.Playing;
        NextRound();
    }

    /// <summary>Scores the round and moves on (or shows the podium after the last round).</summary>
    [RelayCommand]
    private void NextRound()
    {
        if (RoundNumber > 0)
            foreach (var p in Players) p.CommitRound();
        if (RoundNumber >= TotalRounds)
        {
            Finish();
            return;
        }
        RoundNumber++;
        _isLastRound = RoundNumber >= TotalRounds;
        _ = PlayRoundAsync(TakeNextTrack());
    }

    /// <summary>« Passer » : changes the song of this round (no points, the round is replayed with another song).</summary>
    [RelayCommand]
    private void Skip()
    {
        if (_nextIndex >= _order.Count)
        {
            _main.ShowToast("Plus d'autre morceau disponible pour remplacer celui-ci.");
            return;
        }
        foreach (var p in Players) p.ClearRound();
        // Replacing a song uses a spare: the game may end earlier if there are none left.
        TotalRounds = Math.Min(TotalRounds, RoundNumber + _order.Count - _nextIndex - 1);
        _isLastRound = RoundNumber >= TotalRounds;
        _ = PlayRoundAsync(TakeNextTrack());
    }

    [RelayCommand]
    private void Reveal()
    {
        if (CurrentTrack == null || RoundState is BlindTestRoundState.Loading or BlindTestRoundState.Revealed || _player == null) return;
        var wasPlaying = RoundState == BlindTestRoundState.Listening && !IsPausedByHost;
        RoundState = BlindTestRoundState.Revealed;
        IsPausedByHost = false;
        _excerptClock.Stop();
        StatusText = "Réponse";
        // Let the song go on so everyone enjoys it.
        _pauseAfterFade = false;
        if (!wasPlaying) _player.Play();
        FadeTo(1, FadeInSeconds);
        if (Settings.AnnounceAnswer) _ = AnnounceAsync(CurrentTrack);
    }

    /// <summary>Plays the excerpt again from its start (full countdown, no fast bonus).</summary>
    [RelayCommand]
    private void Replay()
    {
        if (_player == null || CurrentTrack == null || RoundState == BlindTestRoundState.Loading) return;
        IsPausedByHost = false;
        if (RoundState == BlindTestRoundState.Revealed)
        {
            _player.Seek(TimeSpan.FromSeconds(_excerptStart));
            _player.Play();
            FadeTo(1, FadeInSeconds);
            return;
        }
        _replayed = true;
        StartExcerpt();
    }

    /// <summary>A player buzzes: freeze the excerpt and the countdown while the host checks the answer.</summary>
    [RelayCommand]
    private void TogglePause()
    {
        if (_player == null || RoundState != BlindTestRoundState.Listening) return;
        IsPausedByHost = !IsPausedByHost;
        if (IsPausedByHost)
        {
            _excerptClock.Stop();
            _player.Pause();
            StatusText = "Pause : vérifiez la réponse";
        }
        else
        {
            _excerptClock.Start();
            _player.Play();
            StatusText = "Écoutez…";
        }
    }

    /// <summary>Found while the first listening is still within the fast window: the bonus is set automatically.</summary>
    private void OnFirstFind(BlindTestPlayer player)
    {
        if (!_replayed && RoundState == BlindTestRoundState.Listening && _excerptClock.Elapsed < BlindTestScoring.FastWindow)
            player.Answer.Fast = true;
    }

    /// <summary>Ends the game now (the points already given count) and shows the podium.</summary>
    [RelayCommand]
    private void EndGame()
    {
        foreach (var p in Players) p.CommitRound();
        Finish();
    }

    [RelayCommand]
    private void NewGame()
    {
        Phase = BlindTestPhase.Setup;
        RefreshSources();
    }

    [RelayCommand]
    private void PlayAgain()
    {
        RefreshSources();
        if (CanStart()) Start();
        else Phase = BlindTestPhase.Setup;
    }

    /// <summary>Back on the page after leaving it during a game: take the player again and go on.</summary>
    [RelayCommand]
    private void ResumeGame()
    {
        if (!IsSuspended || Phase != BlindTestPhase.Playing) return;
        IsSuspended = false;
        Borrow();
        var track = CurrentTrack;
        if (track == null || RoundState == BlindTestRoundState.Loading || !track.IsDownloaded)
        {
            _ = PlayRoundAsync(track ?? TakeNextTrack());
            return;
        }
        try
        {
            _player!.Load(track.FilePath, 0f, autoPlay: false);
            _player.Seek(TimeSpan.FromSeconds(_suspendedPosition));
        }
        catch (Exception ex)
        {
            _main.ShowToast("Impossible de reprendre l'extrait : " + Core.FriendlyErrors.Describe(ex));
            _ = PlayRoundAsync(TakeNextTrack());
            return;
        }
        switch (RoundState)
        {
            case BlindTestRoundState.Listening:
                StartExcerpt();
                break;
            case BlindTestRoundState.Revealed:
                _level = 0;
                _player.Play();
                FadeTo(1, FadeInSeconds);
                break;
        }
    }

    // ---- Round flow --------------------------------------------------------------------------------------------

    private TrackItem? TakeNextTrack() => _nextIndex < _order.Count ? _order[_nextIndex++] : null;

    private async Task PlayRoundAsync(TrackItem? track)
    {
        var token = ++_roundToken;
        IsPausedByHost = false;
        GuessText = "";
        _replayed = false;
        _excerptClock.Reset();
        if (_player != null && _player.HasTrack) _player.Pause();

        for (var attempt = 0; ; attempt++)
        {
            if (track == null)
            {
                _main.ShowToast("Plus aucun morceau disponible : fin de la partie.");
                EndGame();
                return;
            }
            CurrentTrack = track;
            RoundState = BlindTestRoundState.Loading;
            StatusText = track.IsDownloaded ? "Préparation de l'extrait…" : "Téléchargement de l'extrait…";
            Countdown = Settings.ExcerptSeconds;
            CountdownProgress = 1;

            var ok = await Music.EnsureLocalAsync(track);
            if (token != _roundToken || Phase != BlindTestPhase.Playing) return;
            if (ok && _player == null) return; // page left meanwhile: ResumeGame loads it
            if (ok && TryLoad(track)) break;

            if (attempt + 1 >= MaxLoadAttempts || _nextIndex >= _order.Count)
            {
                _main.ShowToast("Impossible de charger les morceaux (connexion ?) : fin de la partie.");
                EndGame();
                return;
            }
            _main.ShowToast("Un morceau n'a pas pu être chargé : on en prend un autre.");
            TotalRounds = Math.Min(TotalRounds, RoundNumber + _order.Count - _nextIndex - 1);
            _isLastRound = RoundNumber >= TotalRounds;
            track = TakeNextTrack();
        }

        PreloadNext();
        StartExcerpt();
    }

    private bool TryLoad(TrackItem track)
    {
        try
        {
            _player!.Load(track.FilePath, 0f, autoPlay: false);
            _excerptStart = BlindTestPicker.ExcerptStart(_player.Duration.TotalSeconds, Settings.ExcerptSeconds, _random);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    /// <summary>Downloads the next round's song now, so there is no wait between rounds.</summary>
    private void PreloadNext()
    {
        if (_isLastRound || _nextIndex >= _order.Count) return;
        var next = _order[_nextIndex];
        if (!next.IsDownloaded) _ = Music.EnsureLocalAsync(next);
    }

    private void StartExcerpt()
    {
        if (_player == null) return;
        _pauseAfterFade = false;
        _level = 0;
        ApplyGain();
        _player.Seek(TimeSpan.FromSeconds(_excerptStart));
        _player.Play();
        FadeTo(1, FadeInSeconds);
        _excerptClock.Restart();
        RoundState = BlindTestRoundState.Listening;
        StatusText = "Écoutez…";
        UpdateCountdown();
        _timer.Start();
    }

    private void OnExternalTrackEnded()
    {
        if (Phase == BlindTestPhase.Playing && RoundState == BlindTestRoundState.Listening) TimeUp();
    }

    private void TimeUp()
    {
        _excerptClock.Stop();
        _player?.Pause();
        RoundState = BlindTestRoundState.TimeUp;
        StatusText = "Temps écoulé !";
        Countdown = 0;
        CountdownProgress = 0;
    }

    private void Tick()
    {
        if (_player == null) return;
        var now = _clock.Elapsed.TotalSeconds;

        if (RoundState == BlindTestRoundState.Listening && !IsPausedByHost)
        {
            var remaining = Settings.ExcerptSeconds - _excerptClock.Elapsed.TotalSeconds;
            if (remaining <= FadeOutSeconds && !_pauseAfterFade && _fadeTo > 0)
            {
                FadeTo(0, Math.Max(0.1, remaining));
                _pauseAfterFade = true;
            }
            if (remaining <= 0) TimeUp();
            else UpdateCountdown();
        }

        if (_fadeDuration > 0)
        {
            var t = Math.Clamp((now - _fadeStart) / _fadeDuration, 0, 1);
            _level = _fadeFrom + (_fadeTo - _fadeFrom) * t;
            if (t >= 1)
            {
                _fadeDuration = 0;
                if (_pauseAfterFade && _fadeTo <= 0 && RoundState != BlindTestRoundState.Revealed) _player.Pause();
            }
        }
        if (_duck < 1 && now >= _duckUntil) _duck = Math.Min(1, _duck + 0.04);
        ApplyGain();
    }

    private void UpdateCountdown()
    {
        var remaining = Math.Max(0, Settings.ExcerptSeconds - _excerptClock.Elapsed.TotalSeconds);
        Countdown = (int)Math.Ceiling(remaining);
        CountdownProgress = remaining / Settings.ExcerptSeconds;
    }

    private void FadeTo(double level, double seconds)
    {
        _fadeFrom = _level;
        _fadeTo = level;
        _fadeStart = _clock.Elapsed.TotalSeconds;
        _fadeDuration = Math.Max(0.01, seconds);
        _timer.Start();
    }

    private void ApplyGain()
    {
        if (_player == null) return;
        var normalization = _main.NormalizationGain(CurrentTrack?.NormalizationDb);
        // Squared level: a fade that sounds even.
        _player.Gain = (float)(normalization * _level * _level * _duck);
    }

    /// <summary>« C'était … de … » in the microphone, the music lowered meanwhile.</summary>
    private async Task AnnounceAsync(TrackItem track)
    {
        if (!Tts.IsAvailable) return;
        var text = string.IsNullOrWhiteSpace(track.Artist)
            ? $"C'était {AnswerTitle}."
            : $"C'était {AnswerTitle}, de {track.Artist}.";
        _duck = 0.35;
        _duckUntil = double.MaxValue;
        var path = await Tts.SayAsync(text, remember: false);
        var seconds = path == null ? 0 : SpeechService.DurationOf(path).TotalSeconds;
        _duckUntil = _clock.Elapsed.TotalSeconds + seconds + 0.3;
    }

    private void Finish()
    {
        _roundToken++;
        _timer.Stop();
        Release();
        RoundState = BlindTestRoundState.Revealed;

        var results = Players.Select(p => (p.Name, p.Score)).ToList();
        if (RoundNumber > 0 && results.Count > 0)
        {
            Settings.RecordGame(results);
            RefreshRecords();
            _main.Save();
        }
        UpdateRanks();
        var ranked = Leaderboard.ToList();
        Podium1 = ranked.ElementAtOrDefault(0);
        Podium2 = ranked.ElementAtOrDefault(1);
        Podium3 = ranked.ElementAtOrDefault(2);
        OthersOnPodium.Clear();
        foreach (var p in ranked.Skip(3)) OthersOnPodium.Add(p);
        Phase = BlindTestPhase.Podium;
        IsSuspended = false;

        if (Settings.AnnounceAnswer && Podium1 is { Score: > 0 } winner && Tts.IsAvailable)
        {
            var tied = ranked.Where(p => p.Score == winner.Score).Select(p => p.Name).ToList();
            var text = tied.Count > 1
                ? $"Égalité entre {string.Join(" et ", tied)}, avec {winner.Score} points !"
                : $"Bravo {winner.Name}, tu remportes le blind test avec {winner.Score} points !";
            _ = Tts.SayAsync(text, remember: false);
        }
    }

    [ObservableProperty] private BlindTestPlayer? _podium1;
    [ObservableProperty] private BlindTestPlayer? _podium2;
    [ObservableProperty] private BlindTestPlayer? _podium3;

    public ObservableCollection<BlindTestPlayer> OthersOnPodium { get; } = [];

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlindTestPlayer.Score)) UpdateRanks();
    }

    /// <summary>Keeps <see cref="Leaderboard"/> sorted by score (stable) and the ranks up to date.</summary>
    private void UpdateRanks()
    {
        var sorted = Players.OrderByDescending(p => p.Score).ThenBy(p => Players.IndexOf(p)).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var from = Leaderboard.IndexOf(sorted[i]);
            if (from != i) Leaderboard.Move(from, i);
        }
        var ranks = BlindTestScoring.Ranks(sorted.Select(p => p.Score).ToList());
        for (var i = 0; i < sorted.Count; i++) sorted[i].Rank = ranks[i];
    }

    // ---- Player borrowing / navigation -------------------------------------------------------------------------

    private void Borrow()
    {
        _player ??= Music.BeginExternalPlayback();
    }

    private void Release()
    {
        if (_player == null) return;
        _player.Stop();
        _player = null;
        Music.EndExternalPlayback();
    }

    private void OnPageChanged(string page)
    {
        if (page == PageName)
        {
            if (Phase == BlindTestPhase.Setup) RefreshSources();
            return;
        }
        if (Phase != BlindTestPhase.Playing || _player == null) return;

        // Never keep the player while the user is elsewhere: pause the game and give it back.
        _suspendedPosition = _player.HasTrack ? _player.Position.TotalSeconds : _excerptStart;
        _timer.Stop();
        _excerptClock.Stop();
        IsPausedByHost = false;
        Release();
        IsSuspended = true;
        StatusText = "Partie en pause";
    }

    /// <summary>App exit: stop the excerpt and hand the player back without resuming anything.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _roundToken++;
        _player?.Stop();
    }
}
