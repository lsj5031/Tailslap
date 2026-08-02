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
    /// the final delivery is skipped (text left on clipboard) if the foreground
    /// window changed during transcription, so a single paste never lands in a
    /// different app. Null/zero disables the guard.
    /// </summary>
    IntPtr? TargetWindow = null
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
