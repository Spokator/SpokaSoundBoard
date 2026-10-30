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

    private void OnHookKeyDown(int vk, HotkeyModifiers modifiers)
    {
        // The radio key must react instantly and is handled directly on the hook thread.
        if (_recording == null && _pttKey == 0)
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

        var bindings = BindingsProvider?.Invoke();
        if (bindings == null) return;
        foreach (var (b, action) in bindings.ToList())
        {
            if (b.IsSet && b.VirtualKey == vk && b.Modifiers == modifiers)
                action();
        }
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
