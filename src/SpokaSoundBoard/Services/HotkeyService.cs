using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Services;

/// <summary>An action and its shortcut; <paramref name="Repeat"/>: repeated while the key or button stays held.</summary>
public sealed record HotkeyAction(HotkeyBinding Binding, Action Action, bool Repeat = false);

/// <summary>
/// Maps global key presses, mouse buttons and controller buttons (HOTAS, joysticks, gamepads) to application actions
/// and supports recording new bindings.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int VkEscape = 0x1B, VkBack = 0x08, VkDelete = 0x2E;
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

    private readonly GlobalInputHook _hook = new();
    private readonly ControllerInput _controllers = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _repeat;
    private Func<HotkeyBinding>? _pushToTalk;
    private Action<bool>? _pushToTalkChanged;
    private volatile HotkeyBinding? _recording;
    private Action<bool>? _recordingDone;

    // Push-to-talk input currently held (the keyboard hook thread and the controller thread both use it).
    private readonly object _pttLock = new();
    private int _pttCode;
    private string? _pttDevice;

    // Held shortcut whose actions repeat (UI thread).
    private int _repeatCode;
    private string? _repeatDevice;
    private List<Action>? _repeatActions;

    // True while the user types in one of the app's text boxes: single keys are text there, not shortcuts.
    private volatile bool _typingInApp;

    public HotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _repeat = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
        _repeat.Tick += OnRepeat;
        _hook.KeyDown += (vk, modifiers) => OnInputDown(vk, modifiers, null, null);
        _hook.KeyUp += vk => OnInputUp(vk, null);
        _controllers.ButtonDown += (code, device, name) => OnInputDown(code, HotkeyModifiers.None, device, name);
        _controllers.ButtonUp += OnInputUp;
    }

    /// <summary>Supplies the current bindings on every key press, so edits apply immediately (UI thread).</summary>
    public Func<IEnumerable<HotkeyAction>>? BindingsProvider { get; set; }

    public void Start()
    {
        KeyNames.NameProvider = GlobalInputHook.GetKeyName;
        // Keyboard focus leaves the text box when the window is deactivated, so these two events are enough.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => _typingInApp = IsTextInput(e.NewFocus)), true);
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => _typingInApp = IsTextInput(e.NewFocus)), true);
        _hook.Install();
        try
        {
            _controllers.Start();
        }
        catch (Exception ex)
        {
            // Without controllers, the keyboard and mouse shortcuts still work.
            App.Log(ex);
        }
    }

    /// <param name="changed">Invoked on the input thread, without delay, so it must be thread-safe.</param>
    public void RegisterPushToTalk(Func<HotkeyBinding> binding, Action<bool> changed)
    {
        _pushToTalk = binding;
        _pushToTalkChanged = changed;
    }

    /// <summary>Next key, mouse button or controller button pressed is stored into <paramref name="binding"/>.</summary>
    public void BeginRecording(HotkeyBinding binding, Action<bool> done)
    {
        StopRepeat();
        _recording = binding;
        _recordingDone = done;
    }

    public void CancelRecording() => FinishRecording(false);

    private static bool IsTextInput(IInputElement? element) =>
        element is TextBoxBase or PasswordBox || element is ComboBox { IsEditable: true };

    /// <summary>
    /// Typing a pad name or a search must not fire « A » or « Espace » bound as a shortcut. Ctrl/Alt combos still
    /// work, and so do keys that never type (F1–F24, mouse buttons, controller buttons…).
    /// </summary>
    private bool SwallowedByTyping(int vk, HotkeyModifiers modifiers) =>
        _typingInApp && KeyNames.IsTypingKey(vk) && (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) == 0;

    /// <param name="device">Controller key for a controller button, null for the keyboard and the mouse.</param>
    private void OnInputDown(int code, HotkeyModifiers modifiers, string? device, string? deviceName)
    {
        // The radio key must react instantly and is handled directly on the input thread.
        if (_recording == null && !SwallowedByTyping(code, modifiers))
        {
            var ptt = _pushToTalk?.Invoke();
            if (ptt is { IsSet: true } && ptt.SameInput(code, device))
            {
                var started = false;
                lock (_pttLock)
                {
                    if (_pttCode == 0)
                    {
                        _pttCode = code;
                        _pttDevice = device;
                        started = true;
                    }
                }
                if (started) _pushToTalkChanged?.Invoke(true);
            }
        }

        _dispatcher.BeginInvoke(() => HandleDown(code, modifiers, device, deviceName));
    }

    private void OnInputUp(int code, string? device)
    {
        var ended = false;
        lock (_pttLock)
        {
            if (_pttCode != 0 && _pttCode == code && SameDevice(_pttDevice, device))
            {
                _pttCode = 0;
                _pttDevice = null;
                ended = true;
            }
        }
        if (ended) _pushToTalkChanged?.Invoke(false);
        _dispatcher.BeginInvoke(() => HandleUp(code, device));
    }

    private static bool SameDevice(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private void HandleDown(int vk, HotkeyModifiers modifiers, string? device, string? deviceName)
    {
        if (_recording != null)
        {
            if (ControllerButtons.IsController(vk))
            {
                _recording.Set(vk, HotkeyModifiers.None, device, deviceName);
                FinishRecording(true);
                return;
            }
            if (KeyNames.IsModifierKey(vk)) return;
            if (vk == VkEscape)
            {
                FinishRecording(false);
            }
            else if (vk is VkBack or VkDelete && modifiers == HotkeyModifiers.None)
            {
                _recording.Clear();
                FinishRecording(true);
            }
            else
            {
                _recording.Set(vk, modifiers);
                FinishRecording(true);
            }
            return;
        }

        if (SwallowedByTyping(vk, modifiers)) return;
        var actions = BindingsProvider?.Invoke()?.ToList();
        if (actions == null) return;
        List<Action>? repeat = null;
        foreach (var i in HotkeyMatching.Select(actions.ConvertAll(a => a.Binding), vk, modifiers, device))
        {
            actions[i].Action();
            if (actions[i].Repeat) (repeat ??= []).Add(actions[i].Action);
        }
        if (repeat == null) return;
        _repeatCode = vk;
        _repeatDevice = device;
        _repeatActions = repeat;
        _repeat.Stop();
        _repeat.Interval = RepeatDelay;
        _repeat.Start();
    }

    private void HandleUp(int code, string? device)
    {
        if (_repeatActions != null && _repeatCode == code && SameDevice(_repeatDevice, device)) StopRepeat();
    }

    private void OnRepeat(object? sender, EventArgs e)
    {
        if (_repeatActions == null)
        {
            _repeat.Stop();
            return;
        }
        _repeat.Interval = RepeatInterval;
        foreach (var action in _repeatActions) action();
    }

    private void StopRepeat()
    {
        _repeat.Stop();
        _repeatActions = null;
    }

    private void FinishRecording(bool changed)
    {
        var done = _recordingDone;
        _recording = null;
        _recordingDone = null;
        done?.Invoke(changed);
    }

    public void Dispose()
    {
        StopRepeat();
        _controllers.Dispose();
        _hook.Dispose();
    }
}
