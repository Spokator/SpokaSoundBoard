using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SpokaSoundBoard;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpokaSoundBoard", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "SpokaSoundBoard.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("SpokaSoundBoard est déjà ouvert.", "SpokaSoundBoard", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        var window = new MainWindow();
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
