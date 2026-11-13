using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Services;

/// <summary>
/// Buttons and hat switches of HOTAS, joysticks, throttles, pedals and gamepads, read in the background through
/// Raw Input (HID): they work while a game has the focus, like the keyboard shortcuts, and need no driver.
/// Runs on its own thread with a message-only window; events are raised on that thread.
/// </summary>
public sealed class ControllerInput : IDisposable
{
    private const int WmInput = 0x00FF, WmInputDeviceChange = 0x00FE;
    private const int GidcRemoval = 2;
    private const uint RidInput = 0x10000003, RimTypeHid = 2;
    private const uint RidiDeviceName = 0x20000007, RidiPreparsedData = 0x20000005;
    private const uint RidevInputSink = 0x00000100, RidevDevNotify = 0x00002000, RidevRemove = 0x00000001;
    private const ushort GenericDesktop = 0x01, ButtonPage = 0x09, HatSwitch = 0x39;
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private const int ValueCapsSize = 72;

    // Joystick, gamepad, multi-axis controller (HOTAS throttles and pedals report one of these).
    private static readonly ushort[] Usages = [0x04, 0x05, 0x08];

    private static readonly int HeaderSize = 8 + 2 * IntPtr.Size;

    private sealed class Device
    {
        public required string Key { get; init; }
        public required string Instance { get; init; }
        public required string Name { get; init; }
        public IntPtr Preparsed { get; init; }
        public ushort[] UsageList { get; init; } = [];
        public (ushort Link, int Min, int Max)[] Hats { get; init; } = [];
        public int[] HatValues { get; init; } = [];
    }

    private readonly ControllerTracker _tracker = new();
    private readonly Dictionary<IntPtr, Device?> _devices = [];
    private readonly List<int> _buttons = [];
    private byte[] _buffer = new byte[512];
    private byte[] _report = new byte[64];
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private HwndSource? _window;

    /// <summary>A button went down: code (<see cref="ControllerButtons"/>), device key, device name.</summary>
    public event Action<int, string, string>? ButtonDown;

