using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Online;

namespace SpokaSoundBoard.ViewModels;

/// <summary>« Sauvegarde complète » (Configuration page): everything in one file, to change PC or edition.</summary>
public partial class MainViewModel
{
    /// <summary>Backup chosen in the app, restored by the next start before anything is loaded.</summary>
    public static string PendingRestorePath => Path.Combine(AppEnvironment.DataDirectory, "pending-restore.txt");

    [ObservableProperty] private bool _backupIncludeMusic;
    [ObservableProperty] private string _backupMusicText = "Inclure les fichiers de musique";
    [ObservableProperty] private string _backupSummary = "";
    [ObservableProperty] private bool _backupBusy;
    [ObservableProperty] private double _backupProgress;

    public async Task RefreshBackupEstimateAsync()
    {
        try
        {
            var (soundBytes, musicBytes, sounds, tracks) = await Task.Run(() => FullBackup.Estimate(DataDirectory));
            BackupSummary = $"Réglages, soundboard ({sounds} son{(sounds > 1 ? "s" : "")}, {DownloadCache.FormatSize(soundBytes)}), effets de voix, " +
                            "raccourcis, télécommande, bibliothèque, playlists et historique.";
            BackupMusicText = tracks == 0
                ? "Inclure les fichiers de musique (aucun pour l'instant)"
                : $"Inclure les fichiers de musique ({tracks} morceau{(tracks > 1 ? "x" : "")}, {DownloadCache.FormatSize(musicBytes)})";
        }
        catch (Exception ex)
        {
            BackupSummary = "Estimation impossible : " + FriendlyErrors.Describe(ex);
        }
    }

    [RelayCommand]
    private async Task CreateBackup()
    {
        var dialog = new SaveFileDialog
        {
            Filter = FullBackup.DialogFilter,
            FileName = FullBackup.DefaultFileName(DateTime.Now),
            DefaultExt = FullBackup.Extension,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return;

        // The backup reads the files: write what is in memory first.
        Save();
        Music.SaveLibrary(wait: true);

        BackupBusy = true;
        BackupProgress = 0;
        try
        {
            var progress = new Progress<double>(p => BackupProgress = p);
            var path = dialog.FileName;
            var includeMusic = BackupIncludeMusic;
            var manifest = await Task.Run(() => FullBackup.Create(DataDirectory, path, includeMusic, CurrentVersion, EditionName, progress));
            var size = DownloadCache.FormatSize(new FileInfo(path).Length);
            ShowToast($"Sauvegarde créée ({size}) : {manifest.Sounds} son(s), {manifest.Tracks} morceau(x), {manifest.Playlists} playlist(s).", 6);
        }
        catch (Exception ex)
        {
            ShowToast("Sauvegarde impossible : " + FriendlyErrors.Describe(ex), 8);
        }
        finally
        {
            BackupBusy = false;
        }
    }

    [RelayCommand]
    private void RestoreBackup()
    {
        var dialog = new OpenFileDialog { Filter = FullBackup.DialogFilter, Title = "Choisir la sauvegarde à restaurer" };
        if (dialog.ShowDialog() != true) return;

        BackupManifest manifest;
        try
        {
            manifest = FullBackup.Inspect(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowToast("Impossible d'ouvrir cette sauvegarde : " + FriendlyErrors.Describe(ex), 8);
            return;
        }

        var content = $"{manifest.Sounds} son(s), {manifest.Tracks} morceau(x), {manifest.Playlists} playlist(s), {manifest.HistoryEntries} écoute(s)" +
                      (manifest.IncludesMusic ? ", fichiers de musique inclus" : "");
        var question = $"Restaurer la sauvegarde du {manifest.CreatedAt:d MMMM yyyy à HH:mm} (SpokaSoundBoard {manifest.AppVersion}) ?\n\n" +
                       $"{content}.\n\n" +
                       "Vos réglages, votre soundboard et votre bibliothèque actuels seront remplacés. Une copie de l'état actuel est gardée " +
                       $"dans le dossier des réglages (« {FullBackup.SafetyFolder} »), et aucun fichier audio n'est supprimé.\n\n" +
                       "SpokaSoundBoard va redémarrer.";
        if (MessageBox.Show(question, "Restaurer une sauvegarde", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(PendingRestorePath, dialog.FileName);
            // The new instance waits for this one to close (single instance), then restores before loading anything.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--after {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            try { File.Delete(PendingRestorePath); } catch { /* nothing to undo */ }
            ShowToast("Redémarrage impossible : " + FriendlyErrors.Describe(ex), 8);
            return;
        }
        ExitRequested = true;
        Application.Current.MainWindow?.Close();
    }

    /// <summary>
    /// After a restore, files the previous state used must stay on disk (the copy in « Sauvegardes » points to them):
    /// the automatic clean-up of unused downloads waits 30 days.
    /// </summary>
    private bool RestoredRecently()
    {
        try
        {
            var folder = Path.Combine(DataDirectory, FullBackup.SafetyFolder);
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "avant-restauration-*")
                .Any(f => File.GetCreationTimeUtc(f) > DateTime.UtcNow.AddDays(-30));
        }
        catch
        {
            return true; // in doubt, keep the files
        }
    }
}
