using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TailSlap;

public sealed class RealtimeTranscriber : IDisposable
{
    private readonly record struct QueueItem(byte[]? Buffer, int Count, bool IsStop);

    private readonly string _wsEndpoint;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _sendTimeout;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _heartbeatTimeout;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private Task? _heartbeatTask;

    // Channel stores QueueItem to ensure type safety and ordering
    private Channel<QueueItem>? _sendChannel;
    private bool _disposed;
    private int _chunksSent = 0;
    private int _chunksSkipped = 0;
    private DateTime _lastReceiveTime = DateTime.MinValue;
    private int _consecutiveErrors = 0;
    private const int MaxConsecutiveErrors = 5;

    public event Action<string, bool>? OnTranscription; // (text, isFinal)
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnConnectionLost; // Fired when heartbeat detects stale connection

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public DateTime LastReceiveTime => _lastReceiveTime;

    public RealtimeTranscriber(string wsEndpoint)
        : this(wsEndpoint, connectionTimeoutSeconds: 10, receiveTimeoutSeconds: 30, sendTimeoutSeconds: 10)
    {
    }

    public RealtimeTranscriber(
        string wsEndpoint,
        int connectionTimeoutSeconds = 10,
        int receiveTimeoutSeconds = 30,
        int sendTimeoutSeconds = 10,
        int heartbeatIntervalSeconds = 10,
        int heartbeatTimeoutSeconds = 15
    )
    {
        _wsEndpoint = wsEndpoint ?? throw new ArgumentNullException(nameof(wsEndpoint));
        _connectionTimeout = TimeSpan.FromSeconds(connectionTimeoutSeconds);
        _receiveTimeout = TimeSpan.FromSeconds(receiveTimeoutSeconds);
        _sendTimeout = TimeSpan.FromSeconds(sendTimeoutSeconds);
        _heartbeatInterval = TimeSpan.FromSeconds(heartbeatIntervalSeconds);
        _heartbeatTimeout = TimeSpan.FromSeconds(heartbeatTimeoutSeconds);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RealtimeTranscriber));

        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            Logger.Log("RealtimeTranscriber: Already connected");
            return;
        }

        // Reset error counter on new connection attempt
        _consecutiveErrors = 0;

        try
        {
            await CleanupWebSocketAsync();
            _ws = new ClientWebSocket();

            // Configure WebSocket options
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            Logger.Log($"RealtimeTranscriber: Connecting to {_wsEndpoint}");

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectionTimeout);

            await _ws.ConnectAsync(new Uri(_wsEndpoint), connectCts.Token).ConfigureAwait(false);
            Logger.Log("RealtimeTranscriber: Connected successfully");

            _lastReceiveTime = DateTime.UtcNow;
            _chunksSent = 0;
            _chunksSkipped = 0;

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Create bounded channel (drop oldest if full) to handle backpressure
            // Using QueueItem struct to avoid boxing
            _sendChannel = Channel.CreateBounded<QueueItem>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                }
            );

            _receiveTask = ReceiveLoopAsync(_connectionCts.Token);
            _sendTask = SendLoopAsync(_connectionCts.Token);
            _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);

            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: Connection failed - {ex.Message}");
            OnError?.Invoke($"Connection failed: {ex.Message}");
            await CleanupWebSocketAsync();
            throw;
        }
    }

    public Task SendAudioChunkAsync(byte[] pcm16Data, CancellationToken ct = default)
    {
        return SendAudioChunkAsync(new ArraySegment<byte>(pcm16Data), ct);
    }

    public Task SendAudioChunkAsync(ArraySegment<byte> pcm16Data, CancellationToken ct = default)
    {
        if (_disposed || _sendChannel == null)
            return Task.CompletedTask;

        try
        {
            var rented = ArrayPool<byte>.Shared.Rent(pcm16Data.Count);
            Buffer.BlockCopy(pcm16Data.Array!, pcm16Data.Offset, rented, 0, pcm16Data.Count);

            if (!_sendChannel.Writer.TryWrite(new QueueItem(rented, pcm16Data.Count, false)))
            {
                ArrayPool<byte>.Shared.Return(rented);
                Interlocked.Increment(ref _chunksSkipped);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: SendAudioChunkAsync failed - {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        if (_sendChannel == null)
            return;

        try
        {
            while (await _sendChannel.Reader.WaitToReadAsync(ct))
            {
                while (_sendChannel.Reader.TryRead(out var item))
                {
                    if (_ws?.State == WebSocketState.Open)
                    {
                        try
                        {
                            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            sendCts.CancelAfter(_sendTimeout);

                            if (item.IsStop)
                            {
                                if (item.Buffer != null)
                                {
                                    await _ws.SendAsync(
                                            new ArraySegment<byte>(item.Buffer, 0, item.Count),
                                            WebSocketMessageType.Binary,
                                            endOfMessage: true,
                                            sendCts.Token
                                        )
                                        .ConfigureAwait(false);
                                }
                                var stopMsg = Encoding.UTF8.GetBytes("{\"action\":\"stop\"}");
                                await _ws.SendAsync(
                                        new ArraySegment<byte>(stopMsg),
                                        WebSocketMessageType.Text,
                                        endOfMessage: true,
                                        sendCts.Token
                                    )
                                    .ConfigureAwait(false);
                            }
                            else if (item.Buffer != null)
                            {
                                await _ws.SendAsync(
                                        new ArraySegment<byte>(item.Buffer, 0, item.Count),
                                        WebSocketMessageType.Binary,
                                        endOfMessage: true,
                                        sendCts.Token
                                    )
                                    .ConfigureAwait(false);
                                ArrayPool<byte>.Shared.Return(item.Buffer);
                                Interlocked.Increment(ref _chunksSent);
                            }

                            // Reset consecutive errors on successful send
                            _consecutiveErrors = 0;
                        }
                        catch (OperationCanceledException)
                        {
                            // Send timeout or cancellation
                            Logger.Log("SendLoopAsync: Send timeout");
                            Interlocked.Increment(ref _consecutiveErrors);
                            if (item.Buffer != null)
                                ArrayPool<byte>.Shared.Return(item.Buffer);

                            if (_consecutiveErrors >= MaxConsecutiveErrors)
                            {
                                Logger.Log("SendLoopAsync: Too many consecutive errors, triggering disconnect");
                                _ = HandleConnectionLostAsync("Too many send failures");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            // If send fails, log but keep loop running (unless cancelled)
                            // We might just be reconnecting or temporary glitch
                            Logger.Log($"SendLoopAsync: Send failed - {ex.Message}");
                            Interlocked.Increment(ref _consecutiveErrors);
                            if (item.Buffer != null)
                                ArrayPool<byte>.Shared.Return(item.Buffer);

                            if (_consecutiveErrors >= MaxConsecutiveErrors)
                            {
                                Logger.Log("SendLoopAsync: Too many consecutive errors, triggering disconnect");
                                _ = HandleConnectionLostAsync("Too many send failures");
                                return;
                            }
                        }
                    }
                    else
                    {
                        // WebSocket not open, clean up the buffer
                        if (item.Buffer != null)
                            ArrayPool<byte>.Shared.Return(item.Buffer);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.Log($"SendLoopAsync error: {ex.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, ct);

                if (_ws?.State != WebSocketState.Open)
                    continue;

                // Check if we've received any data recently
                var timeSinceLastReceive = DateTime.UtcNow - _lastReceiveTime;
                if (timeSinceLastReceive > _heartbeatTimeout)
                {
                    Logger.Log($"Heartbeat: No data received for {timeSinceLastReceive.TotalSeconds:F1}s, connection may be stale");
                    await HandleConnectionLostAsync("Connection timeout - no data received");
                    return;
                }

                // Send ping frame (WebSocket ping)
                try
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(5));

                    // Send an empty binary frame as a keepalive/ping
                    await _ws.SendAsync(
                        ArraySegment<byte>.Empty,
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        pingCts.Token
                    ).ConfigureAwait(false);

                    Logger.Log("Heartbeat: Ping sent");
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("Heartbeat: Ping timeout");
                    await HandleConnectionLostAsync("Ping timeout");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Heartbeat: Ping failed - {ex.Message}");
                    await HandleConnectionLostAsync($"Ping failed: {ex.Message}");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Logger.Log($"HeartbeatLoop error: {ex.Message}");
        }
    }

    private async Task HandleConnectionLostAsync(string reason)
    {
        Logger.Log($"RealtimeTranscriber: Connection lost - {reason}");

        try
        {
            OnConnectionLost?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: OnConnectionLost handler error - {ex.Message}");
        }

        await CleanupWebSocketAsync();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return;

        if (_ws?.State != WebSocketState.Open)
        {
            Logger.Log("RealtimeTranscriber: Cannot send stop - not connected");
            return;
        }

        try
        {
            Logger.Log("RealtimeTranscriber: Sending silence padding and stop signal");

            if (_sendChannel != null)
            {
                var silence = new byte[32000]; // 1s silence
                await _sendChannel
                    .Writer.WriteAsync(new QueueItem(silence, silence.Length, true), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: StopAsync failed - {ex.Message}");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return;

        try
        {
            _connectionCts?.Cancel();
            _heartbeatCts?.Cancel();

            if (_ws?.State == WebSocketState.Open)
            {
                Logger.Log("RealtimeTranscriber: Closing WebSocket");
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                closeCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("RealtimeTranscriber: Close timeout, aborting");
                    _ws.Abort();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: DisconnectAsync error - {ex.Message}");
        }
        finally
        {
            await CleanupWebSocketAsync();
            OnDisconnected?.Invoke();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuffer = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    receiveCts.CancelAfter(_receiveTimeout);

                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCts.Token)
                        .ConfigureAwait(false);

                    // Update last receive time for heartbeat
                    _lastReceiveTime = DateTime.UtcNow;
                    _consecutiveErrors = 0;
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    // Receive timeout
                    Logger.Log("RealtimeTranscriber: Receive timeout");
                    await HandleConnectionLostAsync("Receive timeout");
                    break;
                }
                catch (WebSocketException ex)
                {
                    Logger.Log($"RealtimeTranscriber: WebSocket receive error - {ex.Message}");
                    OnError?.Invoke($"Connection error: {ex.Message}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Log("RealtimeTranscriber: Server closed connection");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var json = messageBuffer.ToString();
                        messageBuffer.Clear();

                        try
                        {
                            var msg = JsonSerializer.Deserialize(
                                json,
                                TailSlapJsonContext.Default.RealtimeTranscriptionMessage
                            );
                            if (msg != null)
                            {
                                if (!string.IsNullOrEmpty(msg.Error))
                                {
                                    Logger.Log($"RealtimeTranscriber: Server error - {msg.Error}");
                                    OnError?.Invoke(msg.Error);
                                }
                                else
                                {
                                    Logger.Log(
                                        $"RealtimeTranscriber: Received text (final={msg.Final}, len={msg.Text?.Length ?? 0}, sha256={Hashing.Sha256Hex(msg.Text ?? string.Empty)})"
                                    );
                                    OnTranscription?.Invoke(msg.Text ?? "", msg.Final);
                                }
                            }
                        }
                        catch (JsonException ex)
                        {
                            Logger.Log($"RealtimeTranscriber: JSON parse error - {ex.Message}");
                        }
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Binary messages (including empty ping/pong responses) update last receive time
                    // but don't contain transcription data
                    if (result.Count > 0)
                    {
                        Logger.Log($"RealtimeTranscriber: Received {result.Count} bytes of binary data (likely ping response)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: ReceiveLoop error - {ex.Message}");
        }
        finally
        {
            Logger.Log(
                $"RealtimeTranscriber: ReceiveLoop ended. Stats: sent={_chunksSent}, skipped={_chunksSkipped}"
            );
            OnDisconnected?.Invoke();
        }
    }

    private async Task CleanupWebSocketAsync()
    {
        try
        {
            _heartbeatCts?.Cancel();
            _connectionCts?.Cancel();

            // Wait for tasks to complete with timeout
            if (_heartbeatTask != null)
            {
                try
                {
                    await Task.WhenAny(_heartbeatTask, Task.Delay(1000)).ConfigureAwait(false);
                }
                catch { }
                _heartbeatTask = null;
            }

            if (_sendTask != null)
            {
                try
                {
                    await Task.WhenAny(_sendTask, Task.Delay(1000)).ConfigureAwait(false);
                }
                catch { }
                _sendTask = null;
            }

            if (_receiveTask != null)
            {
                try
                {
                    await Task.WhenAny(_receiveTask, Task.Delay(1000)).ConfigureAwait(false);
                }
                catch { }
                _receiveTask = null;
            }

            _sendChannel?.Writer.TryComplete();
            _ws?.Abort();
            _ws?.Dispose();
            _ws = null;

            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
            _connectionCts?.Dispose();
            _connectionCts = null;
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: Cleanup error - {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ = CleanupWebSocketAsync();
    }
}

public sealed class RealtimeTranscriptionMessage
{
    public string? Text { get; set; }
    public bool Final { get; set; }
    public string? Error { get; set; }
}
