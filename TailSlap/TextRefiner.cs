using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TailSlap;

public sealed class TextRefiner : ITextRefiner
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private const string ShortOutputRecoveryPrompt = """
        The previous response was too short and dropped important content.

        Rewrite the full input into polished professional writing while preserving all substantive meaning.
        Do not summarize, shorten to a fragment, or return only one corrected phrase unless the input itself is that short.
        Remove filler words and transcription artifacts, fix grammar and punctuation, and improve structure for readability.
        Return only the complete polished text.
        """;
    private const string ShortOutputErrorMessage =
        "Provider returned an incomplete refinement. Try lowering temperature or using a more reliable model.";

    private readonly LlmConfig _cfg;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = TailSlapJsonContext.Default,
    };

    public TextRefiner(LlmConfig cfg, IHttpClientFactory httpClientFactory)
    {
        _cfg = cfg;
        _httpClientFactory =
            httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

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

        DiagnosticsEventSource.Log.RefinementStarted(_cfg.Model, text?.Length ?? 0);
        var startTime = DateTime.UtcNow;

        var baseUrl = _cfg.BaseUrl.TrimEnd('/');
        var endpoint = baseUrl.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : Combine(baseUrl, "chat/completions");
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
                $"LLM input fingerprint: len={text?.Length ?? 0}, sha256={Hashing.Sha256Hex(text ?? string.Empty)}"
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
                new() { Role = "system", Content = _cfg.GetEffectiveRefinementPrompt() },
                new() { Role = "user", Content = BuildRewriteMessage(text ?? string.Empty) },
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
                    request.Headers.Authorization = new("Bearer", _cfg.ApiKey.Trim());
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
                    var errorBody = await resp
                        .Content.ReadAsStringAsync(timeoutCts.Token)
                        .ConfigureAwait(false);
                    var userFriendlyError = GetUserFriendlyError(resp.StatusCode, errorBody);
                    NotificationService.ShowError($"LLM request failed: {userFriendlyError}");
                    throw new Exception($"LLM error {resp.StatusCode}: {errorBody}");
                }

                var result = await ReadResultAsync(resp, timeoutCts.Token).ConfigureAwait(false);
                if (LooksSuspiciouslyShort(text ?? string.Empty, result))
                {
                    try
                    {
                        Logger.Log(
                            $"LLM output suspiciously short; retrying with recovery prompt. inputLen={(text ?? string.Empty).Length}, outputLen={result.Length}"
                        );
                    }
                    catch { }

                    var recovered = await RetryForShortOutputAsync(
                            http,
                            endpoint,
                            text ?? string.Empty,
                            ct
                        )
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(recovered))
                    {
                        result = recovered;
                    }
                }

                if (LooksSuspiciouslyShort(text ?? string.Empty, result))
                {
                    try
                    {
                        Logger.Log(
                            $"LLM output still suspiciously short after recovery. inputLen={(text ?? string.Empty).Length}, outputLen={result.Length}"
                        );
                    }
                    catch { }

                    throw new InvalidOperationException(ShortOutputErrorMessage);
                }

                try
                {
                    Logger.Log(
                        $"LLM output fingerprint: len={result.Length}, sha256={Hashing.Sha256Hex(result)}"
                    );
                }
                catch { }

                var elapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                DiagnosticsEventSource.Log.RefinementCompleted(
                    elapsedMs,
                    result.Length,
                    _cfg.MaxTokens
                );

                return result;
            }
            catch (Exception ex) when (attempts > 0)
            {
                try
                {
                    Logger.Log("LLM exception: " + ex.Message + "; retrying in 1s");
                }
                catch { }
                DiagnosticsEventSource.Log.RefinementRetry(
                    2 - attempts,
                    ex.Message ?? "Unknown error",
                    1000
                );
                await Task.Delay(1000, ct);
            }
        }

        var finalElapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
        var finalError =
            $"LLM service unavailable after multiple attempts at {_cfg.BaseUrl}. Please check your connection and settings.";
        DiagnosticsEventSource.Log.RefinementFailed(finalError, null);
        NotificationService.ShowError(finalError);
        throw new Exception(finalError);
    }

    private async Task<string> RetryForShortOutputAsync(
        HttpClient http,
        string endpoint,
        string text,
        CancellationToken ct
    )
    {
        var retryRequest = new ChatRequest
        {
            Model = _cfg.Model,
            Temperature = Math.Min(_cfg.Temperature, 0.2),
            MaxTokens = _cfg.MaxTokens,
            Messages = new()
            {
                new() { Role = "system", Content = ShortOutputRecoveryPrompt },
                new() { Role = "user", Content = BuildRewriteMessage(text) },
            },
        };

        var json = JsonSerializer.Serialize(retryRequest, JsonOpts);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
            request.Headers.Authorization = new("Bearer", _cfg.ApiKey.Trim());
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
            Logger.Log($"LLM recovery response status: {(int)resp.StatusCode} {resp.StatusCode}");
        }
        catch { }

        if (!resp.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        return await ReadResultAsync(resp, timeoutCts.Token).ConfigureAwait(false);
    }

    private static bool LooksSuspiciouslyShort(string input, string output)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            return false;

        var trimmedInput = input.Trim();
        var trimmedOutput = output.Trim();

        if (trimmedInput.Length < 60)
            return false;

        if (trimmedOutput.Length >= 20)
            return false;

        return trimmedOutput.Length * 8 < trimmedInput.Length;
    }

    private static async Task<string> ReadResultAsync(
        HttpResponseMessage resp,
        CancellationToken ct
    )
    {
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed =
            JsonSerializer.Deserialize(body, TailSlapJsonContext.Default.ChatResponse)
            ?? throw new Exception("Invalid response JSON");
        if (parsed.Choices is not { Count: > 0 } || parsed.Choices[0].Message is null)
            throw new Exception("No choices in response");
        return parsed.Choices[0].Message.Content?.Trim() ?? "";
    }

    private static string BuildRewriteMessage(string text)
    {
        return """
                Rewrite the full text between the <source_text> tags below.
                Preserve all substantive meaning, but clean up dictation artifacts and improve readability.
                Return only the rewritten text, not instructions or commentary.

                <source_text>
                """
            + text
            + """

                </source_text>
                """;
    }

    private string GetUserFriendlyError(System.Net.HttpStatusCode statusCode, string errorBody)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "Invalid API key or authentication failed. Check your settings.",
            System.Net.HttpStatusCode.Forbidden => "Access forbidden. Verify your API permissions.",
            System.Net.HttpStatusCode.NotFound =>
                $"LLM endpoint not found. Check the Base URL in settings: {_cfg.BaseUrl}",
            System.Net.HttpStatusCode.BadRequest => "Invalid request. Check model configuration.",
            System.Net.HttpStatusCode.TooManyRequests =>
                "Rate limit exceeded. Please wait before trying again.",
            System.Net.HttpStatusCode.InternalServerError => "LLM server error. Try again later.",
            System.Net.HttpStatusCode.BadGateway => "LLM service unavailable. Try again later.",
            System.Net.HttpStatusCode.ServiceUnavailable =>
                "LLM service temporarily unavailable. Try again later.",
            System.Net.HttpStatusCode.GatewayTimeout =>
                $"LLM request timed out at {_cfg.BaseUrl}. Check your connection.",
            _ => $"Server error ({(int)statusCode}). Please try again.",
        };
    }

    private static string Combine(string a, string b) => a.EndsWith("/") ? a + b : a + "/" + b;
}
