using System;
using System.IO;
using System.Reflection;

internal static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string PublishTimeUtc { get; } = ReadPublishTimeUtc();

    public static string DisplayString => $"v{Version} · built {PublishTimeUtc} UTC";

    private static string ReadPublishTimeUtc()
    {
        try
        {
            // The publish step rewrites the exe, so its file time identifies
            // the exact build the running process was produced from.
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            // Fall through to the unknown marker.
        }

        return "unknown";
    }
}
