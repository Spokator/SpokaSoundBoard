using System.Collections.ObjectModel;
using System.Windows.Data;
using SpokaSoundBoard.Core;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core.Audio;
using SpokaSoundBoard.Core.Models;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels;

public sealed record PlayModeOption(PadPlayMode Mode, string Label);

/// <summary>One soundboard tab: <see cref="Key"/> is the category ("" for « Tous »).</summary>
public sealed record PadTab(string Key, string Label, int Count)
{
    // Read by screen readers and UI Automation on the list item.
    public override string ToString() => Label;
}

public partial class SoundboardViewModel : ObservableObject
{
    public static readonly string[] Palette =
        ["#7C5CFF", "#3B82F6", "#06B6D4", "#10B981", "#84CC16", "#F59E0B", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#64748B"];

    private readonly MainViewModel _main;

    public SoundboardViewModel(MainViewModel main)
    {
        _main = main;
        Link = new LinkImporter(main, "Sons", false,
            "Rechercher un son (ex. « vine boom », « sirène ») ou coller un lien…",
            [new(SearchSource.MyInstants, "Bruitages (MyInstants)", "\uE8A9"), new(SearchSource.YouTube, "YouTube", "\uE714")],
            AddFromLink);
        Tts = new TextToSpeechViewModel(main, this);
        PadsView = CollectionViewSource.GetDefaultView(Pads);
        PadsView.Filter = o => o is SoundPad p
                               && (SelectedCategory.Length == 0 || string.Equals(p.Category, SelectedCategory, StringComparison.CurrentCultureIgnoreCase))
                               && (string.IsNullOrWhiteSpace(Filter) || p.Name.Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
        foreach (var pad in Pads) pad.PropertyChanged += OnPadChanged;
        main.Settings.Appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSettings.ShowPadHotkeys)) OnPropertyChanged(nameof(ShowHotkeys));
        };
        Pads.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (SoundPad pad in e.NewItems) pad.PropertyChanged += OnPadChanged;
            if (e.OldItems != null)
                foreach (SoundPad pad in e.OldItems) pad.PropertyChanged -= OnPadChanged;
            OnPropertyChanged(nameof(IsEmpty));
            RebuildTabs();
        };
        RebuildTabs();
        // The remembered tab was deleted or renamed elsewhere: show everything.
        if (SelectedCategory.Length > 0 && !Categories.Contains(SelectedCategory)) Appearance.PadCategory = "";
    }

    // ---- Tabs (categories) ----------------------------------------------------------------------------------

    private List<string> Categories => _main.Settings.PadCategories;

    /// <summary>« Tous » then one tab per category, with the number of sounds in each.</summary>
    public ObservableCollection<PadTab> Tabs { get; } = [];

    /// <summary>Category names for the pad editor's list.</summary>
    public ObservableCollection<string> CategoryNames { get; } = [];

    /// <summary>Tab shown ("" = « Tous »). New sounds go into it.</summary>
    public string SelectedCategory => Appearance.PadCategory;

    /// <summary>The tab shown has no sound yet (the soundboard itself is not empty).</summary>
    public bool CategoryIsEmpty => Pads.Count > 0 && SelectedCategory.Length > 0
                                   && !Pads.Any(p => string.Equals(p.Category, SelectedCategory, StringComparison.CurrentCultureIgnoreCase));

    private void RebuildTabs()
    {
        if (PadCategories.Merge(Categories, Pads)) _main.Save();
        var counts = Categories.ToDictionary(c => c, _ => 0, StringComparer.CurrentCultureIgnoreCase);
        foreach (var pad in Pads)
            if (pad.Category.Length > 0 && counts.ContainsKey(pad.Category)) counts[pad.Category]++;
        var tabs = new List<PadTab> { new("", "Tous", Pads.Count) };
        tabs.AddRange(Categories.Select(c => new PadTab(c, c, counts[c])));
        // Update in place: only the tabs that changed get a new chip (keeps focus and screen-reader names stable).
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i >= Tabs.Count) Tabs.Add(tabs[i]);
            else if (Tabs[i] != tabs[i]) Tabs[i] = tabs[i];
        }
        while (Tabs.Count > tabs.Count) Tabs.RemoveAt(Tabs.Count - 1);
        OnPropertyChanged(nameof(CategoryIsEmpty));
        if (!Categories.SequenceEqual(CategoryNames))
        {
            CategoryNames.Clear();
            foreach (var c in Categories) CategoryNames.Add(c);
        }
    }

    [RelayCommand]
    private void SelectCategory(string? key)
    {
        Appearance.PadCategory = key ?? "";
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(CategoryIsEmpty));
        PadsView.Refresh();
        _main.Save();
    }

    [ObservableProperty] private bool _isEditingCategory;
    [ObservableProperty] private string _categoryDraft = "";
    private string? _renamingCategory;

    /// <summary>Title of the category name box: « Nouvelle catégorie » or « Renommer … ».</summary>
    public string CategoryEditTitle => _renamingCategory == null ? "Nouvelle catégorie" : $"Renommer « {_renamingCategory} »";

    private static bool IsCategory(string? key) => !string.IsNullOrEmpty(key);

    [RelayCommand]
    private void NewCategory()
    {
        _renamingCategory = null;
        CategoryDraft = "";
        OnPropertyChanged(nameof(CategoryEditTitle));
        IsEditingCategory = true;
    }

    [RelayCommand(CanExecute = nameof(IsCategory))]
    private void RenameCategory(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        _renamingCategory = key;
        CategoryDraft = key;
        OnPropertyChanged(nameof(CategoryEditTitle));
        IsEditingCategory = true;
    }

    [RelayCommand]
    private void ConfirmCategory()
    {
        var name = PadCategories.Clean(CategoryDraft);
        IsEditingCategory = false;
        if (name.Length == 0) return;
        if (_renamingCategory != null)
        {
            var wasSelected = string.Equals(SelectedCategory, _renamingCategory, StringComparison.CurrentCultureIgnoreCase);
            PadCategories.Rename(Categories, Pads, _renamingCategory, name);
            if (wasSelected) Appearance.PadCategory = PadCategories.Canonical(Categories, name);
        }
        else
        {
            name = PadCategories.Canonical(Categories, name);
            if (!Categories.Contains(name)) Categories.Add(name);
            Appearance.PadCategory = name;
        }
        _renamingCategory = null;
        OnPropertyChanged(nameof(SelectedCategory));
        RebuildTabs();
        PadsView.Refresh();
        _main.Save();
    }

    [RelayCommand]
    private void CancelCategory()
    {
        IsEditingCategory = false;
        _renamingCategory = null;
    }

    [RelayCommand(CanExecute = nameof(IsCategory))]
    private void DeleteCategory(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        PadCategories.Remove(Categories, Pads, key);
        if (string.Equals(SelectedCategory, key, StringComparison.CurrentCultureIgnoreCase)) Appearance.PadCategory = "";
        OnPropertyChanged(nameof(SelectedCategory));
        RebuildTabs();
        PadsView.Refresh();
        _main.Save();
        _main.ShowToast($"Catégorie « {key} » supprimée : ses sons restent dans « Tous ».");
    }

    /// <summary>Drag-and-drop of a sound onto a tab.</summary>
    public void MoveToCategory(SoundPad pad, string key)
    {
        if (string.Equals(pad.Category, key, StringComparison.CurrentCultureIgnoreCase)) return;
        pad.Category = key;
        _main.ShowToast(key.Length == 0 ? $"« {pad.Name} » n'a plus de catégorie." : $"« {pad.Name} » rangé dans « {key} ».");
        _main.Save();
    }

    /// <summary>Drag-and-drop reordering: <paramref name="pad"/> takes the place of <paramref name="target"/>.</summary>
    public void MovePad(SoundPad pad, SoundPad target)
    {
        var from = Pads.IndexOf(pad);
        var to = Pads.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Pads.Move(from, to);
        _main.Save();
    }

    public ObservableCollection<SoundPad> Pads => _main.Settings.Pads;

    public LinkImporter Link { get; }

    /// <summary>« Synthèse vocale » page (its own menu entry, it shares the soundboard to save phrases as sounds).</summary>
    public TextToSpeechViewModel Tts { get; }

    /// <summary>Appearance settings, for the folding state of « Trouver des sons ».</summary>
    public AppearanceSettings Appearance => _main.Settings.Appearance;

    /// <summary>Folds or unfolds « Trouver des sons » (remembered), to keep the pads in view.</summary>
    [RelayCommand]
    private void ToggleSoundSearch()
    {
        Appearance.SoundSearchCollapsed = !Appearance.SoundSearchCollapsed;
        _main.Save();
    }

    /// <summary>A link dropped on the page imports into the search card: make sure its progress is visible.</summary>
    public void ShowSoundSearch()
    {
        if (!Appearance.SoundSearchCollapsed) return;
        Appearance.SoundSearchCollapsed = false;
        _main.Save();
    }

    public bool ShowHotkeys => _main.Settings.Appearance.ShowPadHotkeys;

    public IReadOnlyList<string> Colors => Palette;

    /// <summary>Icons for the phone remote, « automatique » first.</summary>
    public IReadOnlyList<PadIcon> IconChoices { get; } = [new("", "Automatique (d'après le nom)"), .. PadIcons.All];

    public IReadOnlyList<PlayModeOption> PlayModes { get; } =
    [
        new(PadPlayMode.Toggle, "L'arrêter"),
        new(PadPlayMode.Restart, "Le relancer depuis le début"),
        new(PadPlayMode.Overlap, "Le jouer une fois de plus (superposé)"),
    ];

    public bool IsEmpty => Pads.Count == 0;

    /// <summary>Pads filtered by the search box.</summary>
    public ICollectionView PadsView { get; }

    [ObservableProperty] private string _filter = "";

    partial void OnFilterChanged(string value) => PadsView.Refresh();

    public void AnalyzeMissingLoudness()
    {
        foreach (var pad in Pads.Where(p => p.NormalizationDb == null && File.Exists(p.FilePath)).ToList())
            Analyze(pad);
    }

    private void Analyze(SoundPad pad) => _main.Loudness.Enqueue(pad.FilePath, db =>
    {
        pad.NormalizationDb = db;
        _main.Save();
    });

    private float EffectiveVolume(SoundPad pad) => pad.Volume * _main.NormalizationGain(pad.NormalizationDb);

    [RelayCommand]
    private void ExportPack()
    {
        if (Pads.Count == 0)
        {
            _main.ShowToast("Ajoutez d'abord des sons à votre soundboard.");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exporter ma soundboard",
            Filter = SoundboardPack.DialogFilter,
            FileName = "Ma soundboard" + SoundboardPack.Extension,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var count = SoundboardPack.Export(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), Pads, _main.Settings.CustomPresets);
            _main.ShowToast($"Pack exporté : {count} sons. Envoyez le fichier à vos amis !");
        }
        catch (Exception ex)
        {
            _main.ShowToast("Export impossible : " + Core.FriendlyErrors.Describe(ex));
        }
    }

    [RelayCommand]
    private void ImportPack()
    {
        var dialog = new OpenFileDialog { Title = "Importer une soundboard", Filter = SoundboardPack.DialogFilter };
        if (dialog.ShowDialog() == true) ImportPackFile(dialog.FileName);
    }

    public void ImportPackFile(string path)
    {
        try
        {
            var library = Path.Combine(_main.Downloader.CacheDirectory, "Packs");
            var (pads, presets) = SoundboardPack.Import(path, library);
            var known = Pads.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var pad in pads)
            {
                // Importing the same pack twice must not duplicate its sounds.
                if (!known.Add(pad.FilePath)) continue;
                Pads.Add(pad);
                added++;
                if (pad.NormalizationDb == null) Analyze(pad);
            }
            var skipped = pads.Count - added;
            var addedPresets = _main.Voice.AddImportedPresets(presets);
            _main.Save();
            _main.ShowToast($"Pack importé : {added} sons" + (addedPresets > 0 ? $" et {addedPresets} effets de voix" : "") +
                            (skipped > 0 ? $" ({skipped} déjà présents)." : "."));
        }
        catch (Exception ex)
        {
            _main.ShowToast("Import impossible : " + Core.FriendlyErrors.Describe(ex));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SoundPad? _selectedPad;

    public bool HasSelection => SelectedPad != null;

    [RelayCommand]
    private void AddSounds()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ajouter des sons",
            Filter = AudioFileLoader.DialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        var list = paths.ToList();
        foreach (var pack in list.Where(p => p.EndsWith(SoundboardPack.Extension, StringComparison.OrdinalIgnoreCase)))
            ImportPackFile(pack);
        if (list.All(p => p.EndsWith(SoundboardPack.Extension, StringComparison.OrdinalIgnoreCase))) return;
        foreach (var path in list.Where(AudioFileLoader.IsSupported))
        {
            var newPad = new SoundPad
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path,
                Color = Palette[Pads.Count % Palette.Length],
                Category = SelectedCategory,
            };
            Pads.Add(newPad);
            Analyze(newPad);
            added++;
        }
        if (added > 0)
        {
            SelectedPad = Pads[^1];
            _main.Save();
        }
        else
        {
            _main.ShowToast("Aucun fichier audio compatible.");
        }
    }

    private void AddFromLink(DownloadedMedia media)
    {
        var pad = new SoundPad
        {
            Name = media.Title.Length > 40 ? media.Title[..40].TrimEnd() + "…" : media.Title,
            FilePath = media.FilePath,
            SourceUrl = media.SourceUrl,
            Color = Palette[Pads.Count % Palette.Length],
            Category = SelectedCategory,
        };
        Pads.Add(pad);
        Analyze(pad);
        SelectedPad = pad;
    }

    [RelayCommand]
    private async Task PlayPad(SoundPad? pad)
    {
        if (pad == null) return;
        var player = PlayerFor(pad);

        // A second press while the file is still opening must not start it twice.
        if (!_opening.Add(pad.Id)) return;
        try
        {
            await PlayOrStopAsync(pad, player);
        }
        finally
        {
            _opening.Remove(pad.Id);
        }
    }

    private readonly HashSet<Guid> _opening = [];

    private SoundPadPlayer PlayerFor(SoundPad pad) => pad.HeadphonesOnly ? _main.Engine.PrivatePads : _main.Engine.Pads;

    private async Task PlayOrStopAsync(SoundPad pad, SoundPadPlayer player)
    {
        if (player.IsPlaying(pad.Id))
        {
            switch (pad.PlayMode)
            {
                case PadPlayMode.Toggle:
                    player.Stop(pad.Id);
                    pad.IsPlaying = false;
                    return;
                case PadPlayMode.Restart:
                    player.Stop(pad.Id);
                    break;
                // Overlap: one more playback on top of the others.
            }
        }
        if (pad.StopOthers)
        {
            foreach (var p in new[] { _main.Engine.Pads, _main.Engine.PrivatePads })
                foreach (var id in p.PlayingIds())
                    if (id != pad.Id) p.Stop(id);
        }

        if (!File.Exists(pad.FilePath))
        {
            _main.ShowToast($"Fichier introuvable : {pad.FilePath}");
            return;
        }

        try
        {
            pad.IsPlaying = true;
            await player.PlayAsync(pad.Id, pad.FilePath, EffectiveVolume(pad), pad.Loop, pad.StartSeconds, pad.EndSeconds);
        }
        catch (Exception ex)
        {
            pad.IsPlaying = false;
            _main.ShowToast($"Lecture impossible de « {pad.Name} » : {Core.FriendlyErrors.Describe(ex)}");
        }
    }

    /// <summary>« Écouter » in the pad editor: plays the sound in the headphones only, nobody else hears it.</summary>
    [RelayCommand]
    private async Task PreviewPad(SoundPad? pad)
    {
        if (pad == null) return;
        var player = _main.Engine.PrivatePads;
        if (player.IsPlaying(pad.Id))
        {
            player.Stop(pad.Id);
            return;
        }
        if (!File.Exists(pad.FilePath))
        {
            _main.ShowToast($"Fichier introuvable : {pad.FilePath}");
            return;
        }
        try
        {
            pad.IsPlaying = true;
            await player.PlayAsync(pad.Id, pad.FilePath, EffectiveVolume(pad), false, pad.StartSeconds, pad.EndSeconds);
        }
        catch (Exception ex)
        {
            pad.IsPlaying = false;
            _main.ShowToast($"Écoute impossible de « {pad.Name} » : {Core.FriendlyErrors.Describe(ex)}");
        }
    }

    // ---- Recording a sound with the microphone ---------------------------------------------------------------

    private VoiceRecorder? _recorder;
    private System.Windows.Threading.DispatcherTimer? _recordTimer;

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordingTime = "0:00";
    [ObservableProperty] private double _recordingLevel;

    /// <summary>Starts recording the microphone, or stops and adds the recording as a new sound.</summary>
    [RelayCommand]
    private async Task ToggleRecording()
    {
        if (_recorder == null)
        {
            if (string.IsNullOrEmpty(_main.Settings.MicDeviceId) && !_main.Devices.Inputs.Any())
            {
                _main.ShowToast("Aucun micro : choisissez-en un dans Configuration pour enregistrer un son.");
                return;
            }
            _recorder = new VoiceRecorder(MixerCore.SampleRate);
            _main.Engine.Core.Recorder = _recorder;
            IsRecording = true;
            RecordingTime = "0:00";
            _recordTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100),
                System.Windows.Threading.DispatcherPriority.Normal, (_, _) => UpdateRecording(),
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            _recordTimer.Start();
            return;
        }
        await FinishRecordingAsync();
    }

    private void UpdateRecording()
    {
        if (_recorder == null) return;
        var t = _recorder.Elapsed;
        RecordingTime = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        RecordingLevel = Math.Min(1, _recorder.RecentPeak * 1.4);
        if (_recorder.IsFull) _ = FinishRecordingAsync();
    }

    private async Task FinishRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder == null) return;
        _recorder = null;
        _main.Engine.Core.Recorder = null;
        _recordTimer?.Stop();
        IsRecording = false;
        RecordingLevel = 0;

        var number = Pads.Count(p => p.Name.StartsWith("Enregistrement", StringComparison.Ordinal)) + 1;
        var path = Path.Combine(_main.Downloader.CacheDirectory, TextToSpeechViewModel.SoundsFolder,
            $"Enregistrement {DateTime.Now:yyyy-MM-dd HH-mm-ss}.wav");
        try
        {
            var length = await recorder.StopAsync(path);
            if (length == TimeSpan.Zero)
            {
                _main.ShowToast("Rien n'a été enregistré : le micro est resté silencieux. Vérifiez-le dans Configuration.");
                return;
            }
            AddFiles([path]);
            if (SelectedPad is { } pad && pad.FilePath == path)
            {
                pad.Name = $"Enregistrement {number}";
                _main.ShowToast($"« {pad.Name} » ajouté ({length.TotalSeconds:0.0} s). Renommez-le et donnez-lui un raccourci à droite.");
            }
        }
        catch (Exception ex)
        {
            _main.ShowToast("Enregistrement impossible : " + Core.FriendlyErrors.Describe(ex));
        }
    }

    [RelayCommand]
    private void StopAll()
    {
        _main.Engine.Pads.StopAll();
        _main.Engine.PrivatePads.StopAll();
        foreach (var pad in Pads) pad.IsPlaying = false;
    }

    [RelayCommand]
    private void DeletePad(SoundPad? pad)
    {
        pad ??= SelectedPad;
        if (pad == null) return;
        _main.Engine.Pads.Stop(pad.Id);
        _main.Engine.PrivatePads.Stop(pad.Id);
        Pads.Remove(pad);
        if (SelectedPad == pad) SelectedPad = null;
        _main.Save();
    }

    [RelayCommand]
    private void EditPad(SoundPad? pad) => SelectedPad = pad;

    [RelayCommand]
    private void CloseEditor() => SelectedPad = null;

    [RelayCommand]
    private void SetColor(string color)
    {
        if (SelectedPad != null) SelectedPad.Color = color;
    }

    [RelayCommand]
    private void ChangeFile()
    {
        if (SelectedPad == null) return;
        var dialog = new OpenFileDialog { Title = "Choisir le fichier du son", Filter = AudioFileLoader.DialogFilter };
        if (dialog.ShowDialog() != true) return;
        var pad = SelectedPad;
        _main.Engine.Pads.Stop(pad.Id);
        pad.FilePath = dialog.FileName;
        // The excerpt and the loudness correction belonged to the old file.
        pad.StartSeconds = 0;
        pad.EndSeconds = 0;
        pad.NormalizationDb = null;
        pad.SourceUrl = null;
        Analyze(pad);
        _main.Save();
    }

    public void RefreshPlayingState()
    {
        if (Pads.Count == 0) return;
        var playing = _playingIds;
        _main.Engine.Pads.FillPlayingIds(playing);
        _main.Engine.PrivatePads.FillPlayingIds(playing, clear: false);
        foreach (var pad in Pads)
        {
            var isPlaying = playing.Contains(pad.Id);
            // Keep the "starting" state while the file is being opened.
            if (pad.IsPlaying && !isPlaying && _startGrace.TryGetValue(pad.Id, out var t) && DateTime.UtcNow - t < TimeSpan.FromMilliseconds(400))
                continue;
            pad.IsPlaying = isPlaying;
        }
    }

    private readonly Dictionary<Guid, DateTime> _startGrace = [];
    private readonly HashSet<Guid> _playingIds = [];

    private void OnPadChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SoundPad pad) return;
        if (e.PropertyName is nameof(SoundPad.Volume) or nameof(SoundPad.NormalizationDb))
        {
            _main.Engine.Pads.SetVolume(pad.Id, EffectiveVolume(pad));
            _main.Engine.PrivatePads.SetVolume(pad.Id, EffectiveVolume(pad));
        }
        else if (e.PropertyName == nameof(SoundPad.Category))
        {
            RebuildTabs();
            PadsView.Refresh();
        }
        else if (e.PropertyName == nameof(SoundPad.HeadphonesOnly))
        {
            // The sound would keep playing in the other player.
            _main.Engine.Pads.Stop(pad.Id);
            _main.Engine.PrivatePads.Stop(pad.Id);
        }
        else if (e.PropertyName == nameof(SoundPad.IsPlaying) && pad.IsPlaying)
            _startGrace[pad.Id] = DateTime.UtcNow;
    }
}
