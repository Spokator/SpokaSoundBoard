using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SpokaSoundBoard.Core;
using Windows.ApplicationModel;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace SpokaSoundBoard.Services;

/// <summary>Outcome of a change of the "start with Windows" setting.</summary>
public enum AutostartResult
{
    Done,
    /// <summary>Store edition: the user turned the app off in Paramètres > Applications > Démarrage (or Task Manager).</summary>
    DisabledByUser,
    /// <summary>Store edition: an administrator policy decides.</summary>
    ControlledByPolicy,
}

/// <summary>
/// Small Windows integration helpers: autostart, microphone privacy, restart-and-resume.
/// Installer edition: HKCU Run / RunOnce keys. Store edition (MSIX): StartupTask, no registry autostart.
/// </summary>
public static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ValueName = "SpokaSoundBoard";
    private const string MicConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>TaskId of the desktop:StartupTask declared in packaging/AppxManifest.xml.</summary>
    public const string StartupTaskId = "SpokaSoundBoardStartup";

    public const string StartupAppsSettings = "ms-settings:startupapps";
    public const string MicrophonePrivacySettings = "ms-settings:privacy-microphone";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SpokaSoundBoard.exe");

    /// <summary>True when the process has a real package identity (SPOKA_FORCE_PACKAGED only simulates the UI paths).</summary>
    private static bool HasPackageIdentity
    {
        get
        {
            try
            {
                return Package.Current?.Id != null;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<StartupTask?> GetStartupTaskAsync()
    {
        if (!HasPackageIdentity) return null;
        return await StartupTask.GetAsync(StartupTaskId);
    }

    public static async Task<bool> IsAutostartEnabledAsync()
    {
        if (!AppEnvironment.IsPackaged)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var task = await GetStartupTaskAsync();
            return task?.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<AutostartResult> SetAutostartAsync(bool enabled)
    {
        if (!AppEnvironment.IsPackaged)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{ExecutablePath}\" --minimized");
            else key.DeleteValue(ValueName, false);
            return AutostartResult.Done;
        }

        // Store edition: never touch the Run key, Windows owns the StartupTask (and its user / policy switches).
        var task = await GetStartupTaskAsync();
        if (task == null) return AutostartResult.Done; // SPOKA_FORCE_PACKAGED without a package: nothing to do.

        if (!enabled)
        {
            if (task.State == StartupTaskState.EnabledByPolicy) return AutostartResult.ControlledByPolicy;
            task.Disable();
            return AutostartResult.Done;
        }

        var state = task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy
            ? task.State
            : await task.RequestEnableAsync();
        return state switch
        {
            StartupTaskState.DisabledByUser => AutostartResult.DisabledByUser,
            StartupTaskState.DisabledByPolicy => AutostartResult.ControlledByPolicy,
            _ => AutostartResult.Done,
        };
    }

    /// <summary>Store edition: true when Windows started the app through its StartupTask (start hidden in the tray).</summary>
    public static bool WasLaunchedByStartupTask()
    {
        if (!HasPackageIdentity) return false;
        try
        {
            return global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind
                   == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when Windows privacy settings block this app from using the microphone. Desktop apps are governed by the
    /// "NonPackaged" consent entry, the Store edition by its own package entry (both under the global switch).
    /// </summary>
    public static bool IsMicrophoneAccessDenied()
    {
        if (AppEnvironment.IsPackaged && HasPackageIdentity && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            try
            {
                var status = AppCapability.Create("microphone").CheckAccess();
                return status is AppCapabilityAccessStatus.DeniedBySystem or AppCapabilityAccessStatus.DeniedByUser;
            }
            catch
            {
                // Fall back to the registry below.
            }
        }

        static bool Denied(RegistryKey root, string path)
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            if (Denied(Registry.LocalMachine, MicConsentKey) || Denied(Registry.CurrentUser, MicConsentKey)) return true;
            if (!AppEnvironment.IsPackaged) return Denied(Registry.CurrentUser, MicConsentKey + @"\NonPackaged");
            var family = HasPackageIdentity ? Package.Current.Id.FamilyName : null;
            return family != null && Denied(Registry.CurrentUser, MicConsentKey + "\\" + family);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Store edition: shows the Windows consent prompt if the user was never asked (no-op otherwise).</summary>
    public static async Task RequestMicrophoneAccessAsync()
    {
        if (!AppEnvironment.IsPackaged || !HasPackageIdentity || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return;
        try
        {
            var capability = AppCapability.Create("microphone");
            if (capability.CheckAccess() == AppCapabilityAccessStatus.UserPromptRequired)
                await capability.RequestAccessAsync();
        }
        catch
        {
            // Older Windows builds: WASAPI shows the prompt on first capture anyway.
        }
    }

    /// <summary>
    /// Restarts the PC. Installer edition: a RunOnce entry reopens SpokaSoundBoard on the setup assistant.
    /// Store edition: RunOnce is not available to packages; the caller has saved SetupResumeStep, so the assistant
    /// resumes when the user (or the StartupTask) reopens the app.
    /// </summary>
    public static void RestartAndResumeSetup()
    {
        if (!AppEnvironment.IsPackaged)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
            key.SetValue(ValueName, $"\"{ExecutablePath}\" --setup");
        }

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
