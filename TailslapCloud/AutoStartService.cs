using Microsoft.Win32;

public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string appName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(appName) != null;
    }

    public static void Toggle(string appName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null) return;
        if (IsEnabled(appName))
            key.DeleteValue(appName, false);
        else
        {
            var path = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
            if (!path.StartsWith("\"")) path = "\"" + path + "\"";
            key.SetValue(appName, path);
        }
    }
}
