using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

public enum TranscriberErrorType
{
    NetworkTimeout,
    ConnectionFailed,
    HttpError,
    ParseError,
    FormatError,
    Unknown
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
        string? responseText = null) : base(message, innerException)
    {
        ErrorType = errorType;
        StatusCode = statusCode;
        ResponseText = responseText;
    }

    public bool IsRetryable()
    {
        return ErrorType == TranscriberErrorType.NetworkTimeout ||
               ErrorType == TranscriberErrorType.ConnectionFailed;
    }
}

public sealed class RemoteTranscriber
{
    private readonly TranscriberConfig _config;
    private readonly HttpClient _httpClient;

    public RemoteTranscriber(TranscriberConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
        };
    }

    public async Task<string> TestConnectionAsync()
    {
        try
        {
            var headers = new MultipartFormDataContent();
            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                headers.Add(new StringContent(_config.ApiKey), "Authorization", $"Bearer {_config.ApiKey}");
            }

            // Create a short silence WAV file for testing
            var silenceWav = CreateSilenceWavBytes(durationSeconds: 0.6f);
            var audioContent = new ByteArrayContent(silenceWav);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

            var formData = new MultipartFormDataContent();
            formData.Add(audioContent, "file", "connection_test.wav");
            
            if (!string.IsNullOrEmpty(_config.Model))
            {
                formData.Add(new StringContent(_config.Model), "model");
            }

            var response = await _httpClient.PostAsync(_config.BaseUrl, formData);
            
            var responseText = await response.Content.ReadAsStringAsync();
            
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new TranscriberException(
                    TranscriberErrorType.HttpError,
                    $"Remote API returned error (HTTP {(int)response.StatusCode})",
                    statusCode: (int)response.StatusCode,
                    responseText: responseText.Length > 500 ? responseText.Substring(0, 500) : responseText);
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
                    responseText: responseText.Length > 500 ? responseText.Substring(0, 500) : responseText);
            }
        }
        catch (TranscriberException)
        {
            throw;
        }
        catch (TaskCanceledException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.NetworkTimeout,
                $"Remote API request timed out after {_config.TimeoutSeconds}s",
                e);
        }
        catch (HttpRequestException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.ConnectionFailed,
                "Failed to connect to remote API",
                e);
        }
        catch (Exception e)
        {
            throw new TranscriberException(
                TranscriberErrorType.Unknown,
                "Unexpected error during remote connection test",
                e);
        }
    }

    public async Task<string> TranscribeAudioAsync(string audioFilePath)
    {
        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
        }

        try
        {
            var audioBytes = await File.ReadAllBytesAsync(audioFilePath);
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");

            var formData = new MultipartFormDataContent();
            formData.Add(audioContent, "file", Path.GetFileName(audioFilePath));
            
            if (!string.IsNullOrEmpty(_config.Model))
            {
                formData.Add(new StringContent(_config.Model), "model");
            }

            if (!string.IsNullOrEmpty(_config.ApiKey))
            {
                formData.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
            }

            var response = await _httpClient.PostAsync(_config.BaseUrl, formData);
            
            var responseText = await response.Content.ReadAsStringAsync();
            
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new TranscriberException(
                    TranscriberErrorType.HttpError,
                    $"Remote API returned error (HTTP {(int)response.StatusCode})",
                    statusCode: (int)response.StatusCode,
                    responseText: responseText.Length > 500 ? responseText.Substring(0, 500) : responseText);
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
                    responseText: responseText.Length > 500 ? responseText.Substring(0, 500) : responseText);
            }
        }
        catch (TranscriberException)
        {
            throw;
        }
        catch (TaskCanceledException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.NetworkTimeout,
                $"Remote API request timed out after {_config.TimeoutSeconds}s",
                e);
        }
        catch (HttpRequestException e)
        {
            throw new TranscriberException(
                TranscriberErrorType.ConnectionFailed,
                "Failed to connect to remote API",
                e);
        }
        catch (Exception e)
        {
            throw new TranscriberException(
                TranscriberErrorType.Unknown,
                "Unexpected error during remote transcription",
                e);
        }
    }

    private static string ExtractTextFromResponse(JsonElement response)
    {
        // Try common top-level keys
        foreach (var key in new[] { "text", "transcription", "result" })
        {
            if (response.TryGetProperty(key, out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString() ?? "";
            }
        }

        // Try results array
        if (response.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var resultsArray = results.EnumerateArray();
            if (resultsArray.MoveNext() && resultsArray.Current.ValueKind == JsonValueKind.Object)
            {
                var firstResult = resultsArray.Current;
                foreach (var key in new[] { "text", "transcription" })
                {
                    if (firstResult.TryGetProperty(key, out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        return textElement.GetString() ?? "";
                    }
                }
            }
            else if (resultsArray.MoveNext() && resultsArray.Current.ValueKind == JsonValueKind.String)
            {
                return resultsArray.Current.GetString() ?? "";
            }
        }

        // Try nested data object
        if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "text", "transcription", "result" })
            {
                if (data.TryGetProperty(key, out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    return textElement.GetString() ?? "";
                }
            }
        }

        throw new TranscriberException(
            TranscriberErrorType.ParseError,
            "API response does not contain transcription text",
            responseText: response.ToString());
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