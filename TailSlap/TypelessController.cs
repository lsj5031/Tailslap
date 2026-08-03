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
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;
    private readonly IAudioRecorderFactory _audioRecorderFactory;
    private readonly TextTyper _textTyper;
    private readonly ITranscriptionResultSink _resultSink;

    /// <summary>
    /// Recording delegate — can be overridden in tests to avoid needing a real AudioRecorder.
    /// Production code uses DefaultRecordAsync which creates a real AudioRecorder.
    /// </summary>
    private readonly Func<AppConfig, string, CancellationToken, Task<RecordingStats>>? _recordFunc;

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
    private AudioRecorder? _currentRecorder;
    private IntPtr _targetWindow;
    private uint _targetProcessId;
    private string _targetWindowClass = string.Empty;
    private readonly Func<IntPtr> _getForegroundWindow;

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
    public event Action? OnProcessingStarted;
    public event Action? OnCompleted;
    public event Action<float>? OnRmsLevel;

    /// <summary>
    /// Creates a TypelessController for production use with a real AudioRecorder.
    /// </summary>
    internal TypelessController(
        IConfigService config,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IAudioRecorderFactory audioRecorderFactory,
        TextTyper textTyper,
        ITranscriptionResultSink resultSink
    )
        : this(
            config,
            remoteTranscriberFactory,
            audioRecorderFactory,
            textTyper,
            resultSink,
            null!,
            null
        )
    {
        _recordFunc = DefaultRecordAsync;
    }

    /// <summary>
    /// Creates a TypelessController with a custom recording function (for testing).
    /// </summary>
    internal TypelessController(
        IConfigService config,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IAudioRecorderFactory audioRecorderFactory,
        TextTyper textTyper,
        ITranscriptionResultSink resultSink,
        Func<AppConfig, string, CancellationToken, Task<RecordingStats>> recordFunc,
        Func<IntPtr>? getForegroundWindow = null
    )
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _remoteTranscriberFactory =
            remoteTranscriberFactory
            ?? throw new ArgumentNullException(nameof(remoteTranscriberFactory));
        _audioRecorderFactory =
            audioRecorderFactory ?? throw new ArgumentNullException(nameof(audioRecorderFactory));
        _textTyper = textTyper ?? throw new ArgumentNullException(nameof(textTyper));
        _resultSink = resultSink ?? throw new ArgumentNullException(nameof(resultSink));
        _recordFunc = recordFunc; // null for production; set after by public constructor
        _getForegroundWindow = getForegroundWindow ?? (() => NativeMethods.GetForegroundWindow());
    }

    /// <summary>
    /// Default recording implementation using AudioRecorder.
    /// Instance method so it can forward RMS events to OnRmsLevel.
    /// </summary>
    private async Task<RecordingStats> DefaultRecordAsync(
        AppConfig cfg,
        string outputPath,
        CancellationToken ct
    )
    {
        using var recorder = new AudioRecorder(cfg.Transcriber.PreferredMicrophoneIndex);
        _currentRecorder = recorder;
        recorder.OnRmsLevel += rms =>
        {
            try
            {
                OnRmsLevel?.Invoke(rms);
            }
            catch { }
        };

        try
        {
            recorder.SetVadThresholds(
                cfg.Transcriber.VadSilenceThreshold,
                cfg.Transcriber.VadActivationThreshold,
                cfg.Transcriber.VadSustainThreshold
            );
            recorder.SetUseWebRtcVad(cfg.Transcriber.UseWebRtcVad);
            if (cfg.Transcriber.UseWebRtcVad)
            {
                recorder.SetWebRtcVadSensitivity(
                    (VadSensitivity)cfg.Transcriber.WebRtcVadSensitivity
                );
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
        finally
        {
            _currentRecorder = null;
        }
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

        // Capture the target before the overlay/recorder can affect focus. The
        // final delivery uses this handle to avoid pasting into another app.
        IntPtr targetWindow = _getForegroundWindow();
        NativeMethods.TryGetWindowIdentity(
            targetWindow,
            out uint targetProcessId,
            out string targetWindowClass
        );

        // Start recording
        lock (_stateLock)
        {
            if (_state != ControllerState.Idle)
                return Task.CompletedTask;

            _state = ControllerState.Recording;
            _targetWindow = targetWindow;
            _targetProcessId = targetProcessId;
            _targetWindowClass = targetWindowClass;
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
        IntPtr targetWindow;
        uint targetProcessId;
        string targetWindowClass;
        lock (_stateLock)
        {
            if (_state != ControllerState.Recording)
            {
                // Idle: key-up without a recording (e.g. a stray Alt tap) — no-op.
                // Processing: a key-up is not relevant.
                return;
            }

            // Atomically claim this recording so concurrent key-up callbacks cannot
            // stop and transcribe the same session more than once.
            targetWindow = _targetWindow;
            targetProcessId = _targetProcessId;
            targetWindowClass = _targetWindowClass;
            _state = ControllerState.Processing;
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

        try
        {
            Logger.Log("TypelessController: Starting transcription");
        }
        catch { }

        OnProcessingStarted?.Invoke();

        string tempWavPath = _tempWavPath!;

        try
        {
            await TranscribeAsync(
                    tempWavPath,
                    stats.DurationMs,
                    targetWindow,
                    targetProcessId,
                    targetWindowClass,
                    cfg: _config.CreateValidatedCopy()
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Error(
                    $"TypelessController: Transcription failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }

            NotificationService.ShowError($"Transcription failed: {ex.Message}");

            try
            {
                _resultSink.RecordFailure(
                    partialText: null,
                    durationMs: stats?.DurationMs ?? 0,
                    errorSummary: ex.Message
                );
            }
            catch { }
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
            _recordingStats = await _recordFunc!(
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
                Logger.Error(
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

    private async Task TranscribeAsync(
        string wavPath,
        int durationMs,
        IntPtr targetWindow,
        uint targetProcessId,
        string targetWindowClass,
        AppConfig cfg
    )
    {
        if (!File.Exists(wavPath))
        {
            try
            {
                Logger.LogWarning("TypelessController: WAV file not found, skipping transcription");
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

                // Merge instead of blindly appending: ASR servers often resend an
                // identical chunk or send a final full-transcript snapshot, both of
                // which would otherwise duplicate the delivered text ("pasted twice").
                string previous = fullText.ToString();
                string merged = TextTyper.MergeStreamChunk(previous, chunk);
                if (merged.Length != previous.Length + chunk.Length)
                {
                    try
                    {
                        Logger.Log(
                            $"TypelessController: SSE chunk deduplicated (resent/snapshot), grew by {merged.Length - previous.Length} chars instead of {chunk.Length}"
                        );
                    }
                    catch { }
                }
                fullText.Clear();
                fullText.Append(merged);

                try
                {
                    Logger.Log(
                        $"TypelessController: SSE chunk received, len={chunk.Length}, sha256={Hashing.Sha256Hex(chunk)}"
                    );
                }
                catch { }

                // No per-chunk delivery: every chunk used to trigger its own paste/SendKeys,
                // which pressed keys in the target app repeatedly and pasted word-by-word.
                // Instead we accumulate the full transcript and deliver ONCE below.
            }
        }
        catch (Exception ex)
        {
            // Preserve whatever text was typed so far from partial SSE results
            if (fullText.Length > 0)
            {
                try
                {
                    Logger.LogWarning(
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

        var result = await _resultSink
            .ProcessAsync(
                new TranscriptionResultRequest(
                    transcriptionText,
                    cfg,
                    durationMs,
                    TranscriptionDeliveryPolicy.DeliverFinalText,
                    ResultsAlreadyStreamed: false,
                    TargetWindow: targetWindow,
                    TargetProcessId: targetProcessId,
                    TargetWindowClass: targetWindowClass
                )
            )
            .ConfigureAwait(false);

        try
        {
            Logger.Log(
                $"TypelessController: Transcription completed, sha256={Hashing.Sha256Hex(result.FinalText)}"
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
            _targetWindow = IntPtr.Zero;
            _targetProcessId = 0;
            _targetWindowClass = string.Empty;
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
