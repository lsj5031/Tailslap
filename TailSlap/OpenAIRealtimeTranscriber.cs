using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TailSlap;

public sealed class OpenAIRealtimeTranscriber : IRealtimeTranscriber
{
    private readonly record struct QueueItem(byte[]? Buffer, int Count, bool IsStop);

    private readonly TranscriberConfig _config;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _sendTimeout;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveTask;
    private Task? _sendTask;

    private Channel<QueueItem>? _sendChannel;
    private bool _disposed;
    private int _chunksSent = 0;
    private int _chunksSkipped = 0;
    private DateTime _lastReceiveTime = DateTime.MinValue;
    private int _consecutiveErrors = 0;
    private const int MaxConsecutiveErrors = 5;
    private readonly Dictionary<string, string> _itemTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _previousItemIds = new(StringComparer.Ordinal);

    public event Action<RealtimeTranscriptionUpdate>? OnTranscription;
    public event Action<string>? OnError;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action? OnConnectionLost;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public DateTime LastReceiveTime => _lastReceiveTime;

    public OpenAIRealtimeTranscriber(TranscriberConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _connectionTimeout = TimeSpan.FromSeconds(config.WebSocketConnectionTimeoutSeconds);
        _receiveTimeout = TimeSpan.FromSeconds(config.WebSocketReceiveTimeoutSeconds);
        _sendTimeout = TimeSpan.FromSeconds(config.WebSocketSendTimeoutSeconds);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OpenAIRealtimeTranscriber));

        if (_ws != null && _ws.State == WebSocketState.Open)
        {
            Logger.Log("OpenAIRealtimeTranscriber: Already connected");
            return;
        }

        _consecutiveErrors = 0;

        try
        {
            await CleanupWebSocketAsync();
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
            }

            var model = string.IsNullOrEmpty(_config.Model) ? "gpt-4o-transcribe" : _config.Model;
            var wsUrl = BuildWebSocketUrl();

            Logger.Log($"OpenAIRealtimeTranscriber: Connecting to {wsUrl}");

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_connectionTimeout);

            await _ws.ConnectAsync(new Uri(wsUrl), connectCts.Token).ConfigureAwait(false);
            Logger.Log("OpenAIRealtimeTranscriber: Connected successfully");

            _lastReceiveTime = DateTime.UtcNow;
            _chunksSent = 0;
            _chunksSkipped = 0;
            _itemTexts.Clear();
            _previousItemIds.Clear();

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _sendChannel = Channel.CreateBounded<QueueItem>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                }
            );

            // Send session configuration for transcription-only mode
            await ConfigureSessionAsync(model, _connectionCts.Token);

            _receiveTask = ReceiveLoopAsync(_connectionCts.Token);
            _sendTask = SendLoopAsync(_connectionCts.Token);

            OnConnected?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber: Connection failed - {ex.Message}");
            OnError?.Invoke($"Connection failed: {ex.Message}");
            await CleanupWebSocketAsync();
            throw;
        }
    }

    private string BuildWebSocketUrl()
    {
        var baseUrl = _config.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            return "wss://api.openai.com/v1/realtime?intent=transcription";
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return "wss://api.openai.com/v1/realtime?intent=transcription";
        }

        var builder = new UriBuilder(baseUri);
        builder.Scheme = builder.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "wss"
            : "ws";

        var path = builder.Path.TrimEnd('/');
        if (
            !path.EndsWith("/v1/realtime", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("/realtime", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                path += "/realtime";
            else if (path.EndsWith("/v1/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
                path = path[..^"/audio/transcriptions".Length] + "/realtime";
            else
                path += "/v1/realtime";
        }
        builder.Path = path;

        var query = "intent=transcription";
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? query
            : builder.Query.TrimStart('?') + "&" + query;

        return builder.ToString();
    }

    private async Task ConfigureSessionAsync(string model, CancellationToken ct)
    {
        var sessionUpdate = new
        {
            type = "transcription_session.update",
            input_audio_format = "pcm16",
            input_audio_transcription = new
            {
                model,
                prompt = string.Empty,
                language = string.Empty,
            },
            turn_detection = new
            {
                type = "server_vad",
                threshold = 0.5,
                prefix_padding_ms = 300,
                silence_duration_ms = 500,
            },
            input_audio_noise_reduction = new { type = "near_field" },
        };

        var json = JsonSerializer.Serialize(sessionUpdate);
        var bytes = Encoding.UTF8.GetBytes(json);

        Logger.Log(
            $"OpenAIRealtimeTranscriber: Sending transcription_session.update for model={model}"
        );

        await _ws!
            .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        Logger.Log("OpenAIRealtimeTranscriber: Transcription session configured");
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
            Logger.Log($"OpenAIRealtimeTranscriber: SendAudioChunkAsync failed - {ex.Message}");
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
                    if (_ws?.State != WebSocketState.Open)
                    {
                        if (item.Buffer != null)
                            ArrayPool<byte>.Shared.Return(item.Buffer);
                        continue;
                    }

                    try
                    {
                        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        sendCts.CancelAfter(_sendTimeout);

                        if (item.IsStop)
                        {
                            await SendCommitAndClearAsync(sendCts.Token);
                        }
                        else if (item.Buffer != null)
                        {
                            // Resample 16kHz -> 24kHz and send as base64-encoded input_audio_buffer.append
                            var resampled = AudioResampler.Resample16To24(
                                item.Buffer,
                                0,
                                item.Count
                            );
                            ArrayPool<byte>.Shared.Return(item.Buffer);

                            var base64 = Convert.ToBase64String(resampled);
                            var appendEvent = new
                            {
                                type = "input_audio_buffer.append",
                                audio = base64,
                            };
                            var json = JsonSerializer.Serialize(appendEvent);
                            var bytes = Encoding.UTF8.GetBytes(json);

                            await _ws.SendAsync(
                                    new ArraySegment<byte>(bytes),
                                    WebSocketMessageType.Text,
                                    true,
                                    sendCts.Token
                                )
                                .ConfigureAwait(false);

                            Interlocked.Increment(ref _chunksSent);
                        }

                        _consecutiveErrors = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log("OpenAIRealtimeTranscriber SendLoop: Send timeout");
                        if (item.Buffer != null)
                            ArrayPool<byte>.Shared.Return(item.Buffer);

                        Interlocked.Increment(ref _consecutiveErrors);
                        if (_consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            await HandleConnectionLostAsync("Too many send failures");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(
                            $"OpenAIRealtimeTranscriber SendLoop: Send failed - {ex.Message}"
                        );
                        if (item.Buffer != null)
                            ArrayPool<byte>.Shared.Return(item.Buffer);

                        Interlocked.Increment(ref _consecutiveErrors);
                        if (_consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            await HandleConnectionLostAsync("Too many send failures");
                            return;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber SendLoop error: {ex.Message}");
        }
    }

    private async Task SendCommitAndClearAsync(CancellationToken ct)
    {
        // Send silence padding first
        var silence = new byte[48000]; // ~1s of 24kHz 16-bit mono silence
        var base64 = Convert.ToBase64String(silence);
        var appendEvent = new { type = "input_audio_buffer.append", audio = base64 };
        var json = JsonSerializer.Serialize(appendEvent);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _ws!
            .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        // Commit the buffer to trigger transcription
        var commitEvent = new { type = "input_audio_buffer.commit" };
        json = JsonSerializer.Serialize(commitEvent);
        bytes = Encoding.UTF8.GetBytes(json);

        await _ws!
            .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        Logger.Log("OpenAIRealtimeTranscriber: Sent commit");

        // Clear the buffer for next utterance
        var clearEvent = new { type = "input_audio_buffer.clear" };
        json = JsonSerializer.Serialize(clearEvent);
        bytes = Encoding.UTF8.GetBytes(json);

        await _ws!
            .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        Logger.Log("OpenAIRealtimeTranscriber: Sent buffer clear");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
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

                    result = await _ws.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            receiveCts.Token
                        )
                        .ConfigureAwait(false);

                    _lastReceiveTime = DateTime.UtcNow;
                    _consecutiveErrors = 0;
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    Logger.Log("OpenAIRealtimeTranscriber: Receive timeout");
                    await HandleConnectionLostAsync("Receive timeout");
                    break;
                }
                catch (WebSocketException ex)
                {
                    Logger.Log(
                        $"OpenAIRealtimeTranscriber: WebSocket receive error - {ex.Message}"
                    );
                    OnError?.Invoke($"Connection error: {ex.Message}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Log("OpenAIRealtimeTranscriber: Server closed connection");
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
                            ProcessServerEvent(json);
                        }
                        catch (JsonException ex)
                        {
                            Logger.Log(
                                $"OpenAIRealtimeTranscriber: JSON parse error - {ex.Message}"
                            );
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber: ReceiveLoop error - {ex.Message}");
        }
        finally
        {
            Logger.Log(
                $"OpenAIRealtimeTranscriber: ReceiveLoop ended. Stats: sent={_chunksSent}, skipped={_chunksSkipped}"
            );
            OnDisconnected?.Invoke();
        }
    }

    private void ProcessServerEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeElement))
            return;

        var eventType = typeElement.GetString() ?? "";

        switch (eventType)
        {
            case "conversation.item.input_audio_transcription.delta":
            case "transcript.text.delta":
            {
                var itemId = TryGetString(root, "item_id");
                var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(delta))
                {
                    var fullText = MergeItemText(itemId, delta);
                    Logger.Log(
                        $"OpenAIRealtimeTranscriber: Delta (len={delta.Length}, sha256={Hashing.Sha256Hex(delta)})"
                    );
                    OnTranscription?.Invoke(
                        new RealtimeTranscriptionUpdate
                        {
                            Text = fullText,
                            IsFinal = false,
                            ItemId = itemId,
                            PreviousItemId = ResolvePreviousItemId(
                                itemId,
                                TryGetString(root, "previous_item_id")
                            ),
                        }
                    );
                }
                break;
            }

            case "conversation.item.input_audio_transcription.completed":
            case "transcript.text.done":
            {
                var itemId = TryGetString(root, "item_id");
                var transcript =
                    root.TryGetProperty("transcript", out var transcriptElement)
                        ? transcriptElement.GetString() ?? ""
                    : root.TryGetProperty("text", out var textElement)
                        ? textElement.GetString() ?? ""
                    : "";
                if (!string.IsNullOrEmpty(itemId))
                {
                    _itemTexts[itemId] = transcript;
                }
                Logger.Log(
                    $"OpenAIRealtimeTranscriber: Completed (len={transcript.Length}, sha256={Hashing.Sha256Hex(transcript)})"
                );
                OnTranscription?.Invoke(
                    new RealtimeTranscriptionUpdate
                    {
                        Text = transcript,
                        IsFinal = true,
                        ItemId = itemId,
                        PreviousItemId = ResolvePreviousItemId(
                            itemId,
                            TryGetString(root, "previous_item_id")
                        ),
                    }
                );
                break;
            }

            case "input_audio_buffer.committed":
            {
                var itemId = TryGetString(root, "item_id");
                var previousItemId = TryGetString(root, "previous_item_id");
                if (!string.IsNullOrEmpty(itemId))
                {
                    _previousItemIds[itemId] = previousItemId;
                }
                Logger.Log("OpenAIRealtimeTranscriber: Audio buffer committed by server");
                break;
            }

            case "input_audio_buffer.cleared":
            {
                Logger.Log("OpenAIRealtimeTranscriber: Audio buffer cleared by server");
                break;
            }

            case "input_audio_buffer.speech_started":
            {
                Logger.Log("OpenAIRealtimeTranscriber: Speech detected");
                break;
            }

            case "input_audio_buffer.speech_stopped":
            {
                Logger.Log("OpenAIRealtimeTranscriber: Speech ended");
                break;
            }

            case "transcription_session.created":
            case "session.created":
            {
                Logger.Log("OpenAIRealtimeTranscriber: Transcription session created");
                break;
            }

            case "transcription_session.updated":
            case "session.updated":
            {
                Logger.Log("OpenAIRealtimeTranscriber: Transcription session updated");
                break;
            }

            case "error":
            {
                var errorMessage = "Unknown error";
                if (root.TryGetProperty("error", out var errorObj))
                {
                    if (errorObj.ValueKind == JsonValueKind.Object)
                    {
                        errorMessage = errorObj.TryGetProperty("message", out var msg)
                            ? msg.GetString() ?? errorMessage
                            : errorMessage;
                    }
                    else if (errorObj.ValueKind == JsonValueKind.String)
                    {
                        errorMessage = errorObj.GetString() ?? errorMessage;
                    }
                }
                Logger.Log($"OpenAIRealtimeTranscriber: Server error - {errorMessage}");
                OnError?.Invoke(errorMessage);
                break;
            }

            default:
            {
                // Log unhandled events at debug level (just the type)
                Logger.Log($"OpenAIRealtimeTranscriber: Unhandled event type: {eventType}");
                break;
            }
        }
    }

    private string MergeItemText(string? itemId, string delta)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return delta;
        }

        var current = _itemTexts.TryGetValue(itemId, out var existing) ? existing : string.Empty;

        string merged;
        if (string.IsNullOrEmpty(current) || delta.StartsWith(current, StringComparison.Ordinal))
        {
            merged = delta;
        }
        else if (current.EndsWith(delta, StringComparison.Ordinal))
        {
            merged = current;
        }
        else
        {
            merged = current + delta;
        }

        _itemTexts[itemId] = merged;
        return merged;
    }

    private string? ResolvePreviousItemId(string? itemId, string? previousItemId)
    {
        if (!string.IsNullOrEmpty(itemId) && previousItemId != null)
        {
            _previousItemIds[itemId] = previousItemId;
        }

        if (string.IsNullOrEmpty(itemId))
        {
            return previousItemId;
        }

        return _previousItemIds.TryGetValue(itemId, out var storedPreviousItemId)
            ? storedPreviousItemId
            : previousItemId;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return;

        if (_ws?.State != WebSocketState.Open)
        {
            Logger.Log("OpenAIRealtimeTranscriber: Cannot send stop - not connected");
            return;
        }

        try
        {
            Logger.Log("OpenAIRealtimeTranscriber: Sending stop (commit + clear)");

            if (_sendChannel != null)
            {
                // Empty buffer as a stop marker
                await _sendChannel
                    .Writer.WriteAsync(new QueueItem(null, 0, true), ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber: StopAsync failed - {ex.Message}");
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
                Logger.Log("OpenAIRealtimeTranscriber: Closing WebSocket");
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                closeCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("OpenAIRealtimeTranscriber: Close timeout, aborting");
                    _ws.Abort();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber: DisconnectAsync error - {ex.Message}");
        }
        finally
        {
            await CleanupWebSocketAsync();
            OnDisconnected?.Invoke();
        }
    }

    private async Task HandleConnectionLostAsync(string reason)
    {
        Logger.Log($"OpenAIRealtimeTranscriber: Connection lost - {reason}");

        try
        {
            OnConnectionLost?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber: OnConnectionLost handler error - {ex.Message}");
        }

        await CleanupWebSocketAsync();
    }

    private async Task CleanupWebSocketAsync()
    {
        try
        {
            _connectionCts?.Cancel();

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

            _connectionCts?.Dispose();
            _connectionCts = null;
        }
        catch (Exception ex)
        {
            Logger.Log($"OpenAIRealtimeTranscriber: Cleanup error - {ex.Message}");
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
