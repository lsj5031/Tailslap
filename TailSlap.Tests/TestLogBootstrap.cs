using System.IO;
using System.Runtime.CompilerServices;

namespace TailSlap.Tests;

/// <summary>
/// Redirects the static <see cref="Logger"/> to a temp directory for the whole
/// test session so `dotnet test` never writes into the real
/// %APPDATA%\TailSlap\logs\app.jsonl file.
/// </summary>
internal static class TestLogBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "tailslap-test-logs",
            System.Guid.NewGuid().ToString("N")
        );
        System.Environment.SetEnvironmentVariable("TAILSLAP_LOG_DIR", dir);
    }
}
