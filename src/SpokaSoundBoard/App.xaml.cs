using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SpokaSoundBoard;

public partial class App : Application
{
    private const string ShowEventName = "SpokaSoundBoard.ShowWindow";
    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;

    public static string LogPath { get; } = Path.Combine(
        Core.AppEnvironment.DataDirectory, "error.log");

    /// <summary>.spokapack files handed to a second instance (double-click while the app runs), imported by the first one.</summary>
    private static string PendingPacksPath { get; } = Path.Combine(
        Core.AppEnvironment.DataDirectory, "pending-packs.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "SpokaSoundBoard.SingleInstance", out var isFirst);
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
        var window = new MainWindow(startArgs.Contains("--setup"), startMinimized && packToImport == null, packToImport);
        MainWindow = window;
        window.Show();
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
        MessageBox.Show("Une erreur inattendue s'est produite :\n\n" + e.Exception.Message, "SpokaSoundBoard",
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
