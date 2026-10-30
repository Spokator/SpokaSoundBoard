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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpokaSoundBoard", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "SpokaSoundBoard.SingleInstance", out var isFirst);
        var startArgs = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        if (!isFirst)
        {
            // Already running (maybe hidden in the tray): bring the existing window to the front.
            if (!startArgs.Contains("--minimized"))
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
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
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
        var window = new MainWindow(startArgs.Contains("--setup"), startArgs.Contains("--minimized"));
        MainWindow = window;
        window.Show();
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
