using System;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

internal sealed class TranscriptionResultSink : ITranscriptionResultSink
{
    internal static bool ShouldAttemptBestEffortRestore(
        IntPtr targetWindow,
        uint targetProcessId,
        string? targetWindowClass,
        bool foregroundChanged
    )
    {
        return foregroundChanged
            && targetWindow != IntPtr.Zero
            && targetProcessId != 0
            && !string.IsNullOrWhiteSpace(targetWindowClass)
            && !string.Equals(targetWindowClass, "MozillaWindowClass", StringComparison.Ordinal);
    }

    private readonly IHistoryService _history;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly ClipboardHelper _clipboardHelper;
    private readonly IClipboardService _clipboardService;
    private readonly TextTyper _textTyper;
    private readonly Func<IntPtr> _getForegroundWindow;
    private readonly Func<IntPtr, uint, string, bool> _isWindowIdentityMatch;
    private readonly Func<IntPtr, uint, string, bool> _tryRestoreWindow;

    public TranscriptionResultSink(
        IHistoryService history,
        ITextRefinerFactory textRefinerFactory,
        ClipboardHelper clipboardHelper,
        IClipboardService clipboardService,
        TextTyper textTyper,
        Func<IntPtr>? getForegroundWindow = null,
        Func<IntPtr, uint, string, bool>? isWindowIdentityMatch = null,
        Func<IntPtr, uint, string, bool>? tryRestoreWindow = null
    )
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _textRefinerFactory =
            textRefinerFactory ?? throw new ArgumentNullException(nameof(textRefinerFactory));
        _clipboardHelper =
            clipboardHelper ?? throw new ArgumentNullException(nameof(clipboardHelper));
        _clipboardService =
            clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _textTyper = textTyper ?? throw new ArgumentNullException(nameof(textTyper));
        _getForegroundWindow = getForegroundWindow ?? (() => NativeMethods.GetForegroundWindow());
        _isWindowIdentityMatch =
            isWindowIdentityMatch
            ?? (
                (window, processId, windowClass) =>
                    NativeMethods.IsWindowIdentityMatch(window, processId, windowClass)
            );
        _tryRestoreWindow =
            tryRestoreWindow
            ?? (
                (window, processId, windowClass) =>
                    NativeMethods.TryRestoreWindow(window, processId, windowClass)
            );
    }

    public void RecordFailure(string? partialText, int durationMs, string errorSummary)
    {
        try
        {
            _history.AppendTranscriptionFailure(partialText, durationMs, errorSummary);
            TryLog(
                $"TranscriptionResultSink: Failure history saved, errLen={errorSummary?.Length ?? 0}"
            );
        }
        catch (Exception ex)
        {
            TryLogWarning(
                $"TranscriptionResultSink: Failure history save failed: {ex.GetType().Name}"
            );
        }
    }

    public async Task<TranscriptionResult> ProcessAsync(
        TranscriptionResultRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Config);

        if (string.IsNullOrEmpty(request.RawText))
            throw new ArgumentException("Raw transcription text is required.", nameof(request));

        if (!Enum.IsDefined(request.DeliveryPolicy))
            throw new ArgumentOutOfRangeException(nameof(request), "Unknown delivery policy.");

        var finalText = await TranscriptionAutoEnhancer
            .MaybeEnhanceAsync(
                request.RawText,
                request.Config,
                _textRefinerFactory,
                cancellationToken
            )
            .ConfigureAwait(false);
        bool wasEnhanced = !string.Equals(finalText, request.RawText, StringComparison.Ordinal);

        await DeliverAsync(request, finalText, wasEnhanced, cancellationToken)
            .ConfigureAwait(false);

        int durationMs = Math.Max(0, request.DurationMs);
        try
        {
            _history.AppendTranscription(request.RawText, durationMs);
            TryLog(
                $"TranscriptionResultSink: Raw history saved, len={request.RawText.Length}, duration={durationMs}ms"
            );
        }
        catch (Exception ex)
        {
            TryLogWarning($"TranscriptionResultSink: Raw history save failed: {ex.GetType().Name}");
        }

        if (wasEnhanced)
        {
            try
            {
                _history.Append(request.RawText, finalText, request.Config.Llm.Model);
                TryLog(
                    $"TranscriptionResultSink: Refinement history saved, rawLen={request.RawText.Length}, finalLen={finalText.Length}, model={request.Config.Llm.Model}"
                );
            }
            catch (Exception ex)
            {
                TryLogWarning(
                    $"TranscriptionResultSink: Refinement history save failed: {ex.GetType().Name}"
                );
            }
        }

        return new TranscriptionResult(finalText, wasEnhanced);
    }

    private async Task DeliverAsync(
        TranscriptionResultRequest request,
        string finalText,
        bool wasEnhanced,
        CancellationToken cancellationToken
    )
    {
        try
        {
            switch (request.DeliveryPolicy)
            {
                case TranscriptionDeliveryPolicy.DeliverFinalText:
                    if (!request.ResultsAlreadyStreamed)
                    {
                        // Prefer the original target captured for this session. If the
                        // user changed windows, restore that target before pasting, but
                        // only when its process identity is still valid. Firefox remains
                        // clipboard-only. Without auto-paste no focus change is needed.
                        IntPtr target = request.TargetWindow ?? IntPtr.Zero;
                        bool autoPaste = request.Config.Transcriber.AutoPaste;
                        if (autoPaste && target != IntPtr.Zero && _getForegroundWindow() != target)
                        {
                            bool restoredTarget = await TryRestoreTargetAsync(
                                    request,
                                    target,
                                    finalText
                                )
                                .ConfigureAwait(false);
                            if (!restoredTarget)
                                break;
                        }

                        bool delivered = await _clipboardHelper
                            .SetTextAndPasteAsync(
                                finalText,
                                autoPaste,
                                autoPaste && target != IntPtr.Zero ? target : (IntPtr?)null
                            )
                            .ConfigureAwait(false);
                        if (!delivered)
                        {
                            TryLogWarning(
                                "TranscriptionResultSink: Clipboard delivery failed for final text"
                            );
                        }
                    }
                    else if (wasEnhanced)
                    {
                        await TypeAndObserveAsync(
                                finalText,
                                request.Config.Transcriber.AutoPaste,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                    break;

                case TranscriptionDeliveryPolicy.DeliverOnlyIfEnhanced:
                    if (!wasEnhanced)
                        break;

                    if (request.Config.Transcriber.AutoPaste)
                    {
                        await TypeAndObserveAsync(finalText, true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        bool delivered = await _clipboardHelper
                            .SetTextAndPasteAsync(finalText, autoPaste: false)
                            .ConfigureAwait(false);
                        if (!delivered)
                        {
                            TryLogWarning(
                                "TranscriptionResultSink: Clipboard delivery failed for enhanced text"
                            );
                        }
                    }
                    break;

                case TranscriptionDeliveryPolicy.EnhancedToClipboardWithNotice:
                    if (!wasEnhanced)
                        break;

                    bool clipboardSet = await _clipboardService
                        .SetTextAsync(finalText)
                        .ConfigureAwait(false);
                    if (clipboardSet)
                    {
                        NotificationService.ShowInfo(
                            "Enhanced transcript is on the clipboard (Ctrl+V to paste)."
                        );
                    }
                    else
                    {
                        TryLogWarning(
                            "TranscriptionResultSink: Clipboard delivery failed for realtime enhanced text"
                        );
                    }
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            TryLog("TranscriptionResultSink: Delivery cancelled");
        }
        catch (Exception ex)
        {
            TryLogWarning($"TranscriptionResultSink: Delivery failed: {ex.GetType().Name}");
        }
    }

    private async Task<bool> TryRestoreTargetAsync(
        TranscriptionResultRequest request,
        IntPtr target,
        string finalText
    )
    {
        bool hasTargetIdentity =
            request.TargetProcessId != 0 && !string.IsNullOrWhiteSpace(request.TargetWindowClass);
        if (
            !hasTargetIdentity
            || !_isWindowIdentityMatch(target, request.TargetProcessId, request.TargetWindowClass!)
        )
        {
            await LeaveOnClipboardAsync(finalText, "original target").ConfigureAwait(false);
            return false;
        }

        if (
            !ShouldAttemptBestEffortRestore(
                target,
                request.TargetProcessId,
                request.TargetWindowClass,
                foregroundChanged: true
            )
        )
        {
            await LeaveFirefoxOnClipboardAsync(finalText).ConfigureAwait(false);
            return false;
        }

        bool restored =
            _tryRestoreWindow(target, request.TargetProcessId, request.TargetWindowClass!)
            && await WaitForForegroundAsync(target).ConfigureAwait(false);
        if (!restored)
        {
            await LeaveOnClipboardAsync(finalText, "original target").ConfigureAwait(false);
            return false;
        }

        TryLog(
            $"TranscriptionResultSink: Restored original target window 0x{target.ToInt64():X} for best-effort delivery"
        );
        return true;
    }

    private async Task<bool> WaitForForegroundAsync(IntPtr target)
    {
        // Focus transitions are asynchronous; give the shell a short settle window
        // before concluding the restore was rejected.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (_getForegroundWindow() == target)
                return true;
            await Task.Delay(50).ConfigureAwait(false);
        }

        return _getForegroundWindow() == target;
    }

    private async Task LeaveFirefoxOnClipboardAsync(string text)
    {
        bool clipboardSet = await _clipboardService.SetTextAsync(text).ConfigureAwait(false);
        TryLogWarning(
            "TranscriptionResultSink: Firefox target is clipboard-only; automatic paste was skipped"
        );
        NotificationService.ShowWarning(
            "Firefox delivery is clipboard-only. Press Ctrl+V in the original Firefox field to paste."
        );
        if (!clipboardSet)
            TryLogWarning("TranscriptionResultSink: Failed to preserve Firefox text on clipboard");
    }

    private async Task LeaveOnClipboardAsync(string text, string targetDescription)
    {
        bool clipboardSet = await _clipboardService.SetTextAsync(text).ConfigureAwait(false);
        TryLogWarning(
            $"TranscriptionResultSink: Could not restore {targetDescription}; text left on clipboard"
        );
        NotificationService.ShowWarning(
            "The original window could not be restored. The text is on your clipboard — paste manually with Ctrl+V."
        );
        if (!clipboardSet)
            TryLogWarning("TranscriptionResultSink: Failed to preserve text on clipboard");
    }

    private async Task TypeAndObserveAsync(
        string text,
        bool autoPaste,
        CancellationToken cancellationToken
    )
    {
        var typeResult = await _textTyper
            .TypeAsync(text, autoPaste, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!typeResult.DeliverySuccess)
        {
            if (typeResult.WindowChanged && typeResult.TextOnClipboard)
            {
                TryLog(
                    "TranscriptionResultSink: Delivery skipped after the foreground window changed; text preserved on clipboard"
                );
            }
            else
            {
                TryLogWarning(
                    $"TranscriptionResultSink: TextTyper delivery failed (windowChanged={typeResult.WindowChanged}, onClipboard={typeResult.TextOnClipboard})"
                );
            }
        }
    }

    private static void TryLog(string message)
    {
        try
        {
            Logger.Log(message);
        }
        catch { }
    }

    private static void TryLogWarning(string message)
    {
        try
        {
            Logger.LogWarning(message);
        }
        catch { }
    }
}
