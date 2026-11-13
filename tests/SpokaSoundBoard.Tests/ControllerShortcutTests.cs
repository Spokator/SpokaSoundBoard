using SpokaSoundBoard.Core;
using SpokaSoundBoard.Core.Models;

namespace SpokaSoundBoard.Tests;

/// <summary>2.13: HOTAS / joystick / gamepad buttons as shortcuts, and the music volume shortcuts.</summary>
public sealed class ControllerShortcutTests
{
    private const string Stick = "VID_044F&PID_B10A";
    private const string Throttle = "VID_044F&PID_B687";

    [Theory]
    [InlineData(@"\\?\HID#VID_044F&PID_B10A#7&2b4c1d3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", "VID_044F&PID_B10A")]
    [InlineData(@"\\?\hid#vid_06a3&pid_0762&mi_00#8&1a2b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", "VID_06A3&PID_0762&MI_00")]
    [InlineData(@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&0b13#9&abc&0&0000#{4d1e55b2}", "VID_045E&PID_0B13")]
    [InlineData(@"\\?\ROOT#HIDCLASS#0000", @"\\?\ROOT#HIDCLASS#0000")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void DeviceKeys_SurviveAChangeOfUsbPort(string? path, string expected) => Assert.Equal(expected, ControllerButtons.DeviceKey(path));

    [Fact]
    public void Names_AreReadable()
    {
        Assert.Equal("Bouton 1", ControllerButtons.Name(ControllerButtons.Button(1)));
        Assert.Equal("Bouton 32", ControllerButtons.Name(ControllerButtons.Button(32)));
        Assert.Equal("Chapeau haut", ControllerButtons.Name(ControllerButtons.Hat(0, 0)));
        Assert.Equal("Chapeau gauche", ControllerButtons.Name(ControllerButtons.Hat(0, 3)));
        Assert.Equal("Chapeau 2 bas", ControllerButtons.Name(ControllerButtons.Hat(1, 2)));
        Assert.True(ControllerButtons.IsController(ControllerButtons.Button(1)));
        Assert.False(ControllerButtons.IsController(0x41));
        Assert.False(ControllerButtons.IsController(0x06)); // mouse button 5

        var b = new HotkeyBinding();
        b.Set(ControllerButtons.Button(3), HotkeyModifiers.None, Stick, "T.16000M");
        Assert.Equal("T.16000M · Bouton 3", b.Display);
        Assert.Equal("Bouton 3", b.ShortDisplay);
        Assert.True(b.IsController);
        b.Set(ControllerButtons.Hat(0, 1), HotkeyModifiers.None, Stick, null);
        Assert.Equal("Manette · Chapeau droite", b.Display);

        // Back to a key: the device goes away with the button.
        b.Set(0x70, HotkeyModifiers.Ctrl);
        Assert.Null(b.Device);
        Assert.Null(b.DeviceName);
        Assert.False(b.IsController);
        Assert.Equal(b.Display, b.ShortDisplay);
    }

    [Fact]
    public void Tracker_TurnsReportsIntoPressesAndReleases()
    {
        var t = new ControllerTracker();
        var (pressed, released) = t.Update("stick", [1, 3], [-1]);
        Assert.Equal([ControllerButtons.Button(1), ControllerButtons.Button(3)], pressed);
        Assert.Empty(released);

        // Nothing changed (only the axes moved): nothing to do.
        (pressed, released) = t.Update("stick", [3, 1], [-1]);
        Assert.Empty(pressed);
        Assert.Empty(released);

        (pressed, released) = t.Update("stick", [3], [-1]);
        Assert.Empty(pressed);
        Assert.Equal([ControllerButtons.Button(1)], released);

        // A report without buttons (another report id): the held ones stay held.
        (pressed, released) = t.Update("stick", null, null);
        Assert.Empty(released);

        // Other devices are separate.
        (pressed, _) = t.Update("throttle", [3], null);
        Assert.Equal([ControllerButtons.Button(3)], pressed);

        // Unplugged: everything it held is released, once.
        Assert.Equal([ControllerButtons.Button(3)], t.Release("stick"));
        Assert.Empty(t.Release("stick"));
        Assert.Equal([("throttle", ControllerButtons.Button(3))], t.ReleaseAll());
    }

    [Fact]
    public void Hats_GiveFourDirections_DiagonalsPressBoth()
    {
        var t = new ControllerTracker();
        var (pressed, _) = t.Update("stick", [], [0]);
        Assert.Equal([ControllerButtons.Hat(0, 0)], pressed);

        // Up → up-right: right is added, up stays held.
        var (p2, r2) = t.Update("stick", [], [1]);
        Assert.Equal([ControllerButtons.Hat(0, 1)], p2);
        Assert.Empty(r2);

        // Up-right → right: up is released.
        var (p3, r3) = t.Update("stick", [], [2]);
        Assert.Empty(p3);
        Assert.Equal([ControllerButtons.Hat(0, 0)], r3);

        // Not in this report: unchanged. Then centered: released.
        Assert.Empty(t.Update("stick", [], [ControllerTracker.HatUnchanged]).Released);
        Assert.Equal([ControllerButtons.Hat(0, 1)], t.Update("stick", [], [-1]).Released);

        // A second hat.
        Assert.Equal([ControllerButtons.Hat(1, 3)], t.Update("stick", [], [-1, 6]).Pressed);
        Assert.Equal([0, 1, 3], ControllerButtons.HatDirections(7).Concat(ControllerButtons.HatDirections(1)).Distinct().Order());
        Assert.Empty(ControllerButtons.HatDirections(-1));
        Assert.Empty(ControllerButtons.HatDirections(8));
    }

    [Fact]
    public void TheSameButtonOnTwoDevices_IsTwoShortcuts()
    {
        var onStick = new HotkeyBinding();
        onStick.Set(ControllerButtons.Button(2), HotkeyModifiers.None, Stick, "Stick");
        var onThrottle = new HotkeyBinding();
        onThrottle.Set(ControllerButtons.Button(2), HotkeyModifiers.None, Throttle, "Throttle");
        var key = new HotkeyBinding();
        key.Set(0x42, HotkeyModifiers.None);
        HotkeyBinding[] all = [onStick, onThrottle, key];

        Assert.Equal([0], HotkeyMatching.Select(all, ControllerButtons.Button(2), HotkeyModifiers.None, Stick));
        Assert.Equal([1], HotkeyMatching.Select(all, ControllerButtons.Button(2), HotkeyModifiers.None, Throttle.ToLowerInvariant()));
        Assert.Empty(HotkeyMatching.Select(all, ControllerButtons.Button(2), HotkeyModifiers.None, "VID_1234&PID_5678"));
        // Keys ignore the device argument; buttons never match a key code.
        Assert.Equal([2], HotkeyMatching.Select(all, 0x42, HotkeyModifiers.None));
        Assert.Empty(HotkeyMatching.Select(all, ControllerButtons.Button(2), HotkeyModifiers.None));

        Assert.Empty(HotkeyMatching.Conflicts(onStick, [(onStick, "a"), (onThrottle, "b")]));
        var again = new HotkeyBinding();
        again.Set(ControllerButtons.Button(2), HotkeyModifiers.None, Stick, "Stick");
        Assert.Equal(["a"], HotkeyMatching.Conflicts(again, [(onStick, "a"), (onThrottle, "b")]));

        // Saved without device (hand edit): any controller.
        var anyPad = new HotkeyBinding { VirtualKey = ControllerButtons.Button(5) };
        Assert.Equal([0], HotkeyMatching.Select([anyPad], ControllerButtons.Button(5), HotkeyModifiers.None, Throttle));
    }

    [Fact]
    public void Settings_KeepControllerShortcuts_AndOldFilesLoad()
    {
        var s = new AppSettings();
        s.Hotkeys.MusicVolumeUp.Set(ControllerButtons.Hat(0, 0), HotkeyModifiers.None, Stick, "T.16000M");
        s.Hotkeys.MusicVolumeDown.Set(0x6D, HotkeyModifiers.Ctrl);
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal("T.16000M · Chapeau haut", back.Hotkeys.MusicVolumeUp.Display);
        Assert.Equal(Stick, back.Hotkeys.MusicVolumeUp.Device);
        Assert.Null(back.Hotkeys.MusicVolumeDown.Device);
        Assert.Equal(0x6D, back.Hotkeys.MusicVolumeDown.VirtualKey);

        // 2.12 settings: no volume shortcuts, no devices.
        var old = SettingsStore.Deserialize("""{ "Hotkeys": { "PlayPause": { "VirtualKey": 179, "Modifiers": 0 }, "MusicVolumeUp": null } }""");
        Assert.False(old.Hotkeys.MusicVolumeUp.IsSet);
        Assert.False(old.Hotkeys.MusicVolumeDown.IsSet);
        Assert.Null(old.Hotkeys.PlayPause.Device);
        Assert.True(old.Hotkeys.PlayPause.IsSet);
    }

    [Theory]
    [InlineData(0.8f, 1, 0.85f)]
    [InlineData(0.8f, -1, 0.75f)]
    [InlineData(0.83f, 1, 0.85f)]
    [InlineData(0.83f, -1, 0.8f)]
    [InlineData(0.02f, -1, 0f)]
    [InlineData(0f, -1, 0f)]
    [InlineData(1.5f, 1, 1.5f)]
    [InlineData(1.48f, 1, 1.5f)]
    [InlineData(float.NaN, 1, 0.85f)]
    public void MusicVolumeSteps_Of5Percent(float current, int direction, float expected) =>
        Assert.Equal(expected, MixSettings.StepVolume(current, direction), 3);
}
