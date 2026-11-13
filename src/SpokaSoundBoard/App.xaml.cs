using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SpokaSoundBoard;

public partial class App : Application
{
    private static readonly string ShowEventName = "SpokaSoundBoard.ShowWindow" + Core.AppEnvironment.InstanceSuffix;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;

    public static string LogPath { get; } = Path.Combine(
        Core.AppEnvironment.DataDirectory, "error.log");

    /// <summary>.spokapack files handed to a second instance (double-click while the app runs), imported by the first one.</summary>
    private static string PendingPacksPath { get; } = Path.Combine(
        Core.AppEnvironment.DataDirectory, "pending-packs.txt");

    /// <summary>Message for the first toast (a backup was restored at start), null otherwise.</summary>
    public static string? StartupNotice { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Restart after « Restaurer une sauvegarde »: let the previous instance finish saving and exit first.
        var after = Array.IndexOf(e.Args, "--after");
        if (after >= 0 && after + 1 < e.Args.Length && int.TryParse(e.Args[after + 1], out var pid)) WaitForExit(pid);

        _singleInstance = new Mutex(true, "SpokaSoundBoard.SingleInstance" + Core.AppEnvironment.InstanceSuffix, out var isFirst);
        var startArgs = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        // Double-click on a .spokapack (file association of the MSIX manifest) or "Ouvrir avec".
        var packToImport = e.Args.FirstOrDefault(a =>
            a.EndsWith(Core.SoundboardPack.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        // Store edition: "Lancer au démarrage de Windows" goes through the StartupTask, which passes no argument.
        var startMinimized = startArgs.Contains("--minimized") || Services.WindowsIntegration.WasLaunchedByStartupTask();
        if (!isFirst)
        {
            if (packToImport != null) QueuePack(packToImport);
            // Already running (maybe hidden in the tray): bring the existing window to the front.
            if (!startMinimized)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowEventName);
                    signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    MessageBox.Show("SpokaSoundBoard est déjà ouvert.", "SpokaSoundBoard", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not MainWindow main) return;
                    main.ShowFromTray();
                    foreach (var pack in TakeQueuedPacks()) main.ImportPack(pack);
                });
        })
        { IsBackground = true, Name = "SpokaSoundBoard show signal" }.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Windows shutdown / installer update (Restart Manager): close the window so settings are saved.
        SessionEnding += (_, _) => MainWindow?.Close();
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        // French number formatting in text boxes ("12,5").
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("fr-FR")));

        base.OnStartup(e);
        ApplyPendingRestore();
        var window = new MainWindow(startArgs.Contains("--setup"), startMinimized && packToImport == null, packToImport);
        MainWindow = window;
        window.Show();
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(20_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    /// <summary>Restores the backup chosen before the restart, before the settings and the library are loaded.</summary>
    private static void ApplyPendingRestore()
    {
        var pending = ViewModels.MainViewModel.PendingRestorePath;
        if (!File.Exists(pending)) return;
        string? backup = null;
        try
        {
            backup = File.ReadAllText(pending).Trim();
            File.Delete(pending); // never retried in a loop, even if the restore fails
            var version = (typeof(App).Assembly.GetName().Version ?? new Version(1, 0)).ToString(3);
            var result = Core.FullBackup.Restore(backup, Core.AppEnvironment.DataDirectory, version,
                Core.AppEnvironment.IsPackaged ? "Microsoft Store" : "Installeur");
            StartupNotice = $"Sauvegarde du {result.Manifest.CreatedAt:d MMMM yyyy} restaurée : {result.Manifest.Sounds} son(s), " +
                            $"{result.Manifest.Tracks} morceau(x), {result.Manifest.Playlists} playlist(s).";
        }
        catch (Exception ex)
        {
            Log(ex);
            StartupNotice = "La sauvegarde n'a pas pu être restaurée : " + Core.FriendlyErrors.Describe(ex);
        }
    }

    private static void QueuePack(string path)
    {
        // Several files opened at once start several instances: retry while another one appends.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPacksPath)!);
                File.AppendAllLines(PendingPacksPath, [Path.GetFullPath(path)]);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Log(ex);
                return;
            }
        }
    }

    private static List<string> TakeQueuedPacks()
    {
        try
        {
            if (!File.Exists(PendingPacksPath)) return [];
            var packs = File.ReadAllLines(PendingPacksPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            File.Delete(PendingPacksPath);
            return packs;
        }
        catch (Exception ex)
        {
            Log(ex);
            return [];
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("Une erreur inattendue s'est produite : " + Core.FriendlyErrors.Describe(e.Exception).TrimEnd('.') +
                        ".\n\nLe détail est enregistré dans le fichier error.log du dossier des réglages.", "SpokaSoundBoard",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
