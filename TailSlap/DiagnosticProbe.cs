using System.Net.Http;

namespace TailSlap;

internal readonly record struct DiagnosticHttpResult(DiagnosticSeverity Severity, string Status);

internal static class DiagnosticProbe
{
    public static HttpRequestMessage CreateGetRequest(string url, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new("Bearer", apiKey.Trim());
        return request;
    }

    public static DiagnosticHttpResult ClassifyHttpStatus(
        int statusCode,
        bool postOnlyEndpoint,
        bool apiKeyConfigured
    )
    {
        if (statusCode >= 200 && statusCode <= 299)
            return new(DiagnosticSeverity.Success, "Reachable");

        if (postOnlyEndpoint && statusCode == 405)
            return new(DiagnosticSeverity.Success, "Reachable (POST required)");

        if (statusCode is 401 or 403)
        {
            return apiKeyConfigured
                ? new(DiagnosticSeverity.Error, "Authentication rejected")
                : new(DiagnosticSeverity.Warning, "Authentication required");
        }

        return new(DiagnosticSeverity.Warning, $"Server responded ({statusCode})");
    }
}
