using System;
using System.Collections.Generic;
using System.IO;
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
    private IRealtimeTranscriber? _realtimeTranscriber;
    private AudioRecorder? _realtimeRecorder;

    private string _realtimeTranscriptionText = "";
    private string _typedText = "";
    private int _lastTypedLength = 0;
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
    private readonly MemoryStream _streamingBuffer = new();
    private const int SEND_BUFFER_SIZE = 16000;
    private IntPtr _streamingTargetWindow = IntPtr.Zero;
    private int _cleanupInProgress = 0;
    private DateTime _streamingStartTime = DateTime.MinValue;
    private const int NO_SPEECH_TIMEOUT_SECONDS = 30;
    private CancellationTokenSource? _textProcessingCts;
    private volatile bool _allowRealtimeTextUpdates;
    private volatile bool _allowRealtimeInterimWhileStopping;
    private string _lastReceivedRealtimeText = "";
    private string? _currentRealtimeItemId;
    private readonly Dictionary<
        string,
        PendingOrderedRealtimeUpdate
    > _pendingOrderedRealtimeUpdates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _orderedRealtimeSequences = new(
        StringComparer.Ordinal
    );
    private readonly HashSet<string> _completedOrderedRealtimeItems = new(StringComparer.Ordinal);
    private long _nextOrderedRealtimeSequence = 0;

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
    public event Action<float>? OnRmsLevel;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0x0;
    private const uint VK_BACK = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private sealed class PendingOrderedRealtimeUpdate
    {
        public required RealtimeTranscriptionUpdate Update { get; set; }
        public required long Sequence { get; init; }
    }

    public RealtimeTranscriptionController(
        IConfigService config,
        IClipboardService clip,
        IRealtimeTranscriberFactory transcriberFactory,
        IAudioRecorderFactory audioRecorderFactory
    )
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _transcriberFactory =
            transcriberFactory ?? throw new ArgumentNullException(nameof(transcriberFactory));
        _audioRecorderFactory =
            audioRecorderFactory ?? throw new ArgumentNullException(nameof(audioRecorderFactory));
    }

    public async Task TriggerStreamingAsync()
    {
        var cfg = _config.CreateValidatedCopy();

        if (!cfg.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            NotificationService.ShowWarning(
                "Remote transcription is disabled. Enable it in settings first."
            );
            return;
        }

        StreamingState currentState;
        lock (_streamingStateLock)
        {
            if (
                _streamingState == StreamingState.Starting
                || _streamingState == StreamingState.Stopping
            )
            {
                Logger.Log(
                    $"TriggerStreamingAsync: Ignoring hotkey, transition in progress (state={_streamingState})"
                );
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
            await StopAsyncInternal(suppressInterimUpdates: true);
        }
        else
        {
            await StartAsync(cfg);
        }
    }

    public async Task StartAsync()
    {
        await StartAsync(_config.CreateValidatedCopy());
    }

    private async Task StartAsync(AppConfig cfg)
    {
        _textProcessingCts?.Cancel();
        _textProcessingCts?.Dispose();
        _textProcessingCts = new CancellationTokenSource();

        Logger.Log("StartAsync: Starting real-time WebSocket transcription");
        _realtimeTranscriptionText = "";
        _typedText = "";
        _lastTypedLength = 0;
        _lastReceivedRealtimeText = "";
        _currentRealtimeItemId = null;
        _allowRealtimeTextUpdates = true;
        _allowRealtimeInterimWhileStopping = false;
        _pendingOrderedRealtimeUpdates.Clear();
        _orderedRealtimeSequences.Clear();
        _completedOrderedRealtimeItems.Clear();
        _nextOrderedRealtimeSequence = 0;

        try
        {
            NotificationService.ShowInfo("Real-time transcription started. Speak now...");
            OnStarted?.Invoke();

            _realtimeTranscriber = _transcriberFactory.Create(cfg.Transcriber);
            _realtimeTranscriber.OnTranscription += HandleRealtimeTranscriptionEvent;
            _realtimeTranscriber.OnError += HandleRealtimeError;
            _realtimeTranscriber.OnDisconnected += HandleRealtimeDisconnected;
            _realtimeTranscriber.OnConnectionLost += HandleRealtimeConnectionLost;

            await _realtimeTranscriber.ConnectAsync();
            Logger.Log("StartAsync: WebSocket connected");

            _realtimeRecorder = _audioRecorderFactory.Create(
                cfg.Transcriber.PreferredMicrophoneIndex
            );

            Logger.Log(
                $"StartAsync: VAD settings - Activ={cfg.Transcriber.VadActivationThreshold}, Sust={cfg.Transcriber.VadSustainThreshold}, Sil={cfg.Transcriber.VadSilenceThreshold}, WebRtcVAD={cfg.Transcriber.UseWebRtcVad}"
            );

            _realtimeRecorder.SetVadThresholds(
                cfg.Transcriber.VadSilenceThreshold,
                cfg.Transcriber.VadActivationThreshold,
                cfg.Transcriber.VadSustainThreshold
            );

            // Configure WebRTC VAD
            _realtimeRecorder.SetUseWebRtcVad(cfg.Transcriber.UseWebRtcVad);
            if (cfg.Transcriber.UseWebRtcVad)
            {
                _realtimeRecorder.SetWebRtcVadSensitivity(
                    (VadSensitivity)cfg.Transcriber.WebRtcVadSensitivity
                );
            }

            _realtimeRecorder.OnAudioChunk += HandleRealtimeAudioChunk;
            _realtimeRecorder.OnSilenceDetected += HandleRealtimeSilenceDetected;
            _realtimeRecorder.OnRmsLevel += rms =>
            {
                try
                {
                    OnRmsLevel?.Invoke(rms);
                }
                catch { }
            };

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
                silenceThresholdMs: cfg.Transcriber.SilenceThresholdMs
            );
        }
        catch (Exception ex)
        {
            Logger.Log($"StartAsync: Error - {ex.Message}");
            NotificationService.ShowError($"Real-time transcription failed: {ex.Message}");
            await CleanupAsync();
        }
    }

    public Task StopAsync()
    {
        return StopAsyncInternal(suppressInterimUpdates: true);
    }

    private async Task StopAsyncInternal(bool suppressInterimUpdates)
    {
        Logger.Log("StopAsync: Stopping real-time transcription");
        NotificationService.ShowInfo("Stopping real-time transcription...");
        _allowRealtimeTextUpdates = true;
        _allowRealtimeInterimWhileStopping = !suppressInterimUpdates;

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

                void OnTranscriptionReceived(RealtimeTranscriptionUpdate update)
                {
                    if (update.IsFinal)
                        finalMessageTcs.TrySetResult(true);
                }

                _realtimeTranscriber.OnDisconnected += OnServerDisconnected;
                _realtimeTranscriber.OnTranscription += OnTranscriptionReceived;

                byte[]? remainingData = null;
                lock (_streamingBuffer)
                {
                    if (_streamingBuffer.Length > 0)
                    {
                        remainingData = _streamingBuffer.ToArray();
                        _streamingBuffer.SetLength(0);
                        _streamingBuffer.Position = 0;
                    }
                }

                if (remainingData != null)
                {
                    await _realtimeTranscriber.SendAudioChunkAsync(
                        new ArraySegment<byte>(remainingData)
                    );
                }

                await _realtimeTranscriber.StopAsync();

                Logger.Log(
                    "StopAsync: Waiting for server to close connection or send final message..."
                );
                await Task.WhenAny(serverClosedTcs.Task, finalMessageTcs.Task, Task.Delay(10000));

                // If the server delivered a final transcript, let any in-flight text processing
                // finish before cleanup cancels the processing token and resets state.
                if (finalMessageTcs.Task.IsCompletedSuccessfully)
                {
                    await _transcriptionLock.WaitAsync();
                    _transcriptionLock.Release();
                }

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

        if (
            _streamingStartTime != DateTime.MinValue
            && _realtimeTranscriptionText.Length == 0
            && _typedText.Length == 0
            && (DateTime.UtcNow - _streamingStartTime).TotalSeconds >= NO_SPEECH_TIMEOUT_SECONDS
        )
        {
            Logger.Log(
                $"HandleRealtimeAudioChunk: No speech detected after {NO_SPEECH_TIMEOUT_SECONDS}s, triggering auto-stop"
            );
            _ = Task.Run(() => HandleRealtimeSilenceDetected());
            return;
        }

        if (_realtimeTranscriber?.IsConnected == true)
        {
            lock (_streamingBuffer)
            {
                if (chunk.Array != null && chunk.Count > 0)
                {
                    _streamingBuffer.Write(chunk.Array, chunk.Offset, chunk.Count);
                }

                if (_streamingBuffer.Length >= SEND_BUFFER_SIZE)
                {
                    var dataToSend = _streamingBuffer.ToArray();
                    _streamingBuffer.SetLength(0);
                    _streamingBuffer.Position = 0;
                    _ = _realtimeTranscriber.SendAudioChunkAsync(
                        new ArraySegment<byte>(dataToSend)
                    );
                }
            }
        }
    }

    private void HandleRealtimeTranscriptionEvent(RealtimeTranscriptionUpdate update)
    {
        Logger.Log(
            $"HandleRealtimeTranscriptionEvent: text.Length={update.Text.Length}, final={update.IsFinal}, itemId={update.ItemId ?? "<none>"}"
        );

        if (!string.IsNullOrEmpty(update.Text) && !update.IsFinal)
        {
            _realtimeRecorder?.NotifySpeechDetected();
        }

        OnTranscription?.Invoke(update.Text, update.IsFinal);

        if (!_allowRealtimeTextUpdates && !update.IsFinal)
        {
            Logger.Log("HandleRealtimeTranscriptionEvent: Ignoring local text update during stop");
            return;
        }

        if (string.IsNullOrEmpty(update.ItemId))
        {
            ProcessLegacyTranscriptionEvent(update.Text, update.IsFinal);
            return;
        }

        if (!_orderedRealtimeSequences.TryGetValue(update.ItemId, out var sequence))
        {
            sequence = _nextOrderedRealtimeSequence++;
            _orderedRealtimeSequences[update.ItemId] = sequence;
        }

        _pendingOrderedRealtimeUpdates[update.ItemId] = new PendingOrderedRealtimeUpdate
        {
            Update = update,
            Sequence = sequence,
        };

        TryProcessQueuedOrderedRealtimeUpdates();
    }

    private void TryProcessQueuedOrderedRealtimeUpdates()
    {
        var textProcessingToken = _textProcessingCts?.Token ?? CancellationToken.None;

        while (true)
        {
            PendingOrderedRealtimeUpdate? next = null;
            foreach (var queuedUpdate in _pendingOrderedRealtimeUpdates.Values)
            {
                if (!CanProcessOrderedRealtimeUpdate(queuedUpdate.Update))
                {
                    continue;
                }

                if (next == null || queuedUpdate.Sequence < next.Sequence)
                {
                    next = queuedUpdate;
                }
            }

            if (next == null)
            {
                return;
            }

            _ = ProcessTranscriptionAsync(
                next.Update.Text,
                next.Update.IsFinal,
                next.Update.ItemId,
                textProcessingToken
            );

            if (!next.Update.IsFinal)
            {
                return;
            }

            _completedOrderedRealtimeItems.Add(next.Update.ItemId!);
            _pendingOrderedRealtimeUpdates.Remove(next.Update.ItemId!);
            _orderedRealtimeSequences.Remove(next.Update.ItemId!);
        }
    }

    private bool CanProcessOrderedRealtimeUpdate(RealtimeTranscriptionUpdate update)
    {
        return string.IsNullOrEmpty(update.PreviousItemId)
            || _completedOrderedRealtimeItems.Contains(update.PreviousItemId)
            || !_orderedRealtimeSequences.ContainsKey(update.PreviousItemId)
                && !_pendingOrderedRealtimeUpdates.ContainsKey(update.PreviousItemId);
    }

    private void ProcessLegacyTranscriptionEvent(string text, bool isFinal)
    {
        if (!isFinal && string.Equals(text, _lastReceivedRealtimeText, StringComparison.Ordinal))
        {
            Logger.Log("HandleRealtimeTranscriptionEvent: Duplicate interim text, skipping");
            return;
        }

        _lastReceivedRealtimeText = text;

        var textProcessingToken = _textProcessingCts?.Token ?? CancellationToken.None;
        _ = ProcessTranscriptionAsync(text, isFinal, null, textProcessingToken);
    }

    private async Task ProcessTranscriptionAsync(
        string text,
        bool isFinal,
        string? itemId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _transcriptionLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            StreamingState state;
            lock (_streamingStateLock)
            {
                state = _streamingState;
            }
            bool canProcessWhileStopping =
                state == StreamingState.Stopping && (isFinal || _allowRealtimeInterimWhileStopping);
            if (state != StreamingState.Streaming && !canProcessWhileStopping)
            {
                Logger.Log($"ProcessTranscriptionAsync: Ignoring, state={state}");
                return;
            }

            if (string.IsNullOrEmpty(text))
                return;

            if (
                !string.IsNullOrEmpty(itemId)
                && !string.IsNullOrEmpty(_currentRealtimeItemId)
                && !string.Equals(_currentRealtimeItemId, itemId, StringComparison.Ordinal)
            )
            {
                Logger.Log(
                    $"ProcessTranscriptionAsync: Switching item baseline from {_currentRealtimeItemId} to {itemId}"
                );

                if (_lastTypedLength > 0 && _lastTypedLength <= _realtimeTranscriptionText.Length)
                {
                    _typedText += _realtimeTranscriptionText.Substring(0, _lastTypedLength);
                }

                _realtimeTranscriptionText = "";
                _lastTypedLength = 0;
                _lastReceivedRealtimeText = "";
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                _currentRealtimeItemId = itemId;
            }

            if (string.Equals(text, _realtimeTranscriptionText, StringComparison.Ordinal))
            {
                if (!isFinal)
                {
                    Logger.Log("ProcessTranscriptionAsync: Text unchanged, skipping");
                    return;
                }

                Logger.Log("ProcessTranscriptionAsync: Final text unchanged, finalizing");
            }

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

            string onScreen =
                _lastTypedLength > 0 && _lastTypedLength <= _realtimeTranscriptionText.Length
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
                Logger.Log(
                    $"ProcessTranscriptionAsync: Backspacing {backspaceCount} chars for correction"
                );
                SendBackspace(backspaceCount);
                _lastTypedLength = commonPrefixLen;
                await Task.Delay(20, cancellationToken);
            }

            if (text.Length > _lastTypedLength)
            {
                var newText = text.Substring(_lastTypedLength);
                Logger.Log($"ProcessTranscriptionAsync: Typing {newText.Length} chars");
                cancellationToken.ThrowIfCancellationRequested();

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
                if (
                    !string.IsNullOrEmpty(itemId)
                    && string.Equals(_currentRealtimeItemId, itemId, StringComparison.Ordinal)
                )
                {
                    _currentRealtimeItemId = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log("ProcessTranscriptionAsync: Cancelled");
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
            Logger.Log(
                $"IsForegroundWindowSafe: Window changed from 0x{_streamingTargetWindow:X} to 0x{current:X}"
            );
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
            var scanCode = (ushort)MapVirtualKey(VK_BACK, MAPVK_VK_TO_VSC);
            if (scanCode == 0)
            {
                scanCode = 0x0E;
            }

            var inputs = new INPUT[count * 2];
            for (int i = 0; i < count; i++)
            {
                int downIndex = i * 2;
                inputs[downIndex] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wScan = scanCode, dwFlags = KEYEVENTF_SCANCODE },
                    },
                };
                inputs[downIndex + 1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT
                        {
                            wScan = scanCode,
                            dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                        },
                    },
                };
            }

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                Logger.Log(
                    $"SendBackspace: SendInput sent {sent}/{inputs.Length} events, falling back to SendKeys"
                );
                SendKeys.SendWait("{BS " + count + "}");
                SendKeys.Flush();
            }
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
                if (
                    c == '+'
                    || c == '^'
                    || c == '%'
                    || c == '~'
                    || c == '('
                    || c == ')'
                    || c == '{'
                    || c == '}'
                    || c == '['
                    || c == ']'
                )
                {
                    escaped.Append('{').Append(c).Append('}');
                }
                else if (c == '\n')
                {
                    escaped.Append("{ENTER}");
                }
                else if (c == '\r') { }
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

            await StopAsyncInternal(suppressInterimUpdates: true);
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
                await StopAsyncInternal(suppressInterimUpdates: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"HandleRealtimeDisconnected: ERROR - {ex.Message}");
        }
    }

    private async void HandleRealtimeConnectionLost()
    {
        try
        {
            Logger.Log(
                "HandleRealtimeConnectionLost: Connection lost detected, initiating recovery"
            );

            bool shouldRecover = false;
            lock (_streamingStateLock)
            {
                if (_streamingState == StreamingState.Streaming)
                {
                    _streamingState = StreamingState.Stopping;
                    shouldRecover = true;
                }
            }

            if (shouldRecover)
            {
                // Notify user of connection issue
                NotificationService.ShowWarning(
                    "Real-time transcription connection lost. Stopping..."
                );

                // Gracefully stop and cleanup
                await StopAsyncInternal(suppressInterimUpdates: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"HandleRealtimeConnectionLost: ERROR - {ex.Message}");
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

            await StopAsyncInternal(suppressInterimUpdates: false);
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
            _allowRealtimeTextUpdates = false;
            _allowRealtimeInterimWhileStopping = false;
            _textProcessingCts?.Cancel();

            await _transcriptionLock.WaitAsync();

            string finalTranscriptionText = _realtimeTranscriptionText;
            string finalTypedText = _typedText;

            _typedText = "";
            _realtimeTranscriptionText = "";
            _lastTypedLength = 0;
            _lastReceivedRealtimeText = "";
            _currentRealtimeItemId = null;
            _pendingOrderedRealtimeUpdates.Clear();
            _orderedRealtimeSequences.Clear();
            _completedOrderedRealtimeItems.Clear();
            _nextOrderedRealtimeSequence = 0;

            lock (_streamingStateLock)
            {
                _streamingState = StreamingState.Idle;
            }

            _transcriptionLock.Release();

            var transcriber = _realtimeTranscriber;
            var recorder = _realtimeRecorder;
            var cts = _transcriberCts;
            var textProcessingCts = _textProcessingCts;

            _realtimeTranscriber = null;
            _realtimeRecorder = null;
            _transcriberCts = null;
            _textProcessingCts = null;

            if (transcriber != null)
            {
                transcriber.OnTranscription -= HandleRealtimeTranscriptionEvent;
                transcriber.OnError -= HandleRealtimeError;
                transcriber.OnDisconnected -= HandleRealtimeDisconnected;
                transcriber.OnConnectionLost -= HandleRealtimeConnectionLost;

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
                _streamingBuffer.SetLength(0);
                _streamingBuffer.Position = 0;
            }
            _streamingTargetWindow = IntPtr.Zero;
            _streamingStartTime = DateTime.MinValue;

            if (
                !string.IsNullOrEmpty(finalTranscriptionText)
                || !string.IsNullOrEmpty(finalTypedText)
            )
            {
                NotificationService.ShowSuccess("Real-time transcription complete.");
            }

            OnStopped?.Invoke();
            Logger.Log("CleanupAsync: Done");
            textProcessingCts?.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupInProgress, 0);
        }
    }
}
