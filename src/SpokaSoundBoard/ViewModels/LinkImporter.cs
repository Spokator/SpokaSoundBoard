using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels;

/// <summary>"Paste a link" bar shared by the soundboard and the music pages.</summary>
public partial class LinkImporter : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string _subFolder;
    private readonly Action<DownloadedMedia> _onItem;
    private CancellationTokenSource? _cts;

    public LinkImporter(MainViewModel main, string subFolder, bool supportsPlaylist, string placeholder, Action<DownloadedMedia> onItem)
    {
        _main = main;
        _subFolder = subFolder;
        _onItem = onItem;
        SupportsPlaylist = supportsPlaylist;
        Placeholder = placeholder;
    }

    public bool SupportsPlaylist { get; }
    public string Placeholder { get; }

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private string _url = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _importPlaylist;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _hasError;

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen && string.IsNullOrWhiteSpace(Url))
        {
            // Pre-fill with a link from the clipboard, the most common way to share one.
            try
            {
                var clip = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : null;
                if (MediaDownloader.LooksLikeUrl(clip)) Url = clip!;
            }
            catch
            {
                // Clipboard can be locked by another app.
            }
        }
    }

    private bool CanImport() => !IsBusy && !string.IsNullOrWhiteSpace(Url);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task Import() => ImportAsync(Url);

    public async Task ImportAsync(string url)
    {
        if (IsBusy) return;
        IsOpen = true;
        Url = url.Trim();
        IsBusy = true;
        HasError = false;
        IsIndeterminate = true;
        Progress = 0;
        Status = "Préparation…";
        _cts = new CancellationTokenSource();
        var count = 0;

        var progress = new Progress<DownloadProgress>(p =>
        {
            IsIndeterminate = p.Percent <= 0;
            Progress = p.Percent;
            Status = p.Message;
        });

        try
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var items = await _main.Downloader.DownloadAsync(Url, _subFolder, SupportsPlaylist && ImportPlaylist,
                item => dispatcher.BeginInvoke(() =>
                {
                    count++;
                    _onItem(item);
                }),
                progress, _cts.Token);

            Status = items.Count == 1 ? $"Ajouté : {items[0].Title}" : $"{items.Count} éléments ajoutés";
            Url = "";
            _main.Save();
        }
        catch (OperationCanceledException)
        {
            Status = count > 0 ? $"Annulé ({count} élément(s) déjà ajouté(s))" : "Annulé";
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = ex is DownloadException ? ex.Message : "Erreur : " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private static void OpenSource(string? url)
    {
        if (MediaDownloader.LooksLikeUrl(url))
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
    }
}
