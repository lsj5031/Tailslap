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
        var (response, failureReason) = TryInvoke(mode, foregroundWindow, timeoutMs);
        if (failureReason != null)
        {
            return UiaProbeInvocationResult.Fatal(failureReason);
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

    /// <summary>
    /// Reads the full text value of the focused editable control. Unlike
    /// <see cref="TryGetSelection"/>, a readable-but-empty control is reported
    /// as found with an empty value so callers can distinguish an empty field
    /// from a control that could not be read at all.
    /// </summary>
    public static (bool Found, string? Value) TryGetControlValue(
        IntPtr foregroundWindow,
        int timeoutMs
    )
    {
        var (response, failureReason) = TryInvoke(
            UiaProbeMode.ControlValue,
            foregroundWindow,
            timeoutMs
        );
        if (failureReason != null || response == null)
        {
            return (false, null);
        }

        if (string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            return (true, response.Text ?? string.Empty);
        }

        return (false, null);
    }

    private static (UiaProbeResponse? Response, string? FailureReason) TryInvoke(
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
            return (null, "Current process path is unavailable.");
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
                return (null, "Probe process failed to start.");
            }
        }
        catch (Exception ex)
        {
            return (null, $"Probe process start failed: {ex.GetType().Name}: {ex.Message}");
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

            return (null, $"Probe process timed out after {timeoutMs + StartupBufferMs}ms.");
        }

        // WaitForExit returning true does not guarantee the redirected pipes
        // have hit EOF. Bound the drain so a grandchild inheriting the handles
        // cannot hang this call forever.
        if (!Task.WhenAll(stdoutTask, stderrTask).Wait(TimeSpan.FromSeconds(2)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { }
            return (null, "Probe process stream drain timed out.");
        }
        string stdout = stdoutTask.GetAwaiter().GetResult().Trim();
        string stderr = stderrTask.GetAwaiter().GetResult().Trim();

        if (!UiaProbeProtocol.TryDeserialize(stdout, out var response, out var parseError))
        {
            string detail = !string.IsNullOrWhiteSpace(stderr) ? $" stderr={stderr}" : string.Empty;
            return (null, $"{parseError}{detail}");
        }

        if (process.ExitCode != 0)
        {
            string responseError = !string.IsNullOrWhiteSpace(response?.Error)
                ? response!.Error!
                : stderr;
            string error = string.IsNullOrWhiteSpace(responseError)
                ? $"Probe process exited with code {process.ExitCode}."
                : $"Probe process exited with code {process.ExitCode}: {responseError}";
            return (null, error);
        }

        return (response, null);
    }
}
