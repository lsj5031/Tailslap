using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailSlap;

public sealed class RealtimeTranscriptionController : IRealtimeTranscriptionController
{
    private readonly IConfigService _config;
    private readonly IClipboardService _clip;
    private readonly IRealtimeTranscriberFactory _transcriberFactory;
    private readonly IAudioRecorderFactory _audioRecorderFactory;

    private StreamingState _streamingState = StreamingState.Idle;
    private readonly object _streamingStateLock = new();

    private CancellationTokenSource? _transcriberCts;
    private RealtimeTranscriber? _realtimeTranscriber;
    private AudioRecorder? _realtimeRecorder;

    private string _realtimeTranscriptionText = "";
    private string _typedText = "";
    private int _lastTypedLength = 0;
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
    private readonly List<byte> _streamingBuffer = new();
    private const int SEND_BUFFER_SIZE = 16000;
    private IntPtr _streamingTargetWindow = IntPtr.Zero;
    private int _cleanupInProgress = 0;
    private DateTime _streamingStartTime = DateTime.MinValue;
    private const int NO_SPEECH_TIMEOUT_SECONDS = 30;

    public StreamingState State
    {
        get
        {
            lock (_streamingStateLock)
            {
                return _streamingState;
            }
        }
    }

    public bool IsStreaming => State == StreamingState.Streaming;

    public event Action? OnStarted;
    public event Action? OnStopped;
    public event Action<string, bool>? OnTranscription;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public RealtimeTranscriptionController(
        IConfigService config,
        IClipboardService clip,
        IRealtimeTranscriberFactory transcriberFactory,
        IAudioRecorderFactory audioRecorderFactory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _transcriberFactory = transcriberFactory ?? throw new ArgumentNullException(nameof(transcriberFactory));
        _audioRecorderFactory = audioRecorderFactory ?? throw new ArgumentNullException(nameof(audioRecorderFactory));
    }

