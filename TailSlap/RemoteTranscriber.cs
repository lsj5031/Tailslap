using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TailSlap;

public enum TranscriberErrorType
{
    NetworkTimeout,
    ConnectionFailed,
    HttpError,
    ParseError,
    FormatError,
    Unknown,
}

public class TranscriberException : Exception
{
    public TranscriberErrorType ErrorType { get; }
    public int? StatusCode { get; }
    public string? ResponseText { get; }

    public TranscriberException(
        TranscriberErrorType errorType,
        string message,
        Exception? innerException = null,
        int? statusCode = null,
        string? responseText = null
    )
        : base(message, innerException)
    {
        ErrorType = errorType;
        StatusCode = statusCode;
        ResponseText = responseText;
    }

    public bool IsRetryable()
    {
        return ErrorType == TranscriberErrorType.NetworkTimeout
            || ErrorType == TranscriberErrorType.ConnectionFailed;
    }
}

public sealed class RemoteTranscriber : IRemoteTranscriber
{
    private readonly TranscriberConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public RemoteTranscriber(TranscriberConfig config, IHttpClientFactory httpClientFactory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClientFactory =
            httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Create a short silence WAV file for testing
            var silenceWav = CreateSilenceWavBytes(durationSeconds: 0.6f);

            var endpoint = _config.TranscriptionEndpoint;

            using var http = _httpClientFactory.CreateClient(HttpClientNames.Default);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

            using var audioContent = new ByteArrayContent(silenceWav);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "audio/wav"
            );

            using var formData = new MultipartFormDataContent();
            formData.Add(audioContent, "file", "connection_test.wav");
            AddCommonMultipartFields(formData);

