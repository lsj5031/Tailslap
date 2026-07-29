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
    bool ResultsAlreadyStreamed = false
);

internal sealed record TranscriptionResult(string FinalText, bool WasEnhanced);

internal interface ITranscriptionResultSink
{
    Task<TranscriptionResult> ProcessAsync(
        TranscriptionResultRequest request,
        CancellationToken cancellationToken = default
    );
}
