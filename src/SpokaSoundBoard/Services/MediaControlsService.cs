using System.IO;
using System.Windows.Threading;
using SpokaSoundBoard.ViewModels.Music;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SpokaSoundBoard.Services;

/// <summary>
/// Windows media integration (SystemMediaTransportControls): the keyboard media keys, the Windows volume
/// flyout / lock screen card with title, artist and cover, and play / pause / next / previous from there.
/// A silent WinRT MediaPlayer is only used to obtain the controls for this desktop app.
/// </summary>
public sealed class MediaControlsService : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly SystemMediaTransportControls _smtc;
    private readonly MusicViewModel _music;
    private readonly Dispatcher _dispatcher;
    private string? _lastKey;

    public MediaControlsService(MusicViewModel music, Dispatcher dispatcher)
    {
        _music = music;
        _dispatcher = dispatcher;
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false;
        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += (_, e) =>
            _dispatcher.BeginInvoke(() => _music.SeekTo(e.RequestedPlaybackPosition.TotalSeconds));
        _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        Update();
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (!_music.IsPlaying) _music.PlayPause();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (_music.IsPlaying) _music.PlayPause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    _music.Next();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    _music.Previous();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    _music.Stop();
                    break;
            }
        });
    }

    /// <summary>Refreshes the state, the metadata (when the track changed) and the timeline. UI thread.</summary>
    public void Update()
    {
        try
        {
            var track = _music.CurrentTrack;
            _smtc.IsEnabled = track != null;
            _smtc.PlaybackStatus = track == null ? MediaPlaybackStatus.Closed
                : _music.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (track == null) return;

            var key = $"{track.Id}|{track.Title}|{track.Artist}|{track.ThumbnailUrl}";
            if (key != _lastKey)
            {
                _lastKey = key;
                var updater = _smtc.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = track.Title;
                updater.MusicProperties.Artist = track.Artist ?? track.Subtitle;
                updater.AppMediaId = "SpokaSoundBoard";
                updater.Thumbnail = CoverReference(track.ThumbnailUrl);
                updater.Update();
            }

            var timeline = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromSeconds(_music.Position),
                MaxSeekTime = TimeSpan.FromSeconds(_music.Duration),
                EndTime = TimeSpan.FromSeconds(_music.Duration),
            };
            _smtc.UpdateTimelineProperties(timeline);
        }
        catch
        {
            // Media controls are a nicety: never let them break playback.
        }
    }

    private static RandomAccessStreamReference? CoverReference(string? cover)
    {
        if (string.IsNullOrEmpty(cover)) return null;
        try
        {
            if (File.Exists(cover))
                return RandomAccessStreamReference.CreateFromFile(StorageFile.GetFileFromPathAsync(cover).AsTask().GetAwaiter().GetResult());
            if (Uri.TryCreate(cover, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http"))
                return RandomAccessStreamReference.CreateFromUri(uri);
        }
        catch
        {
            // Missing cover file.
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.IsEnabled = false;
            _player.Dispose();
        }
        catch
        {
            // Shutting down.
        }
    }
}
