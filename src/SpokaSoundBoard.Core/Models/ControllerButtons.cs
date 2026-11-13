using System.Text.RegularExpressions;

namespace SpokaSoundBoard.Core.Models;

/// <summary>
/// Buttons of game controllers (HOTAS, joysticks, throttles, pedals, gamepads) used as shortcuts. They share the
/// <see cref="HotkeyBinding.VirtualKey"/> field with keyboard keys, in a range no key uses, and a binding also names
/// its device (<see cref="HotkeyBinding.Device"/>), so the same button number on the stick and on the throttle are
/// two different shortcuts.
/// </summary>
public static partial class ControllerButtons
{
    /// <summary>First code of the controller range (keyboard and mouse codes stay below 0x100).</summary>
    public const int Base = 0x10000;

    public const int MaxButtons = 256;
    private const int HatBase = Base + 0x400;

    public static int Button(int number) => Base + Math.Clamp(number, 1, MaxButtons);

    /// <summary>A hat switch (POV) direction: 0 up, 1 right, 2 down, 3 left.</summary>
    public static int Hat(int hat, int direction) => HatBase + Math.Clamp(hat, 0, 15) * 4 + (direction & 3);

    public static bool IsController(int code) => code >= Base;

    public static string Name(int code)
    {
        if (code >= HatBase)
        {
            var hat = (code - HatBase) / 4;
            var direction = ((code - HatBase) % 4) switch { 0 => "haut", 1 => "droite", 2 => "bas", _ => "gauche" };
            return hat == 0 ? $"Chapeau {direction}" : $"Chapeau {hat + 1} {direction}";
        }
        return $"Bouton {code - Base}";
    }

    /// <summary>
    /// Stable key of a device from its Windows path: « VID_044F&amp;PID_B10A » (plus « &amp;MI_01 » for one interface of a
    /// composite device). It survives a change of USB port; two identical devices share it.
    /// </summary>
    public static string DeviceKey(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return "";
        var m = VidPid().Match(devicePath);
        if (!m.Success) return devicePath.ToUpperInvariant();
        var key = $"VID_{m.Groups[1].Value}&PID_{m.Groups[2].Value}".ToUpperInvariant();
        var mi = Interface().Match(devicePath);
        return mi.Success ? $"{key}&MI_{mi.Groups[1].Value.ToUpperInvariant()}" : key;
    }

    /// <summary>The cardinal directions held for a hat value (0 = up, then clockwise in eighths; -1 = centered).</summary>
    public static IEnumerable<int> HatDirections(int eighth)
    {
        var mask = HatMask(eighth);
        for (var d = 0; d < 4; d++)
            if ((mask & (1 << d)) != 0) yield return d;
    }

    /// <summary><see cref="HatDirections"/> as bits (bit 0 up … bit 3 left), without allocating.</summary>
    public static int HatMask(int eighth) => eighth switch
    {
        0 => 0b0001, 1 => 0b0011, 2 => 0b0010, 3 => 0b0110,
        4 => 0b0100, 5 => 0b1100, 6 => 0b1000, 7 => 0b1001,
        _ => 0,
    };

    // USB « VID_044F&PID_B10A », Bluetooth « VID&0002045E_PID&0B13 » (source prefix, then the vendor id).
    [GeneratedRegex(@"VID[_&](?:[0-9A-F]{4})?([0-9A-F]{4})(?![0-9A-F]).*?PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    [GeneratedRegex(@"&MI_([0-9A-Fa-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex Interface();
}

/// <summary>
/// Turns the state reports of controllers (the buttons held now, the hat positions) into presses and releases.
/// One instance follows every device. Not thread-safe: the input thread owns it. No allocation when nothing changed
/// (axes moving send hundreds of reports per second).
/// </summary>
public sealed class ControllerTracker
{
    /// <summary>Hat value meaning « not in this report, keep the previous position ».</summary>
    public const int HatUnchanged = int.MinValue;

    private static readonly int[] None = [];

    private sealed class DeviceState
    {
        public readonly HashSet<int> Buttons = [];
        public int[] Hats = [];
        public readonly HashSet<int> Held = [];
    }

    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _now = [];

    /// <param name="device">One physical device (two identical sticks are two instances).</param>
    /// <param name="buttons">Numbers (1-based) of the buttons held, or null when this report carries no buttons
    /// (devices that split their state over several reports): the previous ones stay held.</param>
    /// <param name="hats">Position of each hat switch: 0..7 (eighths from up, clockwise), -1 when centered,
    /// <see cref="HatUnchanged"/> when not in this report.</param>
    public (IReadOnlyList<int> Pressed, IReadOnlyList<int> Released) Update(string device, IEnumerable<int>? buttons, IReadOnlyList<int>? hats)
    {
        if (!_devices.TryGetValue(device, out var state)) _devices[device] = state = new DeviceState();
        if (buttons != null)
        {
            state.Buttons.Clear();
            foreach (var b in buttons)
                if (b is >= 1 and <= ControllerButtons.MaxButtons) state.Buttons.Add(b);
        }
        if (hats != null)
        {
            if (state.Hats.Length < hats.Count)
            {
                var grown = new int[hats.Count];
                Array.Fill(grown, -1);
                state.Hats.CopyTo(grown, 0);
                state.Hats = grown;
            }
            for (var h = 0; h < hats.Count; h++)
                if (hats[h] != HatUnchanged) state.Hats[h] = hats[h];
        }

        _now.Clear();
        foreach (var b in state.Buttons) _now.Add(ControllerButtons.Button(b));
        for (var h = 0; h < state.Hats.Length; h++)
        {
            var mask = ControllerButtons.HatMask(state.Hats[h]);
            for (var d = 0; d < 4; d++)
                if ((mask & (1 << d)) != 0) _now.Add(ControllerButtons.Hat(h, d));
        }

        if (_now.SetEquals(state.Held)) return (None, None);
        var pressed = _now.Where(c => !state.Held.Contains(c)).Order().ToArray();
        var released = state.Held.Where(c => !_now.Contains(c)).Order().ToArray();
        state.Held.Clear();
        state.Held.UnionWith(_now);
        return (pressed, released);
    }

    /// <summary>Device unplugged (or the app losing it): everything it held is released.</summary>
    public IReadOnlyList<int> Release(string device) =>
        _devices.Remove(device, out var state) ? state.Held.Order().ToArray() : None;

    /// <summary>Every device at once (the listener stopping).</summary>
    public List<(string Device, int Code)> ReleaseAll()
    {
        var all = _devices.SelectMany(d => d.Value.Held.Order().Select(c => (d.Key, c))).ToList();
        _devices.Clear();
        return all;
    }
}
