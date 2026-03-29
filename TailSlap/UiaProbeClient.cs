using System.Diagnostics;
using System.Globalization;
using System.Text;

internal readonly record struct UiaProbeInvocationResult(
    bool HasText,
    string? Text,
    bool ContinueAttempts,
    string? FailureReason
)
{
    public static UiaProbeInvocationResult Success(string text) => new(true, text, true, null);

    public static UiaProbeInvocationResult Empty() => new(false, null, true, null);

    public static UiaProbeInvocationResult Fatal(string reason) => new(false, null, false, reason);
}

internal static class UiaProbeClient
{
    private const int StartupBufferMs = 600;

    public static UiaProbeInvocationResult TryGetSelection(
        UiaProbeMode mode,
        IntPtr foregroundWindow,
        int timeoutMs
    )
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                executablePath = current.MainModule?.FileName;
            }
            catch
            {
                executablePath = null;
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return UiaProbeInvocationResult.Fatal("Current process path is unavailable.");
        }

        using var process = new Process();
        process.StartInfo.FileName = executablePath;
        process.StartInfo.ArgumentList.Add(UiaProbeRequest.CommandName);
        process.StartInfo.ArgumentList.Add(UiaProbeProtocol.ToArgument(mode));
        if (foregroundWindow != IntPtr.Zero)
        {
            process.StartInfo.ArgumentList.Add(
                foregroundWindow.ToInt64().ToString(CultureInfo.InvariantCulture)
            );
        }

        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

        try
        {
            if (!process.Start())
            {
                return UiaProbeInvocationResult.Fatal("Probe process failed to start.");
            }
        }
        catch (Exception ex)
        {
            return UiaProbeInvocationResult.Fatal(
                $"Probe process start failed: {ex.GetType().Name}: {ex.Message}"
            );
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs + StartupBufferMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            catch { }

            return UiaProbeInvocationResult.Fatal(
                $"Probe process timed out after {timeoutMs + StartupBufferMs}ms."
            );
        }

        string stdout = stdoutTask.GetAwaiter().GetResult().Trim();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (!UiaProbeProtocol.TryDeserialize(stdout, out var response, out var parseError))
        {
            string detail = !string.IsNullOrWhiteSpace(stderr) ? $" stderr={stderr}" : string.Empty;
            return UiaProbeInvocationResult.Fatal($"{parseError}{detail}");
        }

        if (process.ExitCode != 0)
        {
            string responseError = !string.IsNullOrWhiteSpace(response?.Error)
                ? response!.Error!
                : stderr;
            string error = string.IsNullOrWhiteSpace(responseError)
                ? $"Probe process exited with code {process.ExitCode}."
                : $"Probe process exited with code {process.ExitCode}: {responseError}";
            return UiaProbeInvocationResult.Fatal(error);
        }

        if (response == null)
        {
            return UiaProbeInvocationResult.Fatal("Probe response was unexpectedly null.");
        }

        if (string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(response.Text)
                ? UiaProbeInvocationResult.Empty()
                : UiaProbeInvocationResult.Success(response.Text);
        }

        if (string.Equals(response.Status, "empty", StringComparison.OrdinalIgnoreCase))
        {
            return UiaProbeInvocationResult.Empty();
        }

        string probeError = !string.IsNullOrWhiteSpace(response.Error)
            ? response.Error
            : "Probe reported an unspecified error.";
        return UiaProbeInvocationResult.Fatal(probeError);
    }
}
