using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels;

public partial class SearchResultItem(SearchResult result) : ObservableObject
{
    public SearchResult Result { get; } = result;
    public string Title => Result.Title;
    public string? ThumbnailUrl => Result.ThumbnailUrl;
    public bool HasThumbnail => !string.IsNullOrEmpty(Result.ThumbnailUrl);
    public string Color => Result.Color ?? "#7C5CFF";

    public string Details => Result.Duration is { } d
        ? $"{Result.Subtitle} · {(d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : $"{(int)d.TotalMinutes}:{d.Seconds:00}")}"
        : Result.Subtitle;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAdded;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _isPreviewLoading;

    /// <summary>File downloaded for the preview, reused when the result is added.</summary>
    public string? PreviewFile { get; set; }
}

public sealed record SourceOption(SearchSource Source, string Label, string Icon);

/// <summary>"Search or paste a link" bar shared by the soundboard and the music pages.</summary>
public partial class LinkImporter : ObservableObject
{
    private const string PreviewFolder = "Apercus";
    private readonly MainViewModel _main;
    private readonly string _subFolder;
    private readonly Action<DownloadedMedia> _onItem;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _searchCts;
    private SearchResultItem? _previewItem;

    public LinkImporter(MainViewModel main, string subFolder, bool supportsPlaylist, string placeholder,
        IReadOnlyList<SourceOption> sources, Action<DownloadedMedia> onItem)
    {
        _main = main;
        _subFolder = subFolder;
        _onItem = onItem;
        SupportsPlaylist = supportsPlaylist;
        Placeholder = placeholder;
        Sources = sources;
        _selectedSource = sources[0];
        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(CanClear));
        };
        main.Engine.Preview.TrackEnded += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_previewItem != null) _previewItem.IsPreviewing = false;
            _previewItem = null;
        });
    }

    public bool SupportsPlaylist { get; }
    public string Placeholder { get; }
    public IReadOnlyList<SourceOption> Sources { get; }
    public bool HasSeveralSources => Sources.Count > 1;
    public ObservableCollection<SearchResultItem> Results { get; } = [];
    public bool HasResults => Results.Count > 0;

    [RelayCommand]
    private void SelectSource(SourceOption? option)
    {
        if (option != null) SelectedSource = option;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(IsUrl), nameof(ShowPlaylistOption), nameof(SubmitLabel), nameof(SubmitIcon), nameof(CanClear))]
    private string _url = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    private bool _isBusy;

    [ObservableProperty] private SourceOption _selectedSource;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _importPlaylist;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClear))]
    private string? _status;
    [ObservableProperty] private bool _hasError;

    public bool IsUrl => MediaDownloader.LooksLikeUrl(Url);
    public bool ShowPlaylistOption => SupportsPlaylist && IsUrl;
    public string SubmitLabel => IsUrl ? "Importer" : "Rechercher";
    public string SubmitIcon => IsUrl ? "\uE896" : "\uE721";

    /// <summary>The search bar is always visible: "✕" only appears when there is something to clear.</summary>
    public bool CanClear => !IsBusy && (HasResults || !string.IsNullOrEmpty(Status) || !string.IsNullOrWhiteSpace(Url));

    /// <summary>Empties the search field and its results (an import in progress has its own "Annuler").</summary>
    [RelayCommand]
    private void Clear()
    {
        if (IsBusy) return;
        _searchCts?.Cancel();
        StopPreview();
        Results.Clear();
        Url = "";
        Status = null;
        HasError = false;
        IsSearching = false;
    }

    private bool CanSubmit() => !IsBusy && !string.IsNullOrWhiteSpace(Url);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task Submit() => IsUrl ? ImportAsync(Url) : SearchAsync(Url);

    partial void OnSelectedSourceChanged(SourceOption value)
    {
        if (!IsUrl && !string.IsNullOrWhiteSpace(Url) && Results.Count > 0) _ = SearchAsync(Url);
    }

    public async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        IsSearching = true;
        HasError = false;
        Status = $"Recherche de « {query.Trim()} »…";
        StopPreview();
        Results.Clear();
        try
        {
            var results = await _main.Search.SearchAsync(SelectedSource.Source, query, SelectedSource.Source == SearchSource.MyInstants ? 24 : 10, ct);
            if (ct.IsCancellationRequested) return;
            foreach (var r in results) Results.Add(new SearchResultItem(r));
            Status = results.Count == 0
                ? "Aucun résultat. Essayez d'autres mots" + (HasSeveralSources ? " ou une autre source." : ".")
                : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = ex is DownloadException ? ex.Message : "Recherche impossible : " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task AddResult(SearchResultItem? item)
    {
        if (item == null || item.IsBusy || item.IsAdded) return;
        item.IsBusy = true;
        HasError = false;
        try
        {
            if (_previewItem == item) StopPreview();
            DownloadedMedia media;
            if (item.PreviewFile != null && File.Exists(item.PreviewFile))
            {
                media = new DownloadedMedia(MoveFromPreview(item.PreviewFile), item.Title, item.Result.PageUrl);
            }
            else if (item.Result.DirectAudioUrl != null)
            {
                media = await _main.Downloader.DownloadDirectAsync(item.Result.DirectAudioUrl, item.Title, item.Result.PageUrl, _subFolder, null, CancellationToken.None);
            }
            else
            {
                var items = await _main.Downloader.DownloadAsync(item.Result.PageUrl, _subFolder, false, null, null, CancellationToken.None);
                media = items[0] with { Title = item.Title };
            }

            _onItem(media);
            item.IsAdded = true;
            _main.Save();
            _main.ShowToast($"Ajouté : {item.Title}");
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = $"« {item.Title} » : " + (ex is DownloadException ? ex.Message : ex.Message);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PreviewResult(SearchResultItem? item)
    {
        if (item == null) return;
        if (item.IsPreviewing)
        {
            StopPreview();
            return;
        }
        StopPreview();
        if (_main.Settings.MonitorDeviceId == null)
        {
            _main.ShowToast("Choisissez un « Retour casque » dans Configuration pour écouter les aperçus.");
            return;
        }

        item.IsPreviewLoading = true;
        try
        {
            if (item.PreviewFile == null || !File.Exists(item.PreviewFile))
            {
                var media = item.Result.DirectAudioUrl != null
                    ? await _main.Downloader.DownloadDirectAsync(item.Result.DirectAudioUrl, item.Title, item.Result.PageUrl, PreviewFolder, null, CancellationToken.None)
                    : (await _main.Downloader.DownloadAsync(item.Result.PageUrl, PreviewFolder, false, null, null, CancellationToken.None))[0];
                item.PreviewFile = media.FilePath;
            }

            // Another preview may have been requested while this one was downloading.
            if (_previewItem != null) StopPreview();
            _main.Engine.Preview.Load(item.PreviewFile);
            _previewItem = item;
            item.IsPreviewing = true;
        }
        catch (Exception ex)
        {
            HasError = true;
            Status = "Aperçu impossible : " + ex.Message;
        }
        finally
        {
            item.IsPreviewLoading = false;
        }
    }

    [RelayCommand]
    private static void OpenResult(SearchResultItem? item)
    {
        if (item != null) OpenSource(item.Result.PageUrl);
    }

    public void StopPreview()
    {
        if (_previewItem == null) return;
        _main.Engine.Preview.Stop();
        _previewItem.IsPreviewing = false;
        _previewItem = null;
    }

    private string MoveFromPreview(string previewFile)
    {
        var dir = Path.Combine(_main.Downloader.CacheDirectory, _subFolder);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(previewFile));
        if (!File.Exists(target)) File.Move(previewFile, target);
        return target;
    }

    /// <summary>Removes preview files left from previous sessions.</summary>
    public static void CleanPreviewCache(MediaDownloader downloader)
    {
        try
        {
            var dir = Path.Combine(downloader.CacheDirectory, PreviewFolder);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // Files in use: they will be cleaned next time.
        }
    }

    public async Task ImportAsync(string url)
    {
        if (IsBusy) return;
        Url = url.Trim();
        IsBusy = true;
        HasError = false;
        IsIndeterminate = true;
        Progress = 0;
        Status = "Préparation…";
        Results.Clear();
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
