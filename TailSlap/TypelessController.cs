using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

/// <summary>
/// Push-to-talk transcription controller.
/// State machine: Idle → Recording → Processing → Idle
/// </summary>
public sealed class TypelessController : ITypelessController
{
    private readonly IConfigService _config;
    private readonly ClipboardHelper _clipboardHelper;
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;
    private readonly IAudioRecorderFactory _audioRecorderFactory;
    private readonly IHistoryService _history;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly TextTyper _textTyper;

    /// <summary>
    /// Recording delegate — can be overridden in tests to avoid needing a real AudioRecorder.
    /// Production code uses DefaultRecordAsync which creates a real AudioRecorder.
    /// </summary>
    private readonly Func<AppConfig, string, CancellationToken, Task<RecordingStats>> _recordFunc;

    private readonly object _stateLock = new();

    private enum ControllerState
    {
        Idle,
        Recording,
        Processing,
    }

    private ControllerState _state = ControllerState.Idle;
    private CancellationTokenSource? _recordingCts;
    private string? _tempWavPath;
    private Task? _recordingTask;
    private RecordingStats? _recordingStats;

    public bool IsRecording
    {
        get
        {
            lock (_stateLock)
            {
                return _state == ControllerState.Recording;
            }
        }
    }

    public bool IsProcessing
    {
        get
        {
            lock (_stateLock)
            {
                return _state == ControllerState.Processing;
            }
        }
    }

    public event Action? OnStarted;
    public event Action? OnCompleted;

    /// <summary>
    /// Creates a TypelessController for production use with a real AudioRecorder.
    /// </summary>
    public TypelessController(
        IConfigService config,
        ClipboardHelper clipboardHelper,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IAudioRecorderFactory audioRecorderFactory,
        IHistoryService history,
        ITextRefinerFactory textRefinerFactory,
        TextTyper textTyper
    )
        : this(
            config,
            clipboardHelper,
            remoteTranscriberFactory,
            audioRecorderFactory,
            history,
            textRefinerFactory,
            textTyper,
            DefaultRecordAsync
        ) { }

    /// <summary>
    /// Creates a TypelessController with a custom recording function (for testing).
    /// </summary>
    internal TypelessController(
        IConfigService config,
        ClipboardHelper clipboardHelper,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IAudioRecorderFactory audioRecorderFactory,
        IHistoryService history,
        ITextRefinerFactory textRefinerFactory,
        TextTyper textTyper,
        Func<AppConfig, string, CancellationToken, Task<RecordingStats>> recordFunc
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
        _textTyper = textTyper ?? throw new ArgumentNullException(nameof(textTyper));
        _recordFunc = recordFunc ?? throw new ArgumentNullException(nameof(recordFunc));
    }

    /// <summary>
    /// Default recording implementation using AudioRecorder.
    /// </summary>
    private static async Task<RecordingStats> DefaultRecordAsync(
        AppConfig cfg,
        string outputPath,
        CancellationToken ct
    )
    {
        using var recorder = new AudioRecorder(cfg.Transcriber.PreferredMicrophoneIndex);
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

        return await recorder
            .RecordAsync(
                outputPath,
                maxDurationMs: 0,
                ct: ct,
                enableVAD: cfg.Transcriber.EnableVAD,
                silenceThresholdMs: cfg.Transcriber.SilenceThresholdMs
            )
            .ConfigureAwait(false);
    }

    public Task HandleKeyDownAsync()
    {
        lock (_stateLock)
        {
            if (_state == ControllerState.Recording)
            {
                // Auto-repeat suppression: ignore
                return Task.CompletedTask;
            }

            if (_state == ControllerState.Processing)
            {
                try
                {
                    Logger.Log("TypelessController: Key-down rejected, transcription in progress");
                }
                catch { }

                NotificationService.ShowWarning("Transcription in progress. Please wait.");
                return Task.CompletedTask;
            }
        }

        // State is Idle — check if transcriber is enabled
        var cfg = _config.CreateValidatedCopy();
        if (!cfg.Transcriber.Enabled)
        {
            try
            {
                Logger.Log("TypelessController: Transcriber disabled, ignoring key-down");
            }
            catch { }

            return Task.CompletedTask;
        }

        // Start recording
        lock (_stateLock)
        {
            _state = ControllerState.Recording;
        }

        // Reset the TextTyper baseline for this new transcription session
        _textTyper.ResetBaseline();

        _tempWavPath = Path.Combine(
            Path.GetTempPath(),
            $"tailslap_typeless_{Guid.NewGuid():N}.wav"
        );

        _recordingCts = new CancellationTokenSource();

        try
        {
            Logger.Log("TypelessController: Recording started");
        }
        catch { }

        OnStarted?.Invoke();

        // Fire-and-forget recording task — we'll await the result in HandleKeyUpAsync
        _recordingTask = Task.Run(() => RunRecordingAsync(cfg));

        return Task.CompletedTask;
    }

