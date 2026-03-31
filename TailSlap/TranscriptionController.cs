using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

public sealed class TranscriptionController : ITranscriptionController
{
    private readonly IConfigService _config;
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;
    private readonly IAudioRecorderFactory _audioRecorderFactory;
    private readonly IHistoryService _history;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly ClipboardHelper _clipboardHelper;

    private bool _isTranscribing;
    private bool _isRecording;
    private CancellationTokenSource? _recordingCts;

    public bool IsTranscribing => _isTranscribing;
    public bool IsRecording => _isRecording;

    public event Action? OnStarted;
    public event Action? OnCompleted;

    public TranscriptionController(
        IConfigService config,
        ClipboardHelper clipboardHelper,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IAudioRecorderFactory audioRecorderFactory,
        IHistoryService history,
        ITextRefinerFactory textRefinerFactory
    )
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clipboardHelper =
            clipboardHelper ?? throw new ArgumentNullException(nameof(clipboardHelper));
        _remoteTranscriberFactory =
            remoteTranscriberFactory
            ?? throw new ArgumentNullException(nameof(remoteTranscriberFactory));
        _audioRecorderFactory =
            audioRecorderFactory ?? throw new ArgumentNullException(nameof(audioRecorderFactory));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _textRefinerFactory =
            textRefinerFactory ?? throw new ArgumentNullException(nameof(textRefinerFactory));
    }

    public async Task<bool> TriggerTranscribeAsync()
    {
        var cfg = _config.CreateValidatedCopy();

        if (!cfg.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            NotificationService.ShowWarning(
                "Remote transcription is disabled. Enable it in settings first."
            );
            return false;
        }

        // If recording is in progress, stop it
        if (IsRecording)
        {
            Logger.Log("Stopping recording via cancellation token");
            StopRecording();
            return false;
        }

        // If transcription is already in progress, wait
        if (_isTranscribing)
        {
            Logger.Log("Transcription already in progress - waiting for completion");
            NotificationService.ShowWarning(
                "Transcription in progress. Please wait for completion."
            );
            return false;
        }

        Logger.Log("Starting new transcription task");
        _isTranscribing = true;
        OnStarted?.Invoke();

        try
        {
            await TranscribeSelectionAsync(cfg).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"CRITICAL: Transcription task failed at top level: {ex.Message}");
            return false;
        }
        finally
        {
            Logger.Log("Transcription task completed top-level finally");
            _isTranscribing = false;
            OnCompleted?.Invoke();
        }
    }

    public void StopRecording()
    {
        try
        {
            _recordingCts?.Cancel();
            NotificationService.ShowInfo("Stopping recording... Processing audio.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error cancelling transcription task: {ex.Message}");
        }
    }

    private async Task TranscribeSelectionAsync(AppConfig cfg)
    {
        string audioFilePath = "";
        RecordingStats? recordingStats = null;

        try
        {
            Logger.Log("TranscribeSelectionAsync started");
            Logger.Log(
                $"Transcriber config: BaseUrl={cfg.Transcriber.BaseUrl}, Model={cfg.Transcriber.Model}, Timeout={cfg.Transcriber.TimeoutSeconds}s"
            );

            _recordingCts = new CancellationTokenSource();
            Logger.Log(
                $"Created new recording CancellationTokenSource: {_recordingCts?.GetHashCode()}"
            );

            NotificationService.ShowInfo("Recording... Press hotkey again to stop.");

            // Record audio from microphone
            audioFilePath = Path.Combine(
                Path.GetTempPath(),
                $"tailslap_recording_{Guid.NewGuid():N}.wav"
            );
            Logger.Log($"Audio file path: {audioFilePath}");

            try
            {
                Logger.Log("Starting audio recording from microphone");
                _isRecording = true;
                recordingStats = await RecordAudioAsync(audioFilePath, cfg).ConfigureAwait(false);

                if (recordingStats.SilenceDetected)
                {
                    Logger.Log(
                        $"Audio recording stopped early due to silence detection at {recordingStats.DurationMs}ms"
                    );
                }
                Logger.Log(
                    $"Audio recorded to: {audioFilePath}, duration={recordingStats.DurationMs}ms"
                );

                if (recordingStats.DurationMs < 500)
                {
                    Logger.Log("Recording too short (< 500ms), skipping transcription.");
                    NotificationService.ShowWarning("Recording too short. Please speak longer.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Audio recording was stopped by user");
                if (recordingStats != null && recordingStats.DurationMs < 500)
                {
                    NotificationService.ShowWarning("Recording cancelled (too short).");
                    return;
                }
            }
            catch (Exception ex)
            {
                NotificationService.ShowError(
                    "Failed to record audio from microphone. Please check your microphone permissions."
                );
                Logger.Log($"Audio recording failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            finally
            {
                _isRecording = false;
            }

            NotificationService.ShowInfo("Sending to transcriber...");

            // Transcribe audio using remote API
            Logger.Log($"Creating RemoteTranscriber with BaseUrl: {cfg.Transcriber.BaseUrl}");
            var transcriber = _remoteTranscriberFactory.Create(cfg.Transcriber);

            var transcriptionText = await TranscribeRecordedAudioAsync(
                    transcriber,
                    audioFilePath,
                    cfg
                )
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(transcriptionText))
                return;

            // Auto-enhance if enabled and transcription is long enough
            var finalText = await MaybeEnhanceTranscriptionAsync(transcriptionText, cfg)
                .ConfigureAwait(false);

            await _clipboardHelper
                .SetTextAndPasteAsync(finalText, cfg.Transcriber.AutoPaste)
                .ConfigureAwait(false);

            PersistHistoryEntries(
                transcriptionText,
                finalText,
                cfg,
                recordingStats?.DurationMs ?? 0
            );

            Logger.Log("Transcription completed successfully.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError("Transcription failed: " + ex.Message);
            Logger.Log($"TranscribeSelectionAsync error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _recordingCts?.Dispose();
            _recordingCts = null;

            // Clean up temporary audio file
            try
            {
                if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
                {
                    File.Delete(audioFilePath);
                }
            }
            catch { }
        }
    }

    private async Task<RecordingStats> RecordAudioAsync(string audioFilePath, AppConfig cfg)
    {
        Logger.Log(
            $"RecordAudioAsync started. PreferredMic: {cfg.Transcriber.PreferredMicrophoneIndex}, EnableVAD: {cfg.Transcriber.EnableVAD}, VADThreshold: {cfg.Transcriber.SilenceThresholdMs}ms"
        );

        using var recorder = _audioRecorderFactory.Create(cfg.Transcriber.PreferredMicrophoneIndex);
        recorder.SetVadThresholds(
            cfg.Transcriber.VadSilenceThreshold,
            cfg.Transcriber.VadActivationThreshold,
            cfg.Transcriber.VadSustainThreshold
        );

        recorder.SetUseWebRtcVad(cfg.Transcriber.UseWebRtcVad);
        if (cfg.Transcriber.UseWebRtcVad)
        {
            recorder.SetWebRtcVadSensitivity((VadSensitivity)cfg.Transcriber.WebRtcVadSensitivity);
        }

        try
        {
            Logger.Log("Starting recorder with CancellationToken");
            var stats = await recorder
                .RecordAsync(
                    audioFilePath,
                    maxDurationMs: 0,
                    ct: _recordingCts?.Token ?? CancellationToken.None,
                    enableVAD: cfg.Transcriber.EnableVAD,
                    silenceThresholdMs: cfg.Transcriber.SilenceThresholdMs
                )
                .ConfigureAwait(false);

            Logger.Log(
                $"Recording completed: {stats.DurationMs}ms, {stats.BytesRecorded} bytes, silence_detected={stats.SilenceDetected}"
            );
            return stats;
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Recording cancelled");
            throw;
        }
    }

    private async Task<string> TranscribeRecordedAudioAsync(
        IRemoteTranscriber transcriber,
        string audioFilePath,
        AppConfig cfg
    )
    {
        try
        {
            Logger.Log($"Starting remote transcription of {audioFilePath}");
            var transcriptionText = await transcriber
                .TranscribeAudioAsync(audioFilePath)
                .ConfigureAwait(false);
            Logger.Log($"Transcription completed: {transcriptionText?.Length ?? 0} characters");

            if (IsEmptyTranscription(transcriptionText))
            {
                NotificationService.ShowWarning("No speech detected.");
                return "";
            }

            return transcriptionText ?? "";
        }
        catch (TranscriberException ex)
        {
            Logger.Log(
                $"TranscriberException: ErrorType={ex.ErrorType}, StatusCode={ex.StatusCode}, Message={ex.Message}"
            );
            NotificationService.ShowError($"Transcription failed: {ex.Message}");
            return "";
        }
        catch (Exception ex)
        {
            Logger.Log($"Unexpected exception: {ex.GetType().Name}: {ex.Message}");
            NotificationService.ShowError($"Transcription failed: {ex.Message}");
            return "";
        }
    }

    private async Task<string> MaybeEnhanceTranscriptionAsync(
        string transcriptionText,
        AppConfig cfg
    )
    {
        if (!cfg.Transcriber.EnableAutoEnhance)
            return transcriptionText;

        if (transcriptionText.Length < cfg.Transcriber.AutoEnhanceThresholdChars)
            return transcriptionText;

        if (!cfg.Llm.Enabled)
        {
            Logger.Log("Auto-enhancement skipped: LLM is disabled");
            return transcriptionText;
        }

        try
        {
            Logger.Log(
                $"Auto-enhancing transcription ({transcriptionText.Length} chars >= {cfg.Transcriber.AutoEnhanceThresholdChars} threshold)"
            );
            NotificationService.ShowInfo("Enhancing transcription with LLM...");

            var enhancementConfig = cfg.Llm.Clone();
            enhancementConfig.Temperature = Math.Min(enhancementConfig.Temperature, 0.2);
            var refiner = _textRefinerFactory.Create(enhancementConfig);
            var enhanced = await refiner.RefineAsync(transcriptionText).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(enhanced) && enhanced.Length > 0)
            {
                if (!ShouldUseEnhancedText(transcriptionText, enhanced, out var rejectionReason))
                {
                    Logger.Log(
                        $"Auto-enhancement rejected: {rejectionReason}. Keeping original transcription."
                    );
                    NotificationService.ShowWarning(
                        "Enhancement looked unreliable. Using the original transcription."
                    );
                    return transcriptionText;
                }

                Logger.Log(
                    $"Transcription enhanced: {transcriptionText.Length} -> {enhanced.Length} chars"
                );
                NotificationService.ShowSuccess("Transcription enhanced!");
                return enhanced;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Auto-enhancement failed: {ex.Message}. Using original transcription.");
            NotificationService.ShowWarning("Enhancement failed. Using original transcription.");
        }

        return transcriptionText;
    }

    private void PersistHistoryEntries(
        string transcriptionText,
        string finalText,
        AppConfig cfg,
        int recordingDurationMs
    )
    {
        try
        {
            _history.AppendTranscription(transcriptionText, recordingDurationMs);
            Logger.Log(
                $"Raw transcription logged: {transcriptionText.Length} characters, duration={recordingDurationMs}ms"
            );
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to log transcription to history: {ex.Message}");
        }

        if (string.Equals(transcriptionText, finalText, StringComparison.Ordinal))
            return;

        try
        {
            _history.Append(transcriptionText, finalText, cfg.Llm.Model);
            Logger.Log(
                $"Enhanced transcription logged to refinement history: {finalText.Length} characters, model={cfg.Llm.Model}"
            );
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to log enhanced transcription to refinement history: {ex.Message}");
        }
    }

    internal static bool ShouldUseEnhancedText(
        string original,
        string enhanced,
        out string rejectionReason
    )
    {
        var originalTrimmed = original?.Trim() ?? string.Empty;
        var enhancedTrimmed = enhanced?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(enhancedTrimmed))
        {
            rejectionReason = "empty enhancement";
            return false;
        }

        if (string.Equals(originalTrimmed, enhancedTrimmed, StringComparison.Ordinal))
        {
            rejectionReason = string.Empty;
            return true;
        }

        if (
            originalTrimmed.Length >= 80
            && enhancedTrimmed.Length < Math.Max(20, originalTrimmed.Length / 2)
        )
        {
            rejectionReason =
                $"enhancement shrank too far ({originalTrimmed.Length} -> {enhancedTrimmed.Length})";
            return false;
        }

        var originalWords = SplitWords(originalTrimmed);
        var enhancedWords = SplitWords(enhancedTrimmed);
        if (originalWords.Length >= 6 && enhancedWords.Length > 0)
        {
            int sharedWords = enhancedWords.Count(word => originalWords.Contains(word));
            double overlap = sharedWords / (double)enhancedWords.Length;
            if (overlap < 0.35)
            {
                rejectionReason = $"lexical overlap too low ({overlap:F2})";
                return false;
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static string[] SplitWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(
                new[]
                {
                    ' ',
                    '\t',
                    '\r',
                    '\n',
                    '.',
                    ',',
                    ';',
                    ':',
                    '!',
                    '?',
                    '(',
                    ')',
                    '[',
                    ']',
                    '{',
                    '}',
                    '"',
                    '\'',
                    '-',
                    '_',
                    '/',
                },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
    }

    private static bool IsEmptyTranscription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmed = text.Trim();
        return trimmed.Equals("[Empty transcription]", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("(empty)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("[silence]", StringComparison.OrdinalIgnoreCase);
    }
}
