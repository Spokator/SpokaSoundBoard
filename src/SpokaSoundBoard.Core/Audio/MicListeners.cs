using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>A program currently recording from a microphone (one Windows audio session).</summary>
public sealed record MicListener(string DeviceName, bool IsVirtualCable, string ProcessName);

/// <summary>
/// Which programs are recording from which microphone, read from the Windows audio sessions of every capture endpoint
/// (the same information as the microphone privacy indicator). Read only: nothing is opened or recorded.
/// </summary>
public static class MicListeners
{
    /// <summary>Active capture sessions right now. COM calls: run it off the UI thread. Never throws.</summary>
    public static IReadOnlyList<MicListener> Snapshot()
    {
        var result = new List<MicListener>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    var name = device.FriendlyName;
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession) continue;
                        var process = ProcessName((int)session.GetProcessID);
                        if (process != null) result.Add(new MicListener(name, DeviceService.LooksVirtual(name), process));
                    }
                }
                catch
                {
                    // A driver that does not expose its sessions: skip that microphone.
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // No audio service: nothing to report.
        }
        return result;
    }

    private static string? ProcessName(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null; // ended in the meantime
        }
    }
}

public enum DiscordMicState
{
    /// <summary>Discord is not recording right now (not in a voice channel, or closed).</summary>
    Unknown,
    /// <summary>Discord records a real microphone: friends on Discord hear the natural voice, without effects.</summary>
    RealMic,
    /// <summary>Discord records a virtual microphone: it hears what SpokaSoundBoard sends.</summary>
    VirtualMic,
}

/// <summary>What Discord is listening to.</summary>
public sealed record DiscordMicCheck(DiscordMicState State, string? DeviceName)
{
    public static readonly DiscordMicCheck Unknown = new(DiscordMicState.Unknown, null);

    private static readonly string[] DiscordProcesses = ["Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment"];

    public static DiscordMicCheck Evaluate(IEnumerable<MicListener> listeners)
    {
        var discord = listeners.Where(l => DiscordProcesses.Contains(l.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (discord.Count == 0) return Unknown;
        // A virtual microphone anywhere means our mix reaches Discord.
        if (discord.FirstOrDefault(l => l.IsVirtualCable) is { } cable) return new(DiscordMicState.VirtualMic, cable.DeviceName);
        return new(DiscordMicState.RealMic, discord[0].DeviceName);
    }
}
