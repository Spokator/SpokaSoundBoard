using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SpokaSoundBoard.Services;

/// <summary>Small Windows integration helpers: autostart, microphone privacy, restart-and-resume.</summary>
public static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ValueName = "SpokaSoundBoard";
    private const string MicConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SpokaSoundBoard.exe");

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\" --minimized");
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>True when Windows privacy settings block desktop apps from using the microphone.</summary>
    public static bool IsMicrophoneAccessDenied()
    {
        static bool Denied(RegistryKey root, string path)
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Denied(Registry.LocalMachine, MicConsentKey)
                   || Denied(Registry.CurrentUser, MicConsentKey)
                   || Denied(Registry.CurrentUser, MicConsentKey + @"\NonPackaged");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restarts the PC and reopens SpokaSoundBoard on the setup assistant afterwards.</summary>
    public static void RestartAndResumeSetup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunOnceKey))
            key.SetValue(ValueName, $"\"{ExecutablePath}\" --setup");

        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"SpokaSoundBoard : redémarrage pour activer les micros virtuels\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // e.g. discord:// links when Discord is not installed.
        }
    }
}
