using System.IO;
using System.Threading.Channels;
using System.Windows;
using SpokaSoundBoard.Core.Models;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace SpokaSoundBoard.Services;

/// <summary>
/// Reads title / artist / duration and the embedded cover of local audio files through the Windows property system
/// (no extra dependency), one file at a time in the background.
/// </summary>
public sealed class LocalMetadataService
{
    private readonly Channel<TrackItem> _queue = Channel.CreateUnbounded<TrackItem>();
    private readonly string _coverDirectory;
    private readonly Action _changed;

    public LocalMetadataService(string coverDirectory, Action changed)
    {
        _coverDirectory = coverDirectory;
        _changed = changed;
        _ = Task.Run(WorkAsync);
    }

    public void Enqueue(TrackItem track) => _queue.Writer.TryWrite(track);

    private async Task WorkAsync()
    {
        await foreach (var track in _queue.Reader.ReadAllAsync())
        {
            try
            {
                var result = await ReadAsync(track.FilePath, track.Id);
                if (result == null) continue;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!string.IsNullOrWhiteSpace(result.Value.Title) && string.IsNullOrWhiteSpace(track.DisplayTitle))
                        track.DisplayTitle = result.Value.Title;
                    if (!string.IsNullOrWhiteSpace(result.Value.Artist)) track.Artist ??= result.Value.Artist;
                    if (result.Value.Duration > 0 && track.DurationSeconds is null or <= 0) track.DurationSeconds = result.Value.Duration;
                    if (result.Value.Cover != null) track.ThumbnailUrl ??= result.Value.Cover;
                    _changed();
                });
            }
            catch
            {
                // Unreadable tags: keep the file name.
            }
        }
    }

    private async Task<(string? Title, string? Artist, double Duration, string? Cover)?> ReadAsync(string path, Guid id)
    {
        if (!File.Exists(path)) return null;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.GetMusicPropertiesAsync();
        var artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist : props.AlbumArtist;

        string? cover = null;
        try
        {
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 256, ThumbnailOptions.UseCurrentScale);
            // Type Image = real embedded cover; Icon = generic file icon (ignored).
            if (thumb is { Type: ThumbnailType.Image, Size: > 0 })
            {
                Directory.CreateDirectory(_coverDirectory);
                cover = Path.Combine(_coverDirectory, id.ToString("N") + ".jpg");
                await using var output = File.Create(cover);
                await thumb.AsStreamForRead().CopyToAsync(output);
            }
        }
        catch
        {
            cover = null;
        }

        return (props.Title, artist, props.Duration.TotalSeconds, cover);
    }
}
