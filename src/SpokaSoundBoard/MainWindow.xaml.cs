using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using SpokaSoundBoard.Services;
using SpokaSoundBoard.ViewModels;

namespace SpokaSoundBoard;

public partial class MainWindow : Window
{
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    private readonly MainViewModel _vm;
    private readonly bool _startInTray;
    private readonly bool _openSetup;
    private TrayIconService? _tray;
    private bool _dragging;
    private readonly List<string> _pendingPacks = [];
    private bool _initialized;

    public MainWindow(bool openSetup = false, bool startInTray = false, string? packToImport = null)
    {
        if (packToImport != null) _pendingPacks.Add(packToImport);
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _openSetup = openSetup;
        _startInTray = startInTray && !openSetup;
        if (_startInTray)
        {
            // Created minimized and hidden from the taskbar, then sent to the tray once loaded (no flicker).
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _vm.Dispose();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        _tray = new TrayIconService(new TrayActions(
            Show: ShowFromTray,
            Exit: ExitApplication,
            ToggleMic: () => _vm.ToggleMicCommand.Execute(null),
            ToggleEffects: () => _vm.ToggleEffectsCommand.Execute(null),
            ToggleDiscordShare: () => _vm.ToggleDiscordShareCommand.Execute(null),
            PlayPause: () => _vm.Music.PlayPauseCommand.Execute(null),
            StopAll: () => _vm.Soundboard.StopAllCommand.Execute(null),
            IsMicEnabled: () => _vm.Mix.MicEnabled,
            AreEffectsEnabled: () => _vm.VoiceSettings.EffectsEnabled,
            IsSharingWithDiscord: () => _vm.ShareMediaWithDiscord,
            HasDiscordOutput: () => _vm.Devices.HasDiscordOutput));

        _vm.Initialize(_openSetup);
        _initialized = true;
        foreach (var pack in _pendingPacks) ImportPack(pack);
        _pendingPacks.Clear();
        if (_startInTray)
        {
            ShowInTaskbar = true;
            // The setup assistant still has something to do: stay visible.
            if (_vm.Setup.IsOpen) ShowFromTray();
            else
            {
                Hide();
                WindowState = WindowState.Normal;
            }
        }
    }

    /// <summary>Brings the window back from the tray (also used when the user launches the app a second time).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Imports a .spokapack opened from Explorer (startup argument or forwarded by a second instance).</summary>
    public void ImportPack(string path)
    {
        if (!_initialized)
        {
            _pendingPacks.Add(path);
            return;
        }
        _vm.CurrentPage = "soundboard";
        _vm.Soundboard.ImportPackFile(path);
    }

    private void ExitApplication()
    {
        _vm.ExitRequested = true;
        Close();
    }

    private void HideToTray()
    {
        Hide();
        if (!_vm.Settings.TrayHintShown)
        {
            _vm.Settings.TrayHintShown = true;
            _vm.Save();
            _tray?.ShowBalloon("SpokaSoundBoard reste actif",
                "Vos raccourcis et la musique continuent. Cliquez sur l'icône pour rouvrir, clic droit → Quitter pour fermer.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The close button / Alt+F4 send SC_CLOSE: hide to the tray instead of quitting. Plain WM_CLOSE
        // (Windows shutdown, installer, taskkill) still closes the app normally.
        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScClose && _vm.Settings.CloseToTray && !_vm.ExitRequested)
        {
            HideToTray();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragging = true;
        _vm.Music.IsSeeking = true;
    }

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        _vm.Music.SeekTo(SeekSlider.Value);
        _vm.Music.IsSeeking = false;
    }

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e) => _vm.Music.IsSeeking = true;

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // A simple click on the bar (no thumb drag) also seeks.
        if (_dragging) return;
        _vm.Music.SeekTo(SeekSlider.Value);
        _vm.Music.IsSeeking = false;
    }
}
