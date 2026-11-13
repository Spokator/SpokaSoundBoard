using System.Diagnostics;
using System.Runtime.InteropServices;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Services;

/// <summary>
/// Looks every two seconds at the programs running and the one in front, for the game profiles. Works on a thread pool
/// timer (listing processes takes a few milliseconds), and only while at least one profile is enabled.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<bool> _needed;
    private readonly Action<IReadOnlySet<string>, string?> _report;
    private readonly Timer _timer;
    private readonly string _self = GameProfile.NormalizeProcess(Process.GetCurrentProcess().ProcessName);
    private int _busy;

    /// <param name="needed">Called on the timer thread: false skips the scan (no enabled profile).</param>
    /// <param name="report">Called on the timer thread with the running programs and the one in front.</param>
    public GameWatcher(Func<bool> needed, Action<IReadOnlySet<string>, string?> report)
    {
        _needed = needed;
        _report = report;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), Interval);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!_needed()) return;
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try { running.Add(GameProfile.NormalizeProcess(p.ProcessName)); } catch { /* exited meanwhile */ }
                }
            }
            _report(running, Foreground());
        }
        catch
        {
            // A failed scan is simply retried two seconds later.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Program owning the window in front, or null (desktop, lock screen, or SpokaSoundBoard itself).</summary>
    private string? Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = GameProfile.NormalizeProcess(process.ProcessName);
            return name == _self ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Programs with a visible window, for « Ajouter un jeu ouvert » (title, program name).</summary>
    public static List<(string Title, string Process)> WindowedApps()
    {
        var self = Environment.ProcessId;
        var apps = new List<(string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    var name = GameProfile.NormalizeProcess(p.ProcessName);
                    if (name is "explorer" or "applicationframehost" or "textinputhost" or "systemsettings") continue;
                    apps.Add((p.MainWindowTitle.Trim(), name));
                }
                catch
                {
                    // Protected or exited process.
                }
            }
        }
        return apps.DistinctBy(a => a.Item2).OrderBy(a => a.Item1, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Dispose() => _timer.Dispose();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
