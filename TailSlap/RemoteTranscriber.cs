using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Create a short silence WAV file for testing
            var silenceWav = CreateSilenceWavBytes(durationSeconds: 0.6f);

            using var http = _httpClientFactory.CreateClient(HttpClientNames.Default);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

            using var audioContent = new ByteArrayContent(silenceWav);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "audio/wav"
            );

            using var formData = new MultipartFormDataContent();
            formData.Add(audioContent, "file", "connection_test.wav");

            if (!string.IsNullOrEmpty(_config.Model))
            {
                formData.Add(new StringContent(_config.Model), "model");
            }

            // Create request and add Authorization header (must be on HttpRequestMessage, not content)
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.BaseUrl)
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

            var responseText = await response.Content
                .ReadAsStringAsync(timeoutCts.Token)
                .ConfigureAwait(false);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new TranscriberException(
                    TranscriberErrorType.HttpError,
                    $"Remote API returned error (HTTP {(int)response.StatusCode})",
                    statusCode: (int)response.StatusCode,
                    responseText: responseText.Length > 500
                        ? responseText.Substring(0, 500)
                        : responseText
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
                    responseText: responseText.Length > 500
                        ? responseText.Substring(0, 500)
                        : responseText
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
                $"Remote API request timed out after {_config.TimeoutSeconds}s",
                e
            );
        }
        catch (HttpRequestException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.ConnectionFailed,
                "Failed to connect to remote API",
                e
            );
        }
        catch (Exception e)
        {
            Logger.Log($"TestConnectionAsync unexpected error: {e.GetType().Name}: {e.Message}");
            throw new TranscriberException(
                TranscriberErrorType.Unknown,
                $"Unexpected error during remote connection test: {e.Message}",
                e
            );
        }
    }

    public async Task<string> TranscribeAudioAsync(string audioFilePath, CancellationToken ct = default)
    {
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
                var audioBytes = await File.ReadAllBytesAsync(audioFilePath, ct).ConfigureAwait(false);
                Logger.Log($"Read {audioBytes.Length} bytes from audio file");

                using var http = _httpClientFactory.CreateClient(HttpClientNames.Default);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

                using var audioContent = new ByteArrayContent(audioBytes);
                audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "audio/wav"
                );

                using var formData = new MultipartFormDataContent();
                formData.Add(audioContent, "file", Path.GetFileName(audioFilePath));

                if (!string.IsNullOrEmpty(_config.Model))
                {
                    formData.Add(new StringContent(_config.Model), "model");
                    Logger.Log($"Added model to request: {_config.Model}");
                }

                Logger.Log($"Posting to {_config.BaseUrl}");

                // Create request and add Authorization header (must be on HttpRequestMessage, not content)
                using var request = new HttpRequestMessage(HttpMethod.Post, _config.BaseUrl)
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
                var responseText = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                Logger.Log(
                    $"Response body length: {responseText.Length}, content: {responseText.Substring(0, Math.Min(500, responseText.Length))}"
                );

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Logger.Log($"Error response: {responseText}");
                    throw new TranscriberException(
                        TranscriberErrorType.HttpError,
                        $"Remote API returned error (HTTP {(int)response.StatusCode})",
                        statusCode: (int)response.StatusCode,
                        responseText: responseText.Length > 500
                            ? responseText.Substring(0, 500)
                            : responseText
                    );
                }

                try
                {
                    Logger.Log("Parsing JSON response");
                    var payload = JsonDocument.Parse(responseText);
                    Logger.Log("JSON parsed successfully");
                    var text = ExtractTextFromResponse(payload.RootElement);
                    Logger.Log(
                        $"Extracted text from response: {text.Substring(0, Math.Min(100, text.Length))}"
                    );
                    return text;
                }
                catch (JsonException e)
                {
                    Logger.Log($"JSON parsing failed: {e.Message}");
                    throw new TranscriberException(
                        TranscriberErrorType.ParseError,
                        "Remote API returned invalid JSON",
                        e,
                        responseText: responseText.Length > 500
                            ? responseText.Substring(0, 500)
                            : responseText
                    );
                }
            }
            catch (TranscriberException ex)
            {
                if (ex.IsRetryable() && attempts > 0)
                {
                    try
                    {
                        Logger.Log(
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
                    $"Remote API request timed out after {_config.TimeoutSeconds}s",
                    e
                );
                if (attempts > 0)
                {
                    try
                    {
                        Logger.Log($"Transcription timeout; retrying in 1s");
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
                    "Failed to connect to remote API",
                    e
                );
                if (attempts > 0)
                {
                    try
                    {
                        Logger.Log($"Transcription connection failed; retrying in 1s");
                    }
                    catch { }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                throw ex;
            }
            catch (Exception e)
            {
                Logger.Log(
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
            Logger.Log(
                $"ExtractTextFromResponse: Could not find text in response structure: {response.ToString()[..Math.Min(500, response.ToString().Length)]}"
            );
        }
        catch { }

        throw new TranscriberException(
            TranscriberErrorType.ParseError,
            "API response does not contain transcription text in any recognized format",
            responseText: response.ToString()
        );
    }

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
