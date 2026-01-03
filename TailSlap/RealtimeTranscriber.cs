using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class RealtimeTranscriber : IDisposable
{
    private readonly record struct QueueItem(byte[]? Audio, bool IsStop);

    private readonly string _wsEndpoint;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private Task? _sendTask;

    // Channel stores QueueItem to ensure type safety and ordering
    private Channel<QueueItem>? _sendChannel;
    private bool _disposed;
    private int _chunksSent = 0;
    private int _chunksSkipped = 0;

    public event Action<string, bool>? OnTranscription; // (text, isFinal)
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public RealtimeTranscriber(string wsEndpoint)
    {
        _wsEndpoint = wsEndpoint ?? throw new ArgumentNullException(nameof(wsEndpoint));
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

        try
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();

            Logger.Log($"RealtimeTranscriber: Connecting to {_wsEndpoint}");
            await _ws.ConnectAsync(new Uri(_wsEndpoint), ct).ConfigureAwait(false);
            Logger.Log("RealtimeTranscriber: Connected successfully");

            _chunksSent = 0;
            _chunksSkipped = 0;

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

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

            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: Connection failed - {ex.Message}");
            OnError?.Invoke($"Connection failed: {ex.Message}");
            throw;
        }
    }

    public async Task SendAudioChunkAsync(byte[] pcm16Data, CancellationToken ct = default)
    {
        if (_disposed)
            return;

        if (_ws?.State != WebSocketState.Open)
        {
            Logger.Log("RealtimeTranscriber: Cannot send audio - not connected");
            return;
        }

        try
        {
            await _ws.SendAsync(
                    new ArraySegment<byte>(pcm16Data),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: SendAudioChunkAsync failed - {ex.Message}");
        }
    }

    public Task SendAudioChunkAsync(ArraySegment<byte> pcm16Data, CancellationToken ct = default)
    {
        if (_disposed || _sendChannel == null)
            return Task.CompletedTask;

        try
        {
            // Non-blocking write. If channel full, drops oldest automatically (configured in options)
            // Note: ArraySegment is a struct pointing to buffer. We must copy it because
            // the buffer might be reused by AudioRecorder before SendLoop reads it.
            byte[] dataCopy = pcm16Data.ToArray();

            if (!_sendChannel.Writer.TryWrite(new QueueItem(dataCopy, false)))
            {
                // Should not happen with DropOldest, but good safety
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
                            if (item.IsStop)
                            {
                                var stopMsg = Encoding.UTF8.GetBytes("{\"action\":\"stop\"}");
                                await _ws.SendAsync(
                                        new ArraySegment<byte>(stopMsg),
                                        WebSocketMessageType.Text,
                                        endOfMessage: true,
                                        ct
                                    )
                                    .ConfigureAwait(false);
                            }
                            else if (item.Audio != null)
                            {
                                await _ws.SendAsync(
                                        new ArraySegment<byte>(item.Audio),
                                        WebSocketMessageType.Binary,
                                        endOfMessage: true,
                                        ct
                                    )
                                    .ConfigureAwait(false);
                                Interlocked.Increment(ref _chunksSent);
                            }
                        }
                        catch (Exception ex)
                        {
                            // If send fails, log but keep loop running (unless cancelled)
                            // We might just be reconnecting or temporary glitch
                            Logger.Log($"SendLoopAsync: Send failed - {ex.Message}");
                        }
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

            // Send silence padding via channel to maintain order
            var silence = new byte[32000]; // 1s silence
            if (_sendChannel != null)
            {
                await _sendChannel
                    .Writer.WriteAsync(new QueueItem(silence, false), ct)
                    .ConfigureAwait(false);

                // Send stop command via channel
                await _sendChannel
                    .Writer.WriteAsync(new QueueItem(null, true), ct)
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

            if (_ws?.State == WebSocketState.Open)
            {
                Logger.Log("RealtimeTranscriber: Closing WebSocket");
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"RealtimeTranscriber: DisconnectAsync error - {ex.Message}");
        }
        finally
        {
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
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
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
                                        $"RealtimeTranscriber: Received text (final={msg.Final}): {msg.Text?.Substring(0, Math.Min(50, msg.Text?.Length ?? 0))}"
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _sendChannel?.Writer.TryComplete(); // Stop accepting new items
        _ws?.Dispose();
    }
}

public sealed class RealtimeTranscriptionMessage
{
    public string? Text { get; set; }
    public bool Final { get; set; }
    public string? Error { get; set; }
}
