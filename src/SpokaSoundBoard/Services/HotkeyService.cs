using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Services;

/// <summary>Maps global key presses to application actions and supports recording new bindings.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int VkEscape = 0x1B, VkBack = 0x08, VkDelete = 0x2E;

    private readonly GlobalInputHook _hook = new();
    private readonly Dispatcher _dispatcher;
    private Func<HotkeyBinding>? _pushToTalk;
    private Action<bool>? _pushToTalkChanged;
    private HotkeyBinding? _recording;
    private Action<bool>? _recordingDone;
    private volatile int _pttKey;

    // True while the user types in one of the app's text boxes: single keys are text there, not shortcuts.
    private volatile bool _typingInApp;

    public HotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hook.KeyDown += OnHookKeyDown;
        _hook.KeyUp += OnHookKeyUp;
    }

    /// <summary>Supplies the current bindings on every key press, so edits apply immediately (UI thread).</summary>
    public Func<IEnumerable<(HotkeyBinding Binding, Action Action)>>? BindingsProvider { get; set; }

    public void Start()
    {
        KeyNames.NameProvider = GlobalInputHook.GetKeyName;
        // Keyboard focus leaves the text box when the window is deactivated, so these two events are enough.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => _typingInApp = IsTextInput(e.NewFocus)), true);
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => _typingInApp = IsTextInput(e.NewFocus)), true);
        _hook.Install();
    }

    /// <param name="changed">Invoked on the hook thread, without delay, so it must be thread-safe.</param>
    public void RegisterPushToTalk(Func<HotkeyBinding> binding, Action<bool> changed)
    {
        _pushToTalk = binding;
        _pushToTalkChanged = changed;
    }

    /// <summary>Next key / mouse button pressed is stored into <paramref name="binding"/>.</summary>
    public void BeginRecording(HotkeyBinding binding, Action<bool> done)
    {
        _recording = binding;
        _recordingDone = done;
    }

    public void CancelRecording() => FinishRecording(false);

    private static bool IsTextInput(IInputElement? element) =>
        element is TextBoxBase or PasswordBox || element is ComboBox { IsEditable: true };

    /// <summary>
    /// Typing a pad name or a search must not fire « A » or « Espace » bound as a shortcut. Ctrl/Alt combos still
    /// work, and so do keys that never type (F1–F24, mouse buttons…).
    /// </summary>
    private bool SwallowedByTyping(int vk, HotkeyModifiers modifiers) =>
        _typingInApp && KeyNames.IsTypingKey(vk) && (modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) == 0;

    private void OnHookKeyDown(int vk, HotkeyModifiers modifiers)
    {
        // The radio key must react instantly and is handled directly on the hook thread.
        if (_recording == null && _pttKey == 0 && !SwallowedByTyping(vk, modifiers))
        {
            var ptt = _pushToTalk?.Invoke();
            if (ptt is { IsSet: true } && ptt.VirtualKey == vk)
            {
                _pttKey = vk;
                _pushToTalkChanged?.Invoke(true);
            }
        }

        _dispatcher.BeginInvoke(() => HandleKeyDown(vk, modifiers));
    }

    private void OnHookKeyUp(int vk)
    {
        if (_pttKey != 0 && _pttKey == vk)
        {
            _pttKey = 0;
            _pushToTalkChanged?.Invoke(false);
        }
    }

    private void HandleKeyDown(int vk, HotkeyModifiers modifiers)
    {
        if (_recording != null)
        {
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
        var bindings = BindingsProvider?.Invoke()?.ToList();
        if (bindings == null) return;
        foreach (var i in HotkeyMatching.Select(bindings.ConvertAll(b => b.Binding), vk, modifiers))
            bindings[i].Action();
    }

    private void FinishRecording(bool changed)
    {
        var done = _recordingDone;
        _recording = null;
        _recordingDone = null;
        done?.Invoke(changed);
    }

    public void Dispose() => _hook.Dispose();
}