            // Create request and add Authorization header (must be on HttpRequestMessage, not content)
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = formData,
            };
            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
            }

            using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token
                )
                .ConfigureAwait(false);

            var responseText = await response
                .Content.ReadAsStringAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            var responseFingerprint =
                $"len={responseText.Length}, sha256={Hashing.Sha256Hex(responseText)}";

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new TranscriberException(
                    TranscriberErrorType.HttpError,
                    $"Remote API returned error (HTTP {(int)response.StatusCode})",
                    statusCode: (int)response.StatusCode,
                    responseText: responseFingerprint
                );
            }
            try
            {
                var payload = JsonDocument.Parse(responseText);
                return ExtractTextFromResponse(payload.RootElement);
            }
            catch (JsonException e)
            {
                throw new TranscriberException(
                    TranscriberErrorType.ParseError,
                    "Remote API returned invalid JSON",
                    e,
                    responseText: responseFingerprint
                );
            }
        }
        catch (TranscriberException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.NetworkTimeout,
                $"Remote API request timed out after {_config.TimeoutSeconds}s at {_config.BaseUrl}",
                e
            );
        }
        catch (HttpRequestException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.ConnectionFailed,
                $"Failed to connect to remote API at {_config.BaseUrl}",
                e
            );
        }
        catch (Exception e)
        {
            Logger.LogWarning(
                $"TestConnectionAsync unexpected error: {e.GetType().Name}: {e.Message}"
            );
            throw new TranscriberException(
                TranscriberErrorType.Unknown,
                $"Unexpected error during remote connection test: {e.Message}",
                e
            );
        }
    }

    public async Task<string> TranscribeAudioAsync(
        string audioFilePath,
        CancellationToken ct = default
    )
    {
        var endpoint = _config.TranscriptionEndpoint;

        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
        }

        var fileInfo = new System.IO.FileInfo(audioFilePath);
        Logger.Log($"TranscribeAudioAsync: file={audioFilePath}, size={fileInfo.Length} bytes");

        // Retry logic: 2 attempts, 1s backoff (matches TextRefiner pattern)
        int attempts = 2;
        int attemptNumber = 0;
        while (attempts-- > 0)
        {
            attemptNumber++;
            Logger.Log($"Transcription attempt {attemptNumber}/2");
            try
            {
                using var http = _httpClientFactory.CreateClient(HttpClientNames.Default);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

                // Use FileStream for memory efficiency with large files
                using var fileStream = new FileStream(
                    audioFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    true
                );
                using var audioContent = new StreamContent(fileStream);
                audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "audio/wav"
                );

                using var formData = new MultipartFormDataContent();
                formData.Add(audioContent, "file", Path.GetFileName(audioFilePath));
                AddCommonMultipartFields(formData);

                if (!string.IsNullOrEmpty(_config.Model))
                {
                    Logger.Log($"Added model to request: {_config.Model}");
                }

                Logger.Log($"Posting to {endpoint}");

                // Create request and add Authorization header (must be on HttpRequestMessage, not content)
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = formData,
                };
                if (!string.IsNullOrEmpty(_config.ApiKey))
                {
                    request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
                    Logger.Log("Added Authorization header");
                }

                using var response = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token
                    )
                    .ConfigureAwait(false);

                Logger.Log(
                    $"Received response: HTTP {(int)response.StatusCode} {response.StatusCode}"
                );
                var responseText = await response
                    .Content.ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                var responseFingerprint =
                    $"len={responseText.Length}, sha256={Hashing.Sha256Hex(responseText)}";
                Logger.Log($"Response body fingerprint: {responseFingerprint}");

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Logger.LogWarning($"Error response fingerprint: {responseFingerprint}");
                    throw new TranscriberException(
                        TranscriberErrorType.HttpError,
                        $"Remote API returned error (HTTP {(int)response.StatusCode})",
                        statusCode: (int)response.StatusCode,
                        responseText: responseFingerprint
                    );
                }

                try
                {
                    Logger.Log("Parsing JSON response");
                    var payload = JsonDocument.Parse(responseText);
                    Logger.Log("JSON parsed successfully");
                    var text = ExtractTextFromResponse(payload.RootElement);
                    Logger.Log(
                        $"Extracted text from response: len={text.Length}, sha256={Hashing.Sha256Hex(text)}"
                    );
                    return text;
                }
                catch (JsonException e)
                {
                    Logger.LogWarning($"JSON parsing failed: {e.Message}");
                    throw new TranscriberException(
                        TranscriberErrorType.ParseError,
                        "Remote API returned invalid JSON",
                        e,
                        responseText: FingerprintPayload(responseText)
                    );
                }
            }
            catch (TranscriberException ex)
            {
                if (ex.IsRetryable() && attempts > 0)
                {
                    try
                    {
                        Logger.LogWarning(
                            $"Transcription failed with retryable error: {ex.Message}; retrying in 1s"
                        );
                    }
                    catch { }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException e)
            {
                var ex = new TranscriberException(
                    TranscriberErrorType.NetworkTimeout,
                    $"Remote API request timed out after {_config.TimeoutSeconds}s at {_config.BaseUrl}",
                    e
                );
                if (attempts > 0)
                {
                    try
                    {
                        Logger.LogWarning($"Transcription timeout; retrying in 1s");
                    }
                    catch { }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                throw ex;
            }
            catch (HttpRequestException e)
            {
                var ex = new TranscriberException(
                    TranscriberErrorType.ConnectionFailed,
                    $"Failed to connect to remote API at {_config.BaseUrl}",
                    e
                );
                if (attempts > 0)
                {
                    try
                    {
                        Logger.LogWarning($"Transcription connection failed; retrying in 1s");
                    }
                    catch { }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                throw ex;
            }
            catch (Exception e)
            {
                Logger.LogWarning(
                    $"TranscribeAudioAsync unexpected error: {e.GetType().Name}: {e.Message}"
                );
                throw new TranscriberException(
                    TranscriberErrorType.Unknown,
                    $"Unexpected error during remote transcription: {e.Message}",
                    e
                );
            }
        }

        throw new TranscriberException(
            TranscriberErrorType.Unknown,
            "Transcription failed after multiple retries"
        );
    }

    public async IAsyncEnumerable<string> TranscribeStreamingAsync(
        string audioFilePath,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var endpoint = _config.TranscriptionEndpoint;

        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
        }

        var fileInfo = new FileInfo(audioFilePath);
        Logger.Log($"TranscribeStreamingAsync: file={audioFilePath}, size={fileInfo.Length} bytes");

        var result = await SendStreamingRequestAsync(audioFilePath, endpoint, ct)
            .ConfigureAwait(false);

        using (result.Response)
        {
            if (!result.IsStreaming)
            {
                // Server doesn't support streaming, fall back to reading full response
                Logger.Log("Server returned non-streaming response, yielding full text");
                var text = ExtractTextFromResponseString(result.NonStreamingText ?? "");
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }
                yield break;
            }

            // Read SSE / NDJSON stream.
            // Formats accepted per line:
            //   data: <plain text>          (glm-asr style plain-text chunks)
            //   data: {json}                (OpenAI-style JSON events — text is extracted)
            //   {json}                      (raw NDJSON line)
            //   data: [DONE]                (stream end marker)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
            using var stream = await result
                .Response.Content.ReadAsStreamAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while (
                (line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) != null
            )
            {
                ct.ThrowIfCancellationRequested();

                // Skip empty lines (SSE events are separated by \n\n)
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string? payload = null;
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    payload = line.Substring(6); // "data: " is 6 chars
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    payload = line.Substring(5).TrimStart();
                }
                else if (
                    result.IsNdjson
                    || line.TrimStart().StartsWith("{", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("[", StringComparison.Ordinal)
                )
                {
                    // Raw NDJSON line or a JSON payload without the "data:" prefix
                    payload = line;
                }

                if (payload == null)
                {
                    continue; // e.g. "event: xxx" lines — ignored
                }

                // Check for stream end
                if (payload == "[DONE]")
                {
                    Logger.Log("Streaming completed with [DONE]");
                    yield break;
                }

                // Check for error
                if (payload.StartsWith("[Error:", StringComparison.Ordinal))
                {
                    Logger.LogWarning($"Streaming error chunk: {FingerprintPayload(payload)}");
                    throw new TranscriberException(
                        TranscriberErrorType.HttpError,
                        "Remote streaming error",
                        responseText: FingerprintPayload(payload)
                    );
                }

                if (!TryExtractTextFromChunk(payload, out var chunkText) || chunkText.Length == 0)
                {
                    continue;
                }

                Logger.Log(
                    $"Streaming chunk: len={chunkText.Length}, sha256={Hashing.Sha256Hex(chunkText)}"
                );
                yield return chunkText;
            }
        }
    }

    /// <summary>
    /// Sends the streaming transcription request with retry for retryable
    /// connection-phase failures (timeout / connection refused). Retries only
    /// before any content has been streamed.
    /// </summary>
    private async Task<StreamingHttpResult> SendStreamingRequestAsync(
        string audioFilePath,
        Uri endpoint,
        CancellationToken ct
    )
    {
        int attempts = 2;
        while (true)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var http = _httpClientFactory.CreateClient(HttpClientNames.Default);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

                // Use FileStream for memory efficiency
                using var fileStream = new FileStream(
                    audioFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    true
                );
                using var audioContent = new StreamContent(fileStream);
                audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "audio/wav"
                );

                using var formData = new MultipartFormDataContent();
                formData.Add(audioContent, "file", Path.GetFileName(audioFilePath));
                AddCommonMultipartFields(formData);

                // Request streaming response
                formData.Add(new StringContent("true"), "stream");

                Logger.Log($"Posting streaming request to {endpoint}");

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = formData,
                };
                if (!string.IsNullOrEmpty(_config.ApiKey))
                {
                    request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
                }

                // NOTE: the response is deliberately NOT disposed here — ownership is
                // transferred to the caller (TranscribeStreamingAsync), which disposes it
                // after consuming the stream. It is disposed in the catch/finally paths
                // below if an error occurs before transfer.
                response = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token
                    )
                    .ConfigureAwait(false);

                Logger.Log(
                    $"Streaming response: HTTP {(int)response.StatusCode} {response.StatusCode}"
                );

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response
                        .Content.ReadAsStringAsync(timeoutCts.Token)
                        .ConfigureAwait(false);
                    Logger.LogWarning($"Streaming error response: {FingerprintPayload(errorText)}");
                    throw new TranscriberException(
                        TranscriberErrorType.HttpError,
                        $"Remote API returned error (HTTP {(int)response.StatusCode})",
                        statusCode: (int)response.StatusCode,
                        responseText: FingerprintPayload(errorText)
                    );
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                Logger.Log($"Streaming content type: {contentType}");

                // Check if response is actually streaming (SSE or chunked)
                bool isStreaming =
                    contentType.Contains("text/event-stream")
                    || contentType.Contains("application/x-ndjson")
                    || response.Headers.TransferEncodingChunked == true;
                bool isNdjson = contentType.Contains("application/x-ndjson");

                if (!isStreaming)
                {
                    var fullText = await response
                        .Content.ReadAsStringAsync(timeoutCts.Token)
                        .ConfigureAwait(false);
                    return new StreamingHttpResult(response, false, isNdjson, fullText);
                }

                return new StreamingHttpResult(response, true, isNdjson, null);
            }
            catch (TranscriberException ex) when (ex.IsRetryable() && attempts > 1)
            {
                response?.Dispose();
                attempts--;
                Logger.LogWarning($"Streaming connection failed; retrying in 1s: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempts > 1)
            {
                response?.Dispose();
                attempts--;
                Logger.LogWarning($"Streaming connection error; retrying in 1s: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempts > 1)
            {
                response?.Dispose();
                attempts--;
                Logger.LogWarning(
                    $"Streaming request timed out after {_config.TimeoutSeconds}s; retrying in 1s: {ex.Message}"
                );
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Final attempt (or a timeout that cannot be retried): normalize to
                // the TranscriberException contract used everywhere else.
                response?.Dispose();
                throw new TranscriberException(
                    TranscriberErrorType.NetworkTimeout,
                    $"Remote API request timed out after {_config.TimeoutSeconds}s at {_config.BaseUrl}",
                    ex
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }

    private sealed record StreamingHttpResult(
        HttpResponseMessage Response,
        bool IsStreaming,
        bool IsNdjson,
        string? NonStreamingText
    );

    /// <summary>
    /// Converts a single streaming payload into text. JSON payloads have their
    /// text extracted so raw JSON is never typed into the target application;
    /// plain-text payloads are passed through unchanged.
    /// </summary>
    private static bool TryExtractTextFromChunk(string payload, out string text)
    {
        var trimmed = payload.TrimStart();
        if (trimmed.Length == 0)
        {
            text = "";
            return true;
        }

        bool looksLikeJson = trimmed[0] == '{' || trimmed[0] == '[';
        if (looksLikeJson)
        {
            var extracted = ExtractTextFromStreamChunk(payload);
            if (!string.IsNullOrEmpty(extracted))
            {
                text = extracted;
                return true;
            }

            Logger.LogWarning(
                $"Streaming chunk is JSON without recognized text; skipping ({FingerprintPayload(payload)})"
            );
            text = "";
            return false;
        }

        text = payload; // plain-text chunk
        return true;
    }

    private void AddCommonMultipartFields(MultipartFormDataContent formData)
    {
        if (!string.IsNullOrEmpty(_config.Model))
        {
            formData.Add(new StringContent(_config.Model), "model");
        }

        var language = _config.Language?.Trim();
        if (!string.IsNullOrEmpty(language))
        {
            formData.Add(new StringContent(language), "language");
        }
    }

    private static string ExtractTextFromStreamChunk(string jsonData)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonData);
            var root = doc.RootElement;

            // Try common streaming formats
            // Format 1: { "text": "..." } or { "content": "..." }
            foreach (var key in new[] { "text", "content", "transcription", "delta" })
            {
                if (
                    root.TryGetProperty(key, out var textElement)
                    && textElement.ValueKind == JsonValueKind.String
                )
                {
                    return textElement.GetString() ?? "";
                }
            }

            // Format 2: { "choices": [{ "delta": { "content": "..." } }] } (OpenAI style)
            if (
                root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (
                        choice.TryGetProperty("delta", out var delta)
                        && delta.ValueKind == JsonValueKind.Object
                    )
                    {
                        if (
                            delta.TryGetProperty("content", out var content)
                            && content.ValueKind == JsonValueKind.String
                        )
                        {
                            return content.GetString() ?? "";
                        }
                        if (
                            delta.TryGetProperty("text", out var text)
                            && text.ValueKind == JsonValueKind.String
                        )
                        {
                            return text.GetString() ?? "";
                        }
                    }
                    // Also check direct text in choice
                    if (
                        choice.TryGetProperty("text", out var choiceText)
                        && choiceText.ValueKind == JsonValueKind.String
                    )
                    {
                        return choiceText.GetString() ?? "";
                    }
                }
            }

            // Format 3: { "result": { "text": "..." } }
            if (
                root.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
            )
            {
                if (
                    result.TryGetProperty("text", out var resultText)
                    && resultText.ValueKind == JsonValueKind.String
                )
                {
                    return resultText.GetString() ?? "";
                }
            }

            return "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string ExtractTextFromResponseString(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            return ExtractTextFromResponse(doc.RootElement);
        }
        catch
        {
            // Not pure JSON. If the body looks like SSE lines (some servers return
            // SSE content with an application/json content type), extract the
            // data: payloads instead of typing the raw stream into the target app.
            if (
                responseText.StartsWith("data:", StringComparison.Ordinal)
                || responseText.Contains("\ndata:", StringComparison.Ordinal)
            )
            {
                var sb = new StringBuilder();
                foreach (var rawLine in responseText.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                        line = line.Substring(6);
                    else if (line.StartsWith("data:", StringComparison.Ordinal))
                        line = line.Substring(5).TrimStart();
                    else
                        continue;

                    if (line == "[DONE]")
                        break;
                    if (line.StartsWith("[Error:", StringComparison.Ordinal))
                        continue;

                    if (TryExtractTextFromChunk(line, out var chunk) && chunk.Length > 0)
                        sb.Append(chunk);
                }
                return sb.ToString();
            }

            return responseText;
        }
    }

    private static string ExtractTextFromResponse(JsonElement response)
    {
        // Try common top-level keys
        foreach (var key in new[] { "text", "transcription", "result", "content" })
        {
            if (
                response.TryGetProperty(key, out var textElement)
                && textElement.ValueKind == JsonValueKind.String
            )
            {
                return textElement.GetString() ?? "";
            }
        }

        // Try choices array (OpenAI format)
        if (
            response.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
        )
        {
            var choicesArray = choices.EnumerateArray();
            if (choicesArray.MoveNext() && choicesArray.Current.ValueKind == JsonValueKind.Object)
            {
                var firstChoice = choicesArray.Current;
                // Try text directly in choice
                foreach (var key in new[] { "text", "transcription", "content" })
                {
                    if (
                        firstChoice.TryGetProperty(key, out var textElement)
                        && textElement.ValueKind == JsonValueKind.String
                    )
                    {
                        return textElement.GetString() ?? "";
                    }
                }
                // Try message.content (OpenAI format)
                if (
                    firstChoice.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.Object
                )
                {
                    if (
                        msg.TryGetProperty("content", out var msgContent)
                        && msgContent.ValueKind == JsonValueKind.String
                    )
                    {
                        return msgContent.GetString() ?? "";
                    }
                }
            }
        }

        // Try results array
        if (
            response.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array
        )
        {
            var resultsArray = results.EnumerateArray();
            if (resultsArray.MoveNext() && resultsArray.Current.ValueKind == JsonValueKind.Object)
            {
                var firstResult = resultsArray.Current;
                foreach (var key in new[] { "text", "transcription", "content" })
                {
                    if (
                        firstResult.TryGetProperty(key, out var textElement)
                        && textElement.ValueKind == JsonValueKind.String
                    )
                    {
                        return textElement.GetString() ?? "";
                    }
                }
            }
            else if (
                resultsArray.MoveNext()
                && resultsArray.Current.ValueKind == JsonValueKind.String
            )
            {
                return resultsArray.Current.GetString() ?? "";
            }
        }

        // Try nested data object
        if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "text", "transcription", "result", "content" })
            {
                if (
                    data.TryGetProperty(key, out var textElement)
                    && textElement.ValueKind == JsonValueKind.String
                )
                {
                    return textElement.GetString() ?? "";
                }
            }
            // Try nested structure in data
            if (
                data.TryGetProperty("text", out var dataText)
                && dataText.ValueKind == JsonValueKind.Object
            )
            {
                if (
                    dataText.TryGetProperty("content", out var textContent)
                    && textContent.ValueKind == JsonValueKind.String
                )
                {
                    return textContent.GetString() ?? "";
                }
            }
        }

        try
        {
            Logger.LogWarning(
                $"ExtractTextFromResponse: Could not find text in response structure: {FingerprintPayload(response.ToString())}"
            );
        }
        catch { }

        throw new TranscriberException(
            TranscriberErrorType.ParseError,
            "API response does not contain transcription text in any recognized format",
            responseText: FingerprintPayload(response.ToString())
        );
    }

    private static string FingerprintPayload(string? s) =>
        string.IsNullOrEmpty(s) ? "" : $"len={s.Length}, sha256={Hashing.Sha256Hex(s)}";

    private static byte[] CreateSilenceWavBytes(float durationSeconds)
    {
        const int sampleRate = 16000;
        int frameCount = Math.Max(1, (int)(durationSeconds * sampleRate));

        using var memoryStream = new MemoryStream();
        using var writer = new BinaryWriter(memoryStream);

        // WAV file header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + frameCount * 2); // File size - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size (16 for PCM)
        writer.Write((short)1); // AudioFormat (1 for PCM)
        writer.Write((short)1); // NumChannels (1 for mono)
        writer.Write(sampleRate); // SampleRate
        writer.Write(sampleRate * 2); // ByteRate (SampleRate * NumChannels * BitsPerSample/8)
        writer.Write((short)2); // BlockAlign (NumChannels * BitsPerSample/8)
        writer.Write((short)16); // BitsPerSample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frameCount * 2); // Subchunk2Size (NumSamples * NumChannels * BitsPerSample/8)

        // Write silence frames
        for (int i = 0; i < frameCount; i++)
        {
            writer.Write((short)0);
        }

        return memoryStream.ToArray();
    }
}
