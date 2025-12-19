using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

public sealed class TextRefiner : ITextRefiner
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly LlmConfig _cfg;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TextRefiner(LlmConfig cfg, IHttpClientFactory httpClientFactory)
    {
        _cfg = cfg;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

        var hasApiKey = !string.IsNullOrWhiteSpace(_cfg.ApiKey);
        var hasReferer = !string.IsNullOrWhiteSpace(_cfg.HttpReferer);
        var hasXTitle = !string.IsNullOrWhiteSpace(_cfg.XTitle);

        try
        {
            Logger.Log(
                $"LLM client init: baseUrl={_cfg.BaseUrl}, model={_cfg.Model}, temp={_cfg.Temperature}, "
                    + $"maxTokens={_cfg.MaxTokens?.ToString() ?? "null"}, hasApiKey={hasApiKey}, "
                    + $"hasReferer={hasReferer}, hasXTitle={hasXTitle}"
            );
        }
        catch { }
    }

    public async Task<string> RefineAsync(string text, CancellationToken ct = default)
    {
        if (!_cfg.Enabled)
        {
            var errorMsg = "LLM processing is disabled. Enable it in Settings.";
            NotificationService.ShowWarning(errorMsg);
            throw new InvalidOperationException(errorMsg);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            var errorMsg = "Cannot refine empty text.";
            NotificationService.ShowWarning(errorMsg);
            throw new ArgumentException(errorMsg);
        }

        var endpoint = Combine(_cfg.BaseUrl.TrimEnd('/'), "chat/completions");
        try
        {
            Logger.Log(
                $"Calling LLM endpoint: {endpoint}, model={_cfg.Model}, temp={_cfg.Temperature}"
            );
        }
        catch { }
        try
        {
            Logger.Log(
                $"LLM input fingerprint: len={text?.Length ?? 0}, sha256={Sha256Hex(text ?? string.Empty)}"
            );
        }
        catch { }

        var req = new ChatRequest
        {
            Model = _cfg.Model,
            Temperature = _cfg.Temperature,
            MaxTokens = _cfg.MaxTokens,
            Messages = new()
            {
                new()
                {
                    Role = "system",
                    Content =
                        "You are a concise writing assistant. Improve grammar, clarity, and tone without changing meaning. Preserve formatting and line breaks. Return only the improved text.",
                },
                new() { Role = "user", Content = text ?? string.Empty },
            },
        };

        using var http = _httpClientFactory.CreateClient(HttpClientNames.Default);

        int attempts = 2;
        while (attempts-- > 0)
        {
            try
            {
                var json = JsonSerializer.Serialize(req, JsonOpts);
                try
                {
                    Logger.Log($"LLM request json size={json.Length} chars");
                }
                catch { }

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
                    request.Headers.Authorization = new("Bearer", _cfg.ApiKey);
                if (!string.IsNullOrWhiteSpace(_cfg.HttpReferer))
                    request.Headers.TryAddWithoutValidation("Referer", _cfg.HttpReferer);
                if (!string.IsNullOrWhiteSpace(_cfg.XTitle))
                    request.Headers.TryAddWithoutValidation("X-Title", _cfg.XTitle);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(DefaultRequestTimeout);

                using var resp = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token
                    )
                    .ConfigureAwait(false);
                try
                {
                    Logger.Log($"LLM response status: {(int)resp.StatusCode} {resp.StatusCode}");
                }
                catch { }
                if (!resp.IsSuccessStatusCode)
                {
                    if (
                        (int)resp.StatusCode >= 500
                        || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    )
                    {
                        try
                        {
                            Logger.Log("Retryable status; backing off 1s");
                        }
                        catch { }
                        if (attempts > 0)
                        {
                            NotificationService.ShowWarning(
                                $"Server busy ({resp.StatusCode}). Retrying..."
                            );
                            await Task.Delay(1000, ct);
                            continue;
                        }
                    }
                    var errorBody = await resp.Content
                        .ReadAsStringAsync(timeoutCts.Token)
                        .ConfigureAwait(false);
                    var userFriendlyError = GetUserFriendlyError(resp.StatusCode, errorBody);
                    NotificationService.ShowError($"LLM request failed: {userFriendlyError}");
                    throw new Exception($"LLM error {resp.StatusCode}: {errorBody}");
                }

                var body = await resp.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                var parsed =
                    JsonSerializer.Deserialize<ChatResponse>(body, JsonOpts)
                    ?? throw new Exception("Invalid response JSON");
                if (parsed.Choices is not { Count: > 0 } || parsed.Choices[0].Message is null)
                    throw new Exception("No choices in response");
                var result = parsed.Choices[0].Message.Content?.Trim() ?? "";
                try
                {
                    Logger.Log(
                        $"LLM output fingerprint: len={result.Length}, sha256={Sha256Hex(result)}"
                    );
                }
                catch { }
                return result;
            }
            catch (Exception ex) when (attempts > 0)
            {
                try
                {
                    Logger.Log("LLM exception: " + ex.Message + "; retrying in 1s");
                }
                catch { }
                await Task.Delay(1000, ct);
            }
        }
        var finalError =
            "LLM service unavailable after multiple attempts. Please check your connection and settings.";
        NotificationService.ShowError(finalError);
        throw new Exception(finalError);
    }

    private static string GetUserFriendlyError(
        System.Net.HttpStatusCode statusCode,
        string errorBody
    )
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "Invalid API key or authentication failed. Check your settings.",
            System.Net.HttpStatusCode.Forbidden => "Access forbidden. Verify your API permissions.",
            System.Net.HttpStatusCode.NotFound =>
                "LLM endpoint not found. Check the Base URL in settings.",
            System.Net.HttpStatusCode.BadRequest => "Invalid request. Check model configuration.",
            System.Net.HttpStatusCode.TooManyRequests =>
                "Rate limit exceeded. Please wait before trying again.",
            System.Net.HttpStatusCode.InternalServerError => "LLM server error. Try again later.",
            System.Net.HttpStatusCode.BadGateway => "LLM service unavailable. Try again later.",
            System.Net.HttpStatusCode.ServiceUnavailable =>
                "LLM service temporarily unavailable. Try again later.",
            System.Net.HttpStatusCode.GatewayTimeout =>
                "LLM request timed out. Check your connection.",
            _ => $"Server error ({(int)statusCode}). Please try again.",
        };
    }

    private static string Combine(string a, string b) => a.EndsWith("/") ? a + b : a + "/" + b;

    private static string Sha256Hex(string s)
    {
        try
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(s);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
        }
    }

    private sealed class ChatRequest
    {
        public string Model { get; set; } = "";
        public List<Msg> Messages { get; set; } = new();
        public double Temperature { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }
    }

    private sealed class Msg
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class ChatResponse
    {
        public List<Choice> Choices { get; set; } = new();

        public sealed class Choice
        {
            public ChoiceMsg Message { get; set; } = new();
        }

        public sealed class ChoiceMsg
        {
            public string Role { get; set; } = "";
            public string Content { get; set; } = "";
        }
    }
}
