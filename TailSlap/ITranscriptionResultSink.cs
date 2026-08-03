using System;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

internal enum TranscriptionDeliveryPolicy
{
    DeliverFinalText,
    DeliverOnlyIfEnhanced,
    EnhancedToClipboardWithNotice,
}

internal sealed record TranscriptionResultRequest(
    string RawText,
    AppConfig Config,
    int DurationMs,
    TranscriptionDeliveryPolicy DeliveryPolicy,
    bool ResultsAlreadyStreamed = false,
    /// <summary>
    /// The window the user was focused on when transcription began. When set,
    /// final delivery may best-effort restore this window if focus changed.
    /// </summary>
    IntPtr? TargetWindow = null,
    /// <summary>Process identity captured with <see cref="TargetWindow"/>.</summary>
    uint TargetProcessId = 0,
    /// <summary>Window class captured with <see cref="TargetWindow"/>.</summary>
    string? TargetWindowClass = null
);

internal sealed record TranscriptionResult(string FinalText, bool WasEnhanced);

internal interface ITranscriptionResultSink
{
    Task<TranscriptionResult> ProcessAsync(
        TranscriptionResultRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Records a failed transcription attempt (with optional partial text) into
    /// the encrypted transcription history so failures are not lost.
    /// </summary>
    void RecordFailure(string? partialText, int durationMs, string errorSummary);
}
