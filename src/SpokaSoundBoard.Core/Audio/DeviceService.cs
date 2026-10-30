using NAudio.CoreAudioApi;

namespace SpokaSoundBoard.Core.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsVirtualCable)
{
    public override string ToString() => Name;
}

public static class DeviceService
{
    private static readonly string[] VirtualMarkers = ["CABLE", "VB-Audio", "Voicemeeter", "VoiceMeeter", "Virtual Audio"];

    public static bool LooksVirtual(string name) =>
        VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase))
        && !name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies the physical cable behind an endpoint: "CABLE Input" and "CABLE In 16ch" are the same
    /// VB-CABLE, while "CABLE-A Input" or "Line 1 (Virtual Audio Cable)" are different cables.
    /// </summary>
    public static string CableFamily(string name)
    {
        var open = name.LastIndexOf('(');
        var close = name.LastIndexOf(')');
        var driver = open >= 0 && close > open ? name[(open + 1)..close].Trim() : name;
        var head = (open > 0 ? name[..open] : name).Trim();
        // Virtual Audio Cable numbers its cables in the endpoint name ("Line 1", "Line 2"…).
        if (head.StartsWith("Line ", StringComparison.OrdinalIgnoreCase)) return driver + " / " + head;
        return driver;
    }

    public static bool SameCable(AudioDevice? a, AudioDevice? b) =>
        a != null && b != null && string.Equals(CableFamily(a.Name), CableFamily(b.Name), StringComparison.OrdinalIgnoreCase);

    /// <summary>A virtual cable different from the game one, to be used as Discord's microphone.</summary>
    public static AudioDevice? FindSecondCableOutput(IEnumerable<AudioDevice> outputs, AudioDevice? gameOutput) =>
        outputs.Where(o => o.IsVirtualCable && !SameCable(o, gameOutput))
            .OrderByDescending(o => o.Name.Contains("Input", StringComparison.OrdinalIgnoreCase) || o.Name.StartsWith("Line", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    public static int CountDistinctCables(IEnumerable<AudioDevice> outputs) =>
        outputs.Where(o => o.IsVirtualCable).Select(o => CableFamily(o.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public static List<AudioDevice> GetInputs() => List(DataFlow.Capture);

    public static List<AudioDevice> GetOutputs() => List(DataFlow.Render);

    private static List<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                result.Add(new AudioDevice(d.ID, d.FriendlyName, LooksVirtual(d.FriendlyName)));
            }
            catch
            {
                // Some drivers fail to expose properties; skip them.
            }
            finally
            {
                d.Dispose();
            }
        }
        return result.OrderBy(d => d.Name).ToList();
    }

    public static string? GetDefaultId(DataFlow flow, Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using var d = enumerator.GetDefaultAudioEndpoint(flow, role);
            return d.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds the playback side of a virtual cable ("CABLE Input"), which becomes the mic for other apps.</summary>
    public static AudioDevice? FindVirtualCableOutput(IEnumerable<AudioDevice> outputs)
    {
        var list = outputs.Where(o => o.IsVirtualCable).ToList();
        return list.FirstOrDefault(o => o.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault();
    }

    /// <summary>Best physical microphone: the Windows default unless it is itself a virtual cable.</summary>
    public static AudioDevice? FindDefaultMicrophone(IReadOnlyList<AudioDevice> inputs)
    {
        var defaultId = GetDefaultId(DataFlow.Capture, Role.Communications) ?? GetDefaultId(DataFlow.Capture, Role.Console);
        var def = inputs.FirstOrDefault(i => i.Id == defaultId);
        if (def is { IsVirtualCable: false }) return def;
        var defConsole = inputs.FirstOrDefault(i => i.Id == GetDefaultId(DataFlow.Capture, Role.Console));
        if (defConsole is { IsVirtualCable: false }) return defConsole;
        return inputs.FirstOrDefault(i => !i.IsVirtualCable);
    }

    /// <summary>Best headphone output: the Windows default unless it is a virtual cable.</summary>
    public static AudioDevice? FindDefaultMonitor(IReadOnlyList<AudioDevice> outputs)
    {
        var def = outputs.FirstOrDefault(o => o.Id == GetDefaultId(DataFlow.Render, Role.Multimedia));
        if (def is { IsVirtualCable: false }) return def;
        return outputs.FirstOrDefault(o => !o.IsVirtualCable);
    }

    internal static MMDevice? Open(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var d = enumerator.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch
        {
            return null;
        }
    }
}