    public async Task HandleKeyUpAsync()
    {
        lock (_stateLock)
        {
            if (_state != ControllerState.Recording)
            {
                return;
            }
        }

        // Stop recording by cancelling the CTS
        try
        {
            _recordingCts?.Cancel();
        }
        catch { }

        // Wait for recording to finish
        if (_recordingTask != null)
        {
            try
            {
                await _recordingTask.ConfigureAwait(false);
            }
            catch
            {
                // Recording task may throw due to cancellation; that's expected
            }
        }

        // Check recording duration
        var stats = _recordingStats;
        if (stats == null || stats.DurationMs < 500)
        {
            try
            {
                Logger.Log(
                    $"TypelessController: Recording too short ({stats?.DurationMs ?? 0}ms < 500ms), discarding"
                );
            }
            catch { }

            NotificationService.ShowWarning("Recording too short. Please speak longer.");

            CleanupTempFile();
            ReturnToIdle();
            return;
        }

        // Transition to Processing
        lock (_stateLock)
        {
            _state = ControllerState.Processing;
        }

        try
        {
            Logger.Log("TypelessController: Starting transcription");
        }
        catch { }

        string tempWavPath = _tempWavPath!;

        try
        {
            await TranscribeAsync(tempWavPath, stats.DurationMs, cfg: _config.CreateValidatedCopy())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log(
                    $"TypelessController: Transcription failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }

            NotificationService.ShowError($"Transcription failed: {ex.Message}");
        }
        finally
        {
            CleanupTempFile();
            ReturnToIdle();
        }
    }

    private async Task RunRecordingAsync(AppConfig cfg)
    {
        try
        {
            _recordingStats = await _recordFunc(
                    cfg,
                    _tempWavPath!,
                    _recordingCts?.Token ?? CancellationToken.None
                )
                .ConfigureAwait(false);

            try
            {
                Logger.Log(
                    $"TypelessController: Recording completed, duration={_recordingStats.DurationMs}ms, bytes={_recordingStats.BytesRecorded}"
                );
            }
            catch { }
        }
        catch (OperationCanceledException)
        {
            // Expected when user releases key
            try
            {
                Logger.Log("TypelessController: Recording cancelled by user");
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log(
                    $"TypelessController: Recording failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }

            NotificationService.ShowError(
                "Failed to record audio. Please check your microphone permissions."
            );

            // Ensure we return to idle
            CleanupTempFile();
            ReturnToIdle();
        }
    }

    private async Task TranscribeAsync(string wavPath, int durationMs, AppConfig cfg)
    {
        if (!File.Exists(wavPath))
        {
            try
            {
                Logger.Log("TypelessController: WAV file not found, skipping transcription");
            }
            catch { }

            return;
        }

        var transcriber = _remoteTranscriberFactory.Create(cfg.Transcriber);
        var fullText = new StringBuilder();

        try
        {
            await foreach (
                var chunk in transcriber.TranscribeStreamingAsync(wavPath).ConfigureAwait(false)
            )
            {
                if (string.IsNullOrEmpty(chunk))
                    continue;

                fullText.Append(chunk);

                try
                {
                    Logger.Log(
                        $"TypelessController: SSE chunk received, len={chunk.Length}, sha256={Hashing.Sha256Hex(chunk)}"
                    );
                }
                catch { }

                // Type the accumulated text into the focused application
                try
                {
                    await _textTyper
                        .TypeAsync(fullText.ToString(), autoPaste: cfg.Transcriber.AutoPaste)
                        .ConfigureAwait(false);
                }
                catch (Exception typeEx)
                {
                    try
                    {
                        Logger.Log(
                            $"TypelessController: Failed to type SSE chunk: {typeEx.Message}"
                        );
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            // Preserve whatever text was typed so far from partial SSE results
            if (fullText.Length > 0)
            {
                try
                {
                    Logger.Log(
                        $"TypelessController: Partial SSE results preserved ({fullText.Length} chars) after error: {ex.Message}"
                    );
                }
                catch { }
            }
            else
            {
                throw;
            }
        }

        var transcriptionText = fullText.ToString();

        if (string.IsNullOrWhiteSpace(transcriptionText))
        {
            try
            {
                Logger.Log("TypelessController: No speech detected");
            }
            catch { }

            NotificationService.ShowWarning("No speech detected.");
            return;
        }

        // Save to history
        try
        {
            _history.AppendTranscription(transcriptionText, durationMs);
            Logger.Log(
                $"TypelessController: History entry saved, len={transcriptionText.Length}, duration={durationMs}ms"
            );
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"TypelessController: Failed to save history: {ex.Message}");
            }
            catch { }
        }

        try
        {
            Logger.Log(
                $"TypelessController: Transcription completed, sha256={Hashing.Sha256Hex(transcriptionText)}"
            );
        }
        catch { }
    }

    private void CleanupTempFile()
    {
        try
        {
            if (_tempWavPath != null && File.Exists(_tempWavPath))
            {
                File.Delete(_tempWavPath);
            }
        }
        catch { }
        finally
        {
            _tempWavPath = null;
        }
    }

    private void ReturnToIdle()
    {
        lock (_stateLock)
        {
            _state = ControllerState.Idle;
        }

        try
        {
            _recordingCts?.Dispose();
        }
        catch { }

        _recordingCts = null;
        _recordingStats = null;
        _recordingTask = null;

        try
        {
            Logger.Log("TypelessController: Returned to Idle");
        }
        catch { }

        OnCompleted?.Invoke();
    }
}
