using System;
using System.Net.Http;
using System.Net.WebSockets;

/// <summary>
/// User-facing summaries for failures that can be recovered by retrying an API operation.
/// Provider response bodies must never be copied into these messages.
/// </summary>
public static class ApiFailureMessages
{
    private const string RetryInstruction =
        "Choose Retry Last Operation from the TailSlap tray menu.";

    public static bool IsConnectionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (
                current is HttpRequestException
                || current is TaskCanceledException
                || current is TimeoutException
                || current is WebSocketException
            )
            {
                return true;
            }
        }

        return exception
            is TranscriberException
            {
                ErrorType: TranscriberErrorType.ConnectionFailed
                    or TranscriberErrorType.NetworkTimeout
            };
    }

    public static string LlmConnectionFailed(bool retryFromTray = true) =>
        retryFromTray
            ? $"Could not connect to your API. Check the API URL and network connection, then {RetryInstruction}"
            : "Could not connect to your API. Check the API URL and network connection, then try again.";

    public static string TranscriptionConnectionFailed(bool retryFromTray = true) =>
        retryFromTray
            ? $"Could not connect to your transcription API. Check the API URL and network connection, then {RetryInstruction}"
            : "Could not connect to your transcription API. Check the API URL and network connection, then try again.";

    public static string RefinementFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (IsConnectionFailure(exception))
            return LlmConnectionFailed();

        return $"Refinement failed: {Shorten(exception.Message)}";
    }

    public static string TranscriptionFailed(Exception exception, bool retryFromTray = true)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (IsConnectionFailure(exception))
        {
            return retryFromTray
                ? TranscriptionConnectionFailed()
                : "Could not connect to your transcription API. Check the API URL and network connection, then hold the push-to-talk hotkey again to try again.";
        }

        return $"Transcription failed: {Shorten(exception.Message)}";
    }

    private static string Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "the provider returned an unexpected error.";

        const int maxLength = 200;
        return message.Length <= maxLength ? message : message[..maxLength] + "…";
    }
}
