using Microsoft.Win32;

namespace ClipMaster;

public static class StartupService
{
    private const string AppName = "ClipMaster";
    private const string RunKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Registers or unregisters ClipMaster to run on Windows startup
    /// via the current-user registry Run key.
    /// </summary>
    public static void SetRunOnStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    public static bool IsRunOnStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(AppName) != null;
    }
}
