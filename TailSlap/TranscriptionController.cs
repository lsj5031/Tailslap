using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

public sealed class TranscriptionController : ITranscriptionController
{
    private readonly IConfigService _config;
    private readonly IClipboardService _clip;
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;
    private readonly IAudioRecorderFactory _audioRecorderFactory;
    private readonly IHistoryService _history;

    private bool _isTranscribing;
    private CancellationTokenSource? _transcriberCts;

    public bool IsTranscribing => _isTranscribing;
    public bool IsRecording => _transcriberCts != null && !_transcriberCts.IsCancellationRequested;

    public event Action? OnStarted;
    public event Action? OnCompleted;

    public TranscriptionController(
        IConfigService config,
        IClipboardService clip,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IAudioRecorderFactory audioRecorderFactory,
        IHistoryService history)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _remoteTranscriberFactory = remoteTranscriberFactory ?? throw new ArgumentNullException(nameof(remoteTranscriberFactory));
        _audioRecorderFactory = audioRecorderFactory ?? throw new ArgumentNullException(nameof(audioRecorderFactory));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public async Task<bool> TriggerTranscribeAsync()
    {
        var cfg = _config.CreateValidatedCopy();

        if (!cfg.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            NotificationService.ShowWarning("Remote transcription is disabled. Enable it in settings first.");
            return false;
        }

        // If recording is in progress, stop it
        if (IsRecording)
        {
            Logger.Log("Stopping recording via cancellation token");
            StopRecording();
            return false;
        }

        // If transcription is already in progress but recording is done, wait
        if (_isTranscribing)
        {
            Logger.Log("Transcription already in progress - waiting for completion");
            NotificationService.ShowWarning("Transcription in progress. Please wait for completion.");
            return false;
        }

        Logger.Log("Starting new transcription task");
        _isTranscribing = true;
        OnStarted?.Invoke();

        try
        {
            await TranscribeSelectionAsync(cfg);
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
            _transcriberCts?.Cancel();
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
            Logger.Log($"Transcriber config: BaseUrl={cfg.Transcriber.BaseUrl}, Model={cfg.Transcriber.Model}, Timeout={cfg.Transcriber.TimeoutSeconds}s");

            _transcriberCts = new CancellationTokenSource();
            Logger.Log($"Created new CancellationTokenSource: {_transcriberCts?.GetHashCode()}");

            NotificationService.ShowInfo("🎤 Recording... Press hotkey again to stop.");

            // Record audio from microphone
            audioFilePath = Path.Combine(Path.GetTempPath(), $"tailslap_recording_{Guid.NewGuid():N}.wav");
            Logger.Log($"Audio file path: {audioFilePath}");

            try
            {
                Logger.Log("Starting audio recording from microphone");
                recordingStats = await RecordAudioAsync(audioFilePath, cfg);

                if (recordingStats.SilenceDetected)
                {
                    Logger.Log($"Audio recording stopped early due to silence detection at {recordingStats.DurationMs}ms");
                }
                Logger.Log($"Audio recorded to: {audioFilePath}, duration={recordingStats.DurationMs}ms");

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
                NotificationService.ShowError("Failed to record audio from microphone. Please check your microphone permissions.");
                Logger.Log($"Audio recording failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            NotificationService.ShowInfo("Sending to transcriber...");

            // Transcribe audio using remote API
            Logger.Log($"Creating RemoteTranscriber with BaseUrl: {cfg.Transcriber.BaseUrl}");
            var transcriber = _remoteTranscriberFactory.Create(cfg.Transcriber);
            string transcriptionText = "";

            // Use streaming if requested for faster feedback
            if (cfg.Transcriber.StreamResults)
            {
                transcriptionText = await TranscribeStreamingAsync(transcriber, audioFilePath, cfg);
            }
            else
            {
                transcriptionText = await TranscribeNonStreamingAsync(transcriber, audioFilePath, cfg);
            }

            if (string.IsNullOrEmpty(transcriptionText))
                return;

            // Log transcription to history
            try
            {
                _history.AppendTranscription(transcriptionText, recordingStats?.DurationMs ?? 0);
                Logger.Log($"Transcription logged: {transcriptionText.Length} characters, duration={recordingStats?.DurationMs}ms");
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to log transcription to history: {ex.Message}");
            }

            Logger.Log("Transcription completed successfully.");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError("Transcription failed: " + ex.Message);
            Logger.Log($"TranscribeSelectionAsync error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _transcriberCts?.Dispose();
            _transcriberCts = null;

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
        Logger.Log($"RecordAudioAsync started. PreferredMic: {cfg.Transcriber.PreferredMicrophoneIndex}, EnableVAD: {cfg.Transcriber.EnableVAD}, VADThreshold: {cfg.Transcriber.SilenceThresholdMs}ms");

        using var recorder = _audioRecorderFactory.Create(cfg.Transcriber.PreferredMicrophoneIndex);
        recorder.SetVadThresholds(
            cfg.Transcriber.VadSilenceThreshold,
            cfg.Transcriber.VadActivationThreshold,
            cfg.Transcriber.VadSustainThreshold);

        try
        {
            Logger.Log("Starting recorder with CancellationToken");
            var stats = await recorder.RecordAsync(
                audioFilePath,
                maxDurationMs: 0,
                ct: _transcriberCts?.Token ?? CancellationToken.None,
                enableVAD: cfg.Transcriber.EnableVAD,
                silenceThresholdMs: cfg.Transcriber.SilenceThresholdMs);

            Logger.Log($"Recording completed: {stats.DurationMs}ms, {stats.BytesRecorded} bytes, silence_detected={stats.SilenceDetected}");
            return stats;
        }
        catch (OperationCanceledException)
        {
            Logger.Log("Recording cancelled");
            throw;
        }
    }

    private async Task<string> TranscribeStreamingAsync(IRemoteTranscriber transcriber, string audioFilePath, AppConfig cfg)
    {
        Logger.Log("Using streaming transcription for faster feedback");
        var fullText = new System.Text.StringBuilder();

        try
        {
            await foreach (var chunk in transcriber.TranscribeStreamingAsync(audioFilePath))
            {
                if (string.IsNullOrEmpty(chunk))
                    continue;

                fullText.Append(chunk);
            }

            var transcriptionText = fullText.ToString();
            Logger.Log($"Streaming transcription completed: {transcriptionText.Length} characters");

            if (IsEmptyTranscription(transcriptionText))
            {
                NotificationService.ShowWarning("No speech detected.");
                return "";
            }

            _clip.SetText(transcriptionText);

            if (!cfg.Transcriber.AutoPaste)
            {
                NotificationService.ShowTextReadyNotification();
            }

            return transcriptionText;
        }
        catch (TranscriberException ex)
        {
            Logger.Log($"Streaming TranscriberException: ErrorType={ex.ErrorType}, StatusCode={ex.StatusCode}, Message={ex.Message}");
            NotificationService.ShowError($"Transcription failed: {ex.Message}");
            return "";
        }
        catch (Exception ex)
        {
            Logger.Log($"Streaming unexpected exception: {ex.GetType().Name}: {ex.Message}");
            NotificationService.ShowError($"Transcription failed: {ex.Message}");
            return "";
        }
    }

    private async Task<string> TranscribeNonStreamingAsync(IRemoteTranscriber transcriber, string audioFilePath, AppConfig cfg)
    {
        try
        {
            Logger.Log($"Starting remote transcription of {audioFilePath}");
            var transcriptionText = await transcriber.TranscribeAudioAsync(audioFilePath);
            Logger.Log($"Transcription completed: {transcriptionText?.Length ?? 0} characters");

            if (IsEmptyTranscription(transcriptionText))
            {
                NotificationService.ShowWarning("No speech detected.");
                return "";
            }

            bool setTextSuccess = _clip.SetText(transcriptionText ?? "");
            if (!setTextSuccess)
            {
                return "";
            }

            await Task.Delay(100);

            if (cfg.Transcriber.AutoPaste)
            {
                Logger.Log("Transcriber auto-paste attempt");
                bool pasteSuccess = await _clip.PasteAsync();
                if (!pasteSuccess)
                {
                    NotificationService.ShowInfo("Transcription is ready. You can paste manually with Ctrl+V.");
                }
            }
            else
            {
                NotificationService.ShowTextReadyNotification();
            }

            return transcriptionText ?? "";
        }
        catch (TranscriberException ex)
        {
            Logger.Log($"TranscriberException: ErrorType={ex.ErrorType}, StatusCode={ex.StatusCode}, Message={ex.Message}");
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