    /// <summary>A button went up: code, device key.</summary>
    public event Action<int, string>? ButtonUp;

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                // HWND_MESSAGE parent: never shown, never in Alt+Tab, but receives WM_INPUT.
                _window = new HwndSource(new HwndSourceParameters("SpokaSoundBoard controllers")
                {
                    ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0,
                });
                _window.AddHook(WndProc);
                Register(_window.Handle, RidevInputSink | RidevDevNotify);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            ready.Set();
            if (error == null) Dispatcher.Run();
            Cleanup();
        })
        {
            IsBackground = true,
            Name = "SpokaSoundBoard controllers",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (error != null)
        {
            _thread = null;
            throw error;
        }
    }

    private static void Register(IntPtr target, uint flags)
    {
        var devices = Usages.Select(u => new RawInputDevice
        {
            UsagePage = GenericDesktop, Usage = u, Flags = flags, Target = (flags & RidevRemove) != 0 ? IntPtr.Zero : target,
        }).ToArray();
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg == WmInput) OnInput(lParam);
            else if (msg == WmInputDeviceChange && (int)wParam == GidcRemoval) OnRemoved(lParam);
        }
        catch (Exception ex)
        {
            // A strange device must never stop the others (nor the keyboard shortcuts).
            App.Log(ex);
        }
        return IntPtr.Zero; // not handled: DefWindowProc frees the WM_INPUT data
    }

    private void OnInput(IntPtr rawInput)
    {
        uint size = 0;
        if (GetRawInputData(rawInput, RidInput, null, ref size, (uint)HeaderSize) != 0 || size == 0) return;
        if (_buffer.Length < size) _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
        if (GetRawInputData(rawInput, RidInput, _buffer, ref size, (uint)HeaderSize) == uint.MaxValue) return;
        if (BitConverter.ToUInt32(_buffer, 0) != RimTypeHid) return;

        var handle = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(_buffer, 8) : BitConverter.ToInt32(_buffer, 8);
        if (!_devices.TryGetValue(handle, out var device))
        {
            try
            {
                device = Describe(handle);
            }
            catch (Exception ex)
            {
                App.Log(ex); // logged once: the device is then ignored until it is plugged again
                device = null;
            }
            _devices[handle] = device;
        }
        if (device == null) return;

        var reportSize = (int)BitConverter.ToUInt32(_buffer, HeaderSize);
        var count = (int)BitConverter.ToUInt32(_buffer, HeaderSize + 4);
        if (reportSize <= 0 || count <= 0 || HeaderSize + 8 + (long)reportSize * count > size) return;
        if (_report.Length < reportSize) _report = new byte[reportSize];

        for (var r = 0; r < count; r++)
        {
            Buffer.BlockCopy(_buffer, HeaderSize + 8 + r * reportSize, _report, 0, reportSize);
            var buttons = ReadButtons(device, reportSize);
            var hats = ReadHats(device, reportSize);
            var (pressed, released) = _tracker.Update(device.Instance, buttons, hats);
            foreach (var code in released) ButtonUp?.Invoke(code, device.Key);
            foreach (var code in pressed) ButtonDown?.Invoke(code, device.Key, device.Name);
        }
    }

    private List<int>? ReadButtons(Device device, int reportSize)
    {
        if (device.UsageList.Length == 0) return null;
        var length = (uint)device.UsageList.Length;
        var status = HidP_GetUsages(HidpInput, ButtonPage, 0, device.UsageList, ref length, device.Preparsed, _report, (uint)reportSize);
        // Another report of the same device (other report id): its buttons did not change.
        if (status != HidpStatusSuccess) return null;
        _buttons.Clear();
        for (var i = 0; i < length; i++) _buttons.Add(device.UsageList[i]);
        return _buttons;
    }

    private int[]? ReadHats(Device device, int reportSize)
    {
        if (device.Hats.Length == 0) return null;
        for (var h = 0; h < device.Hats.Length; h++)
        {
            var (link, min, max) = device.Hats[h];
            var status = HidP_GetUsageValue(HidpInput, GenericDesktop, link, HatSwitch, out var raw, device.Preparsed, _report, (uint)reportSize);
            device.HatValues[h] = status != HidpStatusSuccess ? ControllerTracker.HatUnchanged : HatEighth((int)raw, min, max);
        }
        return device.HatValues;
    }

    /// <summary>Hat value to eighths from up (8 positions; 4-position hats skip the diagonals), -1 for centered.</summary>
    private static int HatEighth(int raw, int min, int max)
    {
        var positions = max - min + 1;
        if (positions is not (4 or 8)) positions = 8;
        if (raw < min || raw >= min + positions) return -1; // the "null" value: centered
        return (raw - min) * (8 / positions);
    }

    private void OnRemoved(IntPtr handle)
    {
        if (!_devices.Remove(handle, out var device) || device == null) return;
        foreach (var code in _tracker.Release(device.Instance)) ButtonUp?.Invoke(code, device.Key);
        Marshal.FreeHGlobal(device.Preparsed);
    }

    private static Device? Describe(IntPtr handle)
    {
        var path = DeviceName(handle);
        if (path == null) return null;

        uint size = 0;
        GetRawInputDeviceInfo(handle, RidiPreparsedData, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var preparsed = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputDeviceInfo(handle, RidiPreparsedData, preparsed, ref size) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var maxUsages = HidP_MaxUsageListLength(HidpInput, ButtonPage, preparsed);
            var hats = ReadHatCaps(preparsed);
            if (maxUsages == 0 && hats.Length == 0)
            {
                Marshal.FreeHGlobal(preparsed);
                return null; // no button: nothing to bind
            }
            return new Device
            {
                Key = ControllerButtons.DeviceKey(path),
                Instance = path,
                Name = ProductName(path),
                Preparsed = preparsed,
                UsageList = new ushort[maxUsages],
                Hats = hats,
                HatValues = new int[hats.Length],
            };
        }
        catch
        {
            Marshal.FreeHGlobal(preparsed);
            throw;
        }
    }

    private static (ushort, int, int)[] ReadHatCaps(IntPtr preparsed)
    {
        var caps = new byte[64];
        if (HidP_GetCaps(preparsed, caps) != HidpStatusSuccess) return [];
        var valueCount = BitConverter.ToUInt16(caps, 48); // HIDP_CAPS.NumberInputValueCaps
        if (valueCount == 0) return [];
        var values = new byte[valueCount * ValueCapsSize];
        var length = valueCount;
        if (HidP_GetValueCaps(HidpInput, values, ref length, preparsed) != HidpStatusSuccess) return [];

        var hats = new List<(ushort, int, int)>();
        for (var i = 0; i < length; i++)
        {
            var o = i * ValueCapsSize;
            if (BitConverter.ToUInt16(values, o) != GenericDesktop) continue;            // UsagePage
            var isRange = values[o + 12] != 0;
            var usage = BitConverter.ToUInt16(values, o + 56);                            // Usage / UsageMin
            var usageMax = BitConverter.ToUInt16(values, o + 58);
            if (isRange ? HatSwitch < usage || HatSwitch > usageMax : usage != HatSwitch) continue;
            hats.Add((BitConverter.ToUInt16(values, o + 6), BitConverter.ToInt32(values, o + 40), BitConverter.ToInt32(values, o + 44)));
        }
        return hats.ToArray();
    }

    private static string? DeviceName(IntPtr handle)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(handle, RidiDeviceName, IntPtr.Zero, ref chars);
        if (chars == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return GetRawInputDeviceInfo(handle, RidiDeviceName, buffer, ref chars) == uint.MaxValue
                ? null
                : Marshal.PtrToStringUni(buffer)?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>« T.16000M », « Saitek X52 Flight Control System »… or « Manette » when the device does not say.</summary>
    private static string ProductName(string path)
    {
        try
        {
            using var file = CreateFile(path, 0, 3 /* share read | write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
            if (file.IsInvalid) return "Manette";
            var buffer = new byte[256];
            if (!HidD_GetProductString(file, buffer, buffer.Length)) return "Manette";
            var name = System.Text.Encoding.Unicode.GetString(buffer).Split('\0')[0].Trim();
            if (name.Length == 0) return "Manette";
            return name.Length > 40 ? name[..40].TrimEnd() + "…" : name;
        }
        catch
        {
            return "Manette";
        }
    }

    private void Cleanup()
    {
        foreach (var (device, code) in _tracker.ReleaseAll())
        {
            var key = _devices.Values.FirstOrDefault(d => d?.Instance == device)?.Key;
            if (key != null) ButtonUp?.Invoke(code, key);
        }
        foreach (var device in _devices.Values)
            if (device != null) Marshal.FreeHGlobal(device.Preparsed);
        _devices.Clear();
        try
        {
            if (_window != null) Register(IntPtr.Zero, RidevRemove);
        }
        catch
        {
            // Already gone with the window.
        }
        _window?.Dispose();
        _window = null;
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread == null) return;
        _thread = null;
        _dispatcher?.InvokeShutdown();
        thread.Join(2000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, ushort[] usages,
        ref uint length, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);
}
