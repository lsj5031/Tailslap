using System;
using System.Diagnostics.Tracing;

/// <summary>
/// Custom EventSource for TailSlap diagnostic tracing and performance monitoring.
/// Enables real-time monitoring of refinement operations, transcription, and system health.
///
/// Usage: Enable via Event Tracing for Windows (ETW) or dotnet-trace tools.
/// </summary>
[EventSource(Name = "TailSlap.Diagnostics")]
public sealed class DiagnosticsEventSource : EventSource
{
    public static readonly DiagnosticsEventSource Log = new();

    // Event IDs (must be unique within the event source)
    private const int RefinementStartedId = 1;
    private const int RefinementCompletedId = 2;
    private const int RefinementFailedId = 3;
    private const int RefinementRetryId = 4;
    private const int TranscriptionStartedId = 5;
    private const int TranscriptionCompletedId = 6;
    private const int TranscriptionFailedId = 7;
    private const int HttpRequestId = 8;
    private const int HttpResponseId = 9;
    private const int HistoryAppendId = 10;
    private const int HistoryTrimId = 11;
    private const int ConfigReloadId = 12;
    private const int ErrorOccurredId = 13;
    private const int PerformanceWarningId = 14;

    // Event keywords for filtering
    public static class Keywords
    {
        public const EventKeywords Refinement = (EventKeywords)0x0001;
        public const EventKeywords Transcription = (EventKeywords)0x0002;
        public const EventKeywords Http = (EventKeywords)0x0004;
        public const EventKeywords History = (EventKeywords)0x0008;
        public const EventKeywords Configuration = (EventKeywords)0x0010;
        public const EventKeywords Performance = (EventKeywords)0x0020;
        public const EventKeywords Errors = (EventKeywords)0x0040;
    }

    [Event(
        RefinementStartedId,
        Keywords = Keywords.Refinement,
        Level = EventLevel.Informational,
        Message = "Text refinement started: model={0}, inputLen={1}"
    )]
    public void RefinementStarted(string model, int inputLength)
    {
        if (IsEnabled())
            WriteEvent(RefinementStartedId, model, inputLength);
    }

    [Event(
        RefinementCompletedId,
        Keywords = Keywords.Refinement,
        Level = EventLevel.Informational,
        Message = "Text refinement completed: elapsedMs={0}, outputLen={1}, tokens={2}"
    )]
    public void RefinementCompleted(long elapsedMs, int outputLength, int? tokensUsed)
    {
        if (IsEnabled())
            WriteEvent(RefinementCompletedId, elapsedMs, outputLength, tokensUsed ?? 0);
    }

    [Event(
        RefinementFailedId,
        Keywords = Keywords.Refinement | Keywords.Errors,
        Level = EventLevel.Error,
        Message = "Text refinement failed: error={0}, statusCode={1}"
    )]
    public void RefinementFailed(string error, int? statusCode)
    {
        if (IsEnabled())
            WriteEvent(RefinementFailedId, error ?? "Unknown error", statusCode ?? 0);
    }

    [Event(
        RefinementRetryId,
        Keywords = Keywords.Refinement,
        Level = EventLevel.Warning,
        Message = "Text refinement retry: attempt={0}, reason={1}, delayMs={2}"
    )]
    public void RefinementRetry(int attempt, string reason, int delayMs)
    {
        if (IsEnabled())
            WriteEvent(RefinementRetryId, attempt, reason, delayMs);
    }

    [Event(
        TranscriptionStartedId,
        Keywords = Keywords.Transcription,
        Level = EventLevel.Informational,
        Message = "Audio transcription started: durationMs={0}, sampleRate={1}"
    )]
    public void TranscriptionStarted(int durationMs, int sampleRate)
    {
        if (IsEnabled())
            WriteEvent(TranscriptionStartedId, durationMs, sampleRate);
    }

    [Event(
        TranscriptionCompletedId,
        Keywords = Keywords.Transcription,
        Level = EventLevel.Informational,
        Message = "Audio transcription completed: elapsedMs={0}, textLen={1}, confidence={2}"
    )]
    public void TranscriptionCompleted(long elapsedMs, int textLength, float confidence)
    {
        if (IsEnabled())
            WriteEvent(TranscriptionCompletedId, elapsedMs, textLength, confidence);
    }

    [Event(
        TranscriptionFailedId,
        Keywords = Keywords.Transcription | Keywords.Errors,
        Level = EventLevel.Error,
        Message = "Audio transcription failed: error={0}"
    )]
    public void TranscriptionFailed(string error)
    {
        if (IsEnabled())
            WriteEvent(TranscriptionFailedId, error ?? "Unknown error");
    }

    [Event(
        HttpRequestId,
        Keywords = Keywords.Http,
        Level = EventLevel.Verbose,
        Message = "HTTP request: endpoint={0}, method={1}, timeoutMs={2}"
    )]
    public void HttpRequest(string endpoint, string method, int timeoutMs)
    {
        if (IsEnabled())
            WriteEvent(HttpRequestId, endpoint, method, timeoutMs);
    }

    [Event(
        HttpResponseId,
        Keywords = Keywords.Http,
        Level = EventLevel.Verbose,
        Message = "HTTP response: statusCode={0}, elapsedMs={1}, contentLen={2}"
    )]
    public void HttpResponse(int statusCode, long elapsedMs, int contentLength)
    {
        if (IsEnabled())
            WriteEvent(HttpResponseId, statusCode, elapsedMs, contentLength);
    }

    [Event(
        HistoryAppendId,
        Keywords = Keywords.History,
        Level = EventLevel.Verbose,
        Message = "History entry appended: type={0}, entrySize={1}"
    )]
    public void HistoryAppend(string type, int entrySize)
    {
        if (IsEnabled())
            WriteEvent(HistoryAppendId, type, entrySize);
    }

    [Event(
        HistoryTrimId,
        Keywords = Keywords.History,
        Level = EventLevel.Informational,
        Message = "History trimmed: type={0}, beforeCount={1}, afterCount={2}"
    )]
    public void HistoryTrim(string type, int beforeCount, int afterCount)
    {
        if (IsEnabled())
            WriteEvent(HistoryTrimId, type, beforeCount, afterCount);
    }

    [Event(
        ConfigReloadId,
        Keywords = Keywords.Configuration,
        Level = EventLevel.Informational,
        Message = "Configuration reloaded: source={0}, valid={1}"
    )]
    public void ConfigReload(string source, bool valid)
    {
        if (IsEnabled())
            WriteEvent(ConfigReloadId, source, valid);
    }

    [Event(
        ErrorOccurredId,
        Keywords = Keywords.Errors,
        Level = EventLevel.Error,
        Message = "Error occurred: component={0}, exception={1}"
    )]
    public void ErrorOccurred(string component, string exception)
    {
        if (IsEnabled())
            WriteEvent(ErrorOccurredId, component, exception ?? "Unknown");
    }

    [Event(
        PerformanceWarningId,
        Keywords = Keywords.Performance,
        Level = EventLevel.Warning,
        Message = "Performance warning: operation={0}, elapsedMs={1}, threshold={2}"
    )]
    public void PerformanceWarning(string operation, long elapsedMs, long thresholdMs)
    {
        if (IsEnabled())
            WriteEvent(PerformanceWarningId, operation, elapsedMs, thresholdMs);
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        // This method is called when ETW or other consumers enable/disable the event source
        base.OnEventCommand(command);
    }
}
