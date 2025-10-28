using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

public sealed class CloudRefiner
{
    private readonly LlmConfig _cfg;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CloudRefiner(LlmConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey)) _http.DefaultRequestHeaders.Authorization = new("Bearer", _cfg.ApiKey);
        if (!string.IsNullOrWhiteSpace(_cfg.HttpReferer)) _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", _cfg.HttpReferer);
        if (!string.IsNullOrWhiteSpace(_cfg.XTitle)) _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", _cfg.XTitle);
    }

    public async Task<string> RefineAsync(string text, CancellationToken ct = default)
    {
        if (!_cfg.Enabled) throw new InvalidOperationException("Cloud LLM is disabled.");
        var endpoint = Combine(_cfg.BaseUrl.TrimEnd('/'), "chat/completions");
        try { Logger.Log($"Calling LLM endpoint: {endpoint}, model={_cfg.Model}, temp={_cfg.Temperature}"); } catch { }

        var req = new ChatRequest
        {
            Model = _cfg.Model,
            Temperature = _cfg.Temperature,
            MaxTokens = _cfg.MaxTokens,
            Messages = new()
            {
                new() { Role = "system", Content = "You are a concise writing assistant. Improve grammar, clarity, and tone without changing meaning. Preserve formatting and line breaks. Return only the improved text." },
                new() { Role = "user", Content = text }
            }
        };

        int attempts = 2;
        while (attempts-- > 0)
        {
            try
            {
                var json = JsonSerializer.Serialize(req, JsonOpts);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
                try { Logger.Log($"LLM response status: {(int)resp.StatusCode} {resp.StatusCode}"); } catch { }
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        try { Logger.Log("Retryable status; backing off 1s"); } catch { }
                        if (attempts > 0) await Task.Delay(1000, ct);
                        continue;
                    }
                    var errorBody = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Cloud LLM error {resp.StatusCode}: {errorBody}");
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<ChatResponse>(body, JsonOpts) ?? throw new Exception("Invalid response JSON");
                if (parsed.Choices is not { Count: > 0 } || parsed.Choices[0].Message is null) throw new Exception("No choices in response");
                return parsed.Choices[0].Message.Content?.Trim() ?? "";
            }
            catch (Exception ex) when (attempts > 0)
            {
                try { Logger.Log("LLM exception: " + ex.Message + "; retrying in 1s"); } catch { }
                await Task.Delay(1000, ct);
            }
        }
        throw new Exception("Max retries exceeded for LLM request.");
    }

    private static string Combine(string a, string b) => a.EndsWith("/") ? a + b : a + "/" + b;

    private sealed class ChatRequest
    {
        public string Model { get; set; } = "";
        public List<Msg> Messages { get; set; } = new();
        public double Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    }

    private sealed class Msg { public string Role { get; set; } = ""; public string Content { get; set; } = ""; }

    private sealed class ChatResponse
    {
        public List<Choice> Choices { get; set; } = new();
        public sealed class Choice { public ChoiceMsg Message { get; set; } = new(); }
        public sealed class ChoiceMsg { public string Role { get; set; } = ""; public string Content { get; set; } = ""; }
    }
}