    public async Task TriggerStreamingAsync()
    {
        var cfg = _config.CreateValidatedCopy();

        if (!cfg.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            NotificationService.ShowWarning("Remote transcription is disabled. Enable it in settings first.");
            return;
        }

        StreamingState currentState;
        lock (_streamingStateLock)
        {
            if (_streamingState == StreamingState.Starting || _streamingState == StreamingState.Stopping)
            {
                Logger.Log($"TriggerStreamingAsync: Ignoring hotkey, transition in progress (state={_streamingState})");
                return;
            }

            if (_streamingState == StreamingState.Streaming)
            {
                _streamingState = StreamingState.Stopping;
            }
            else
            {
                _streamingState = StreamingState.Starting;
            }
            currentState = _streamingState;
        }

        if (currentState == StreamingState.Stopping)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    public async Task StartAsync()
    {
        var cfg = _config.CreateValidatedCopy();

        Logger.Log("StartAsync: Starting real-time WebSocket transcription");
        _realtimeTranscriptionText = "";
        _typedText = "";
        _lastTypedLength = 0;

        try
        {
            NotificationService.ShowInfo("Real-time transcription started. Speak now...");
            OnStarted?.Invoke();

            _realtimeTranscriber = _transcriberFactory.Create(cfg.Transcriber.WebSocketUrl);
            _realtimeTranscriber.OnTranscription += HandleRealtimeTranscriptionEvent;
            _realtimeTranscriber.OnError += HandleRealtimeError;
            _realtimeTranscriber.OnDisconnected += HandleRealtimeDisconnected;

            await _realtimeTranscriber.ConnectAsync();
            Logger.Log("StartAsync: WebSocket connected");

            _realtimeRecorder = _audioRecorderFactory.Create(cfg.Transcriber.PreferredMicrophoneIndex);
            _realtimeRecorder.SetVadThresholds(
                cfg.Transcriber.VadSilenceThreshold,
                cfg.Transcriber.VadActivationThreshold,
                cfg.Transcriber.VadSustainThreshold);
            _realtimeRecorder.OnAudioChunk += HandleRealtimeAudioChunk;
            _realtimeRecorder.OnSilenceDetected += HandleRealtimeSilenceDetected;

            _streamingTargetWindow = GetForegroundWindow();
            _streamingStartTime = DateTime.UtcNow;
            Logger.Log($"StartAsync: Target window captured: 0x{_streamingTargetWindow:X}");

            lock (_streamingStateLock)
            {
                _streamingState = StreamingState.Streaming;
            }

            _transcriberCts = new CancellationTokenSource();
            await _realtimeRecorder.StartStreamingAsync(
                _transcriberCts.Token,
                enableVAD: cfg.Transcriber.EnableVAD,
                silenceThresholdMs: cfg.Transcriber.SilenceThresholdMs);
        }
        catch (Exception ex)
        {
            Logger.Log($"StartAsync: Error - {ex.Message}");
            NotificationService.ShowError($"Real-time transcription failed: {ex.Message}");
            await CleanupAsync();
        }
    }

    public async Task StopAsync()
    {
        Logger.Log("StopAsync: Stopping real-time transcription");
        NotificationService.ShowInfo("Stopping real-time transcription...");

        _realtimeRecorder?.StopRecording();

        if (_realtimeTranscriber?.IsConnected == true)
        {
            try
            {
                var serverClosedTcs = new TaskCompletionSource<bool>();
                var finalMessageTcs = new TaskCompletionSource<bool>();

                void OnServerDisconnected()
                {
                    serverClosedTcs.TrySetResult(true);
                }

                void OnTranscriptionReceived(string text, bool isFinal)
                {
                    if (isFinal)
                        finalMessageTcs.TrySetResult(true);
                }

                _realtimeTranscriber.OnDisconnected += OnServerDisconnected;
                _realtimeTranscriber.OnTranscription += OnTranscriptionReceived;

                byte[]? remainingData = null;
                lock (_streamingBuffer)
                {
                    if (_streamingBuffer.Count > 0)
                    {
                        remainingData = _streamingBuffer.ToArray();
                        _streamingBuffer.Clear();
                    }
                }

                if (remainingData != null)
                {
                    await _realtimeTranscriber.SendAudioChunkAsync(new ArraySegment<byte>(remainingData));
                }

                await _realtimeTranscriber.StopAsync();

                Logger.Log("StopAsync: Waiting for server to close connection or send final message...");
                await Task.WhenAny(serverClosedTcs.Task, finalMessageTcs.Task, Task.Delay(10000));

                _realtimeTranscriber.OnDisconnected -= OnServerDisconnected;
                _realtimeTranscriber.OnTranscription -= OnTranscriptionReceived;

                Logger.Log("StopAsync: Wait complete or timed out");
            }
            catch (Exception ex)
            {
                Logger.Log($"StopAsync: Error sending stop - {ex.Message}");
            }
        }

        _transcriberCts?.Cancel();
        await CleanupAsync();
    }

    private void HandleRealtimeAudioChunk(ArraySegment<byte> chunk)
    {
        StreamingState state;
        lock (_streamingStateLock)
        {
            state = _streamingState;
        }

        if (state != StreamingState.Streaming)
            return;

        if (_streamingStartTime != DateTime.MinValue
            && _realtimeTranscriptionText.Length == 0
            && _typedText.Length == 0
            && (DateTime.UtcNow - _streamingStartTime).TotalSeconds >= NO_SPEECH_TIMEOUT_SECONDS)
        {
            Logger.Log($"HandleRealtimeAudioChunk: No speech detected after {NO_SPEECH_TIMEOUT_SECONDS}s, triggering auto-stop");
            _ = Task.Run(() => HandleRealtimeSilenceDetected());
            return;
        }

        if (_realtimeTranscriber?.IsConnected == true)
        {
            lock (_streamingBuffer)
            {
                if (chunk.Array != null)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        _streamingBuffer.Add(chunk.Array[chunk.Offset + i]);
                    }
                }

                if (_streamingBuffer.Count >= SEND_BUFFER_SIZE)
                {
                    var dataToSend = _streamingBuffer.ToArray();
                    _streamingBuffer.Clear();
                    _ = _realtimeTranscriber.SendAudioChunkAsync(new ArraySegment<byte>(dataToSend));
                }
            }
        }
    }

    private void HandleRealtimeTranscriptionEvent(string text, bool isFinal)
    {
        Logger.Log($"HandleRealtimeTranscriptionEvent: text.Length={text.Length}, final={isFinal}");

        if (!string.IsNullOrEmpty(text) && !isFinal)
        {
            _realtimeRecorder?.NotifySpeechDetected();
        }

        OnTranscription?.Invoke(text, isFinal);

        _ = ProcessTranscriptionAsync(text, isFinal);
    }

    private async Task ProcessTranscriptionAsync(string text, bool isFinal)
    {
        await _transcriptionLock.WaitAsync();
        try
        {
            StreamingState state;
            lock (_streamingStateLock)
            {
                state = _streamingState;
            }
            if (state == StreamingState.Idle)
            {
                Logger.Log("ProcessTranscriptionAsync: Ignoring, state=Idle");
                return;
            }

            if (string.IsNullOrEmpty(text))
                return;

            if (!IsForegroundWindowSafe())
            {
                Logger.Log("ProcessTranscriptionAsync: Window changed, resetting baseline");
                if (_lastTypedLength > 0 && _lastTypedLength <= _realtimeTranscriptionText.Length)
                {
                    _typedText += _realtimeTranscriptionText.Substring(0, _lastTypedLength);
                }
                _realtimeTranscriptionText = text;
                _lastTypedLength = 0;
                _streamingTargetWindow = GetForegroundWindow();
                return;
            }

            string onScreen = _lastTypedLength > 0 && _lastTypedLength <= _realtimeTranscriptionText.Length
                ? _realtimeTranscriptionText.Substring(0, _lastTypedLength)
                : "";

            int commonPrefixLen = 0;
            int minLen = Math.Min(onScreen.Length, text.Length);
            for (int i = 0; i < minLen; i++)
            {
                if (onScreen[i] == text[i])
                    commonPrefixLen++;
                else
                    break;
            }

            int backspaceCount = _lastTypedLength - commonPrefixLen;
            if (backspaceCount < 0)
                backspaceCount = 0;

            if (backspaceCount > 0)
            {
                Logger.Log($"ProcessTranscriptionAsync: Backspacing {backspaceCount} chars for correction");
                SendBackspace(backspaceCount);
                _lastTypedLength = commonPrefixLen;
                await Task.Delay(20);
            }

            if (text.Length > _lastTypedLength)
            {
                var newText = text.Substring(_lastTypedLength);
                Logger.Log($"ProcessTranscriptionAsync: Typing {newText.Length} chars");

                if (newText.Length > 5)
                {
                    bool pasteSuccess = await _clip.SetTextAndPasteAsync(newText);
                    if (!pasteSuccess)
                    {
                        TypeTextDirectly(newText);
                    }
                }
                else
                {
                    TypeTextDirectly(newText);
                }

                _lastTypedLength = text.Length;
            }

            _realtimeTranscriptionText = text;

            if (isFinal)
            {
                Logger.Log("ProcessTranscriptionAsync: Final transcription received");
                _typedText += text;
                _lastTypedLength = 0;
                _realtimeTranscriptionText = "";
            }
        }
        finally
        {
            _transcriptionLock.Release();
        }
    }

    private bool IsForegroundWindowSafe()
    {
        if (_streamingTargetWindow == IntPtr.Zero)
            return true;

        var current = GetForegroundWindow();
        if (current != _streamingTargetWindow)
        {
            Logger.Log($"IsForegroundWindowSafe: Window changed from 0x{_streamingTargetWindow:X} to 0x{current:X}");
            return false;
        }
        return true;
    }

    private void SendBackspace(int count)
    {
        if (count <= 0)
            return;

        if (!IsForegroundWindowSafe())
        {
            Logger.Log($"SendBackspace: Skipping {count} backspaces, foreground window changed");
            return;
        }

        try
        {
            SendKeys.SendWait("{BS " + count + "}");
            SendKeys.Flush();
        }
        catch (Exception ex)
        {
            Logger.Log($"SendBackspace failed: {ex.Message}");
        }
    }

    private static void TypeTextDirectly(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            var escaped = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (c == '+' || c == '^' || c == '%' || c == '~' || c == '(' || c == ')' || c == '{' || c == '}' || c == '[' || c == ']')
                {
                    escaped.Append('{').Append(c).Append('}');
                }
                else if (c == '\n')
                {
                    escaped.Append("{ENTER}");
                }
                else if (c == '\r')
                {
                }
                else
                {
                    escaped.Append(c);
                }
            }

            SendKeys.SendWait(escaped.ToString());
            SendKeys.Flush();
        }
        catch (Exception ex)
        {
            Logger.Log($"TypeTextDirectly failed: {ex.Message}");
        }
    }

    private async void HandleRealtimeError(string error)
    {
        try
        {
            Logger.Log($"HandleRealtimeError: {error}");
            NotificationService.ShowError($"Real-time transcription error: {error}");

            lock (_streamingStateLock)
            {
                if (_streamingState != StreamingState.Streaming)
                {
                    Logger.Log($"HandleRealtimeError: Ignoring stop, state={_streamingState}");
                    return;
                }
                _streamingState = StreamingState.Stopping;
            }

            await StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"HandleRealtimeError: ERROR during handling - {ex.Message}");
        }
    }

    private async void HandleRealtimeDisconnected()
    {
        try
        {
            Logger.Log("HandleRealtimeDisconnected: WebSocket disconnected");
            bool shouldInitiateStop = false;
            lock (_streamingStateLock)
            {
                if (_streamingState == StreamingState.Streaming)
                {
                    _streamingState = StreamingState.Stopping;
                    shouldInitiateStop = true;
                }
                else if (_streamingState == StreamingState.Stopping)
                {
                    _realtimeRecorder?.StopRecording();
                    _transcriberCts?.Cancel();
                    return;
                }
            }

            if (shouldInitiateStop)
            {
                await StopAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"HandleRealtimeDisconnected: ERROR - {ex.Message}");
        }
    }

    private async void HandleRealtimeSilenceDetected()
    {
        try
        {
            Logger.Log("HandleRealtimeSilenceDetected: Silence detected, stopping streaming");

            lock (_streamingStateLock)
            {
                if (_streamingState != StreamingState.Streaming)
                {
                    Logger.Log($"HandleRealtimeSilenceDetected: Ignoring, state={_streamingState}");
                    return;
                }
                _streamingState = StreamingState.Stopping;
            }

            await StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"HandleRealtimeSilenceDetected: ERROR - {ex.Message}");
        }
    }

    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanupInProgress, 1) == 1)
        {
            Logger.Log("CleanupAsync: Already in progress, returning");
            return;
        }

        try
        {
            Logger.Log("CleanupAsync: Cleaning up");

            await _transcriptionLock.WaitAsync();

            string finalTranscriptionText = _realtimeTranscriptionText;
            int finalLastTypedLength = _lastTypedLength;
            string finalTypedText = _typedText;

            _typedText = "";
            _realtimeTranscriptionText = "";
            _lastTypedLength = 0;

            lock (_streamingStateLock)
            {
                _streamingState = StreamingState.Idle;
            }

            _transcriptionLock.Release();

            var transcriber = _realtimeTranscriber;
            var recorder = _realtimeRecorder;
            var cts = _transcriberCts;

            _realtimeTranscriber = null;
            _realtimeRecorder = null;
            _transcriberCts = null;

            if (transcriber != null)
            {
                transcriber.OnTranscription -= HandleRealtimeTranscriptionEvent;
                transcriber.OnError -= HandleRealtimeError;
                transcriber.OnDisconnected -= HandleRealtimeDisconnected;

                try
                {
                    await transcriber.DisconnectAsync();
                }
                catch { }

                transcriber.Dispose();
            }

            if (recorder != null)
            {
                recorder.OnAudioChunk -= HandleRealtimeAudioChunk;
                recorder.OnSilenceDetected -= HandleRealtimeSilenceDetected;
                recorder.Dispose();
            }

            cts?.Dispose();

            lock (_streamingBuffer)
            {
                _streamingBuffer.Clear();
            }
            _streamingTargetWindow = IntPtr.Zero;
            _streamingStartTime = DateTime.MinValue;

            if (finalTranscriptionText.Length > finalLastTypedLength)
            {
                var remainingText = finalTranscriptionText.Substring(finalLastTypedLength);
                Logger.Log($"CleanupAsync: Typing remaining {remainingText.Length} chars");

                if (remainingText.Length > 5)
                {
                    await _clip.SetTextAndPasteAsync(remainingText);
                }
                else
                {
                    TypeTextDirectly(remainingText);
                }
            }

            if (!string.IsNullOrEmpty(finalTranscriptionText) || !string.IsNullOrEmpty(finalTypedText))
            {
                NotificationService.ShowSuccess("Real-time transcription complete.");
            }

            OnStopped?.Invoke();
            Logger.Log("CleanupAsync: Done");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupInProgress, 0);
        }
    }
}
