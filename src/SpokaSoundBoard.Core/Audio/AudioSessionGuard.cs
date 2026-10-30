using System.Runtime.InteropServices;

namespace SpokaSoundBoard.Core.Audio;

/// <summary>
/// Protects this process' audio sessions on the virtual cables from being turned down by others:
/// Windows "communications" ducking and Discord's "Attenuation" lower the volume of other apps,
/// which made the music sent to Discord drop out while nobody was speaking.
/// </summary>
public static class AudioSessionGuard
{
    private const int ClsCtxAll = 0x17;
    private static readonly Guid AudioSessionManager2Iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>Opts our sessions out of ducking and restores volume/mute. Returns how many corrections were needed.</summary>
    public static int Protect(string deviceId) => Protect(deviceId, (uint)Environment.ProcessId);

    internal static int Protect(string deviceId, uint processId)
    {
        var corrections = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.GetDevice(deviceId, out device) != 0 || device == null) return 0;
            var iid = AudioSessionManager2Iid;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj) != 0) return 0;
            manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return 0;
            sessions.GetCount(out var count);

            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0 || control == null) continue;
                try
                {
                    if (control is not IAudioSessionControl2 control2) continue;
                    if (control2.GetProcessId(out var pid) != 0 || pid != processId) continue;

                    control2.SetDuckingPreference(true);
                    if (control is ISimpleAudioVolume volume)
                    {
                        var context = Guid.Empty;
                        if (volume.GetMasterVolume(out var level) == 0 && level < 0.995f)
                        {
                            volume.SetMasterVolume(1f, ref context);
                            corrections++;
                        }
                        if (volume.GetMute(out var muted) == 0 && muted)
                        {
                            volume.SetMute(false, ref context);
                            corrections++;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
        }
        catch (COMException)
        {
            // Device removed or audio service restarting: nothing to protect right now.
        }
        catch (InvalidCastException)
        {
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(enumerator);
        }
        return corrections;
    }

    /// <summary>Sets the volume of our own session on a device (used by tests to simulate an attenuation).</summary>
    internal static bool SetOwnSessionVolume(string deviceId, float level)
    {
        var done = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.GetDevice(deviceId, out device) != 0 || device == null) return false;
            var iid = AudioSessionManager2Iid;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj) != 0) return false;
            manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return false;
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0 || control == null) continue;
                if (control is IAudioSessionControl2 c2 && c2.GetProcessId(out var pid) == 0 && pid == Environment.ProcessId
                    && control is ISimpleAudioVolume volume)
                {
                    var context = Guid.Empty;
                    done = volume.SetMasterVolume(level, ref context) == 0;
                }
                Marshal.ReleaseComObject(control);
            }
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(enumerator);
        }
        return done;
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    // Created through the CLSID (not a ComImport class): the enumerator is a COM singleton and may already be
    // wrapped by NAudio's own RCW type, which a typed "new" cannot cast.
    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!)!;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
