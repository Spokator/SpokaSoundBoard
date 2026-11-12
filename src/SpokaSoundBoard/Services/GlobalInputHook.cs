using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Services;

/// <summary>
/// System-wide low-level keyboard and mouse hooks. Unlike RegisterHotKey they do not swallow the key,
/// so the game still receives it, and they report key releases (needed for the radio push-to-talk).
/// The hooks live on a dedicated thread so a busy UI can never lag the mouse or keyboard in game.
/// Events are raised on that hook thread.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;

    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private const uint WmCheckStuckKeys = 0x8000 + 0x51; // WM_APP + n, posted to the hook thread itself

    private readonly HashSet<int> _down = [];
    private readonly StuckKeyGuard _stuck = new();
    private IntPtr _keyboardHook, _mouseHook;
    private Thread? _thread;
    private uint _threadId;
    private Timer? _stuckTimer;

    public GlobalInputHook()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    /// <summary>Raised once per physical press (auto-repeat filtered).</summary>
    public event Action<int, HotkeyModifiers>? KeyDown;

    public event Action<int>? KeyUp;

    public void Install()
    {
        if (_thread != null) return;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                using var module = Process.GetCurrentProcess().MainModule;
                var hModule = GetModuleHandle(module?.ModuleName);
                _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, hModule, 0);
                if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hModule, 0);
            }
            catch (Exception ex)
            {
                error = ex;
                ready.Set();
                return;
            }
            ready.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Hwnd == IntPtr.Zero && msg.Message == WmCheckStuckKeys)
                {
                    ReleaseStuckKeys();
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "SpokaSoundBoard input hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait();
        if (error != null)
        {
            _thread = null;
            throw error;
        }
        // Checked on the hook thread, which owns _down; nothing to do while no key is held.
        _stuckTimer = new Timer(_ =>
        {
            if (_down.Count > 0) PostThreadMessage(_threadId, WmCheckStuckKeys, IntPtr.Zero, IntPtr.Zero);
        }, null, 250, 250);
    }

    /// <summary>Hook thread: a key Windows reports as up is released, as if its key-up had arrived.</summary>
    private void ReleaseStuckKeys()
    {
        if (_down.Count == 0) return;
        foreach (var vk in _stuck.Check(_down, vk => (GetAsyncKeyState(vk) & 0x8000) != 0))
            OnUp(vk);
    }

    public static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) m |= HotkeyModifiers.Ctrl;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) m |= HotkeyModifiers.Shift;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) m |= HotkeyModifiers.Alt;
        return m;
    }

    /// <summary>Keyboard-layout aware key name (e.g. "ù", "Verr.maj").</summary>
    public static string? GetKeyName(int vk)
    {
        var scanCode = MapVirtualKey((uint)vk, 0);
        if (scanCode == 0) return null;
        long lParam = scanCode << 16;
        // Extended keys (arrows, insert, delete, home, end, page up/down) need bit 24.
        if (vk is >= 0x21 and <= 0x2E) lParam |= 1 << 24;
        var sb = new StringBuilder(64);
        var len = GetKeyNameText((int)lParam, sb, sb.Capacity);
        if (len <= 0) return null;
        var name = sb.ToString();
        return name.Length == 1 ? name.ToUpperInvariant() : char.ToUpper(name[0]) + name[1..].ToLowerInvariant();
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var msg = (int)wParam;
            if (msg is WmKeyDown or WmSysKeyDown) OnDown(vk);
            else if (msg is WmKeyUp or WmSysKeyUp) OnUp(vk);
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            switch (msg)
            {
                case WmMButtonDown: OnDown(KeyNames.VkMButton); break;
                case WmMButtonUp: OnUp(KeyNames.VkMButton); break;
                case WmXButtonDown or WmXButtonUp:
                    // MSLLHOOKSTRUCT.mouseData high word: 1 = XBUTTON1, 2 = XBUTTON2.
                    var mouseData = Marshal.ReadInt32(lParam, 8);
                    var vk = (mouseData >> 16) == 1 ? KeyNames.VkXButton1 : KeyNames.VkXButton2;
                    if (msg == WmXButtonDown) OnDown(vk); else OnUp(vk);
                    break;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void OnDown(int vk)
    {
        if (!_down.Add(vk)) return;
        try { KeyDown?.Invoke(vk, CurrentModifiers()); } catch { /* never break the hook chain */ }
    }

    private void OnUp(int vk)
    {
        if (!_down.Remove(vk)) return;
        try { KeyUp?.Invoke(vk); } catch { /* never break the hook chain */ }
    }

    public void Dispose()
    {
        if (_thread == null) return;
        _stuckTimer?.Dispose();
        _stuckTimer = null;
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    private const uint WmQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);
}
