using System;
using System.Diagnostics;
using Microsoft.Win32;

public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string appName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(appName) != null;
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"AutoStart IsEnabled failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            try
            {
                NotificationService.ShowWarning(
                    "Unable to read startup setting. You may need additional permissions."
                );
            }
            catch { }
            return false;
        }
    }

    public static void Toggle(string appName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                try
                {
                    Logger.Log($"AutoStart Toggle: Run key not found: {RunKeyPath}");
                }
                catch { }
                try
                {
                    NotificationService.ShowWarning(
                        "Unable to modify startup setting on this system."
                    );
                }
                catch { }
                return;
            }

            if (IsEnabled(appName))
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
            }
            else
            {
                var module = Process.GetCurrentProcess().MainModule;
                var path = module?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        Logger.Log("AutoStart Toggle failed: current process path is unavailable.");
                    }
                    catch { }
                    try
                    {
                        NotificationService.ShowError(
                            "Failed to determine application path for startup entry."
                        );
                    }
                    catch { }
                    return;
                }

                if (!path.StartsWith("\"", StringComparison.Ordinal))
                {
                    path = "\"" + path + "\"";
                }
                key.SetValue(appName, path);
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"AutoStart Toggle failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            try
            {
                NotificationService.ShowError(
                    "Failed to update startup setting. Try again or adjust permissions."
                );
            }
            catch { }
        }
    }
}

public sealed class AutoStartServiceAdapter : IAutoStartService
{
    public bool IsEnabled(string appName) => AutoStartService.IsEnabled(appName);
    public void Toggle(string appName) => AutoStartService.Toggle(appName);
}
