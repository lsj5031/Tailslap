using System.Globalization;
using System.Text.Json;

internal enum UiaProbeMode
{
    Focused,
    Caret,
    Deep,
}

internal sealed class UiaProbeRequest
{
    public const string CommandName = "--uia-probe";

    public required UiaProbeMode Mode { get; init; }

    public long? ForegroundWindowHandle { get; init; }
}

internal sealed class UiaProbeResponse
{
    public required string Status { get; init; }

    public string? Text { get; init; }

    public string? Error { get; init; }

    public static UiaProbeResponse Success(string text) =>
        new() { Status = "success", Text = text };

    public static UiaProbeResponse Empty() => new() { Status = "empty" };

    public static UiaProbeResponse FromError(string error) =>
        new() { Status = "error", Error = error };
}

internal static class UiaProbeProtocol
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool IsProbeInvocation(string[] args) =>
        args.Length > 0
        && string.Equals(args[0], UiaProbeRequest.CommandName, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseArgs(string[] args, out UiaProbeRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (!IsProbeInvocation(args))
        {
            error = "Missing UIA probe command.";
            return false;
        }

        if (args.Length < 2 || args.Length > 3)
        {
            error = "Usage: --uia-probe <focused|caret|deep> [foreground-hwnd]";
            return false;
        }

        if (!TryParseMode(args[1], out var mode))
        {
            error = $"Unknown UIA probe mode: {args[1]}";
            return false;
        }

        long? hwnd = null;
        if (args.Length == 3)
        {
            if (
                !long.TryParse(
                    args[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed
                )
            )
            {
                error = $"Invalid foreground window handle: {args[2]}";
                return false;
            }

            hwnd = parsed;
        }

        request = new UiaProbeRequest { Mode = mode, ForegroundWindowHandle = hwnd };
        return true;
    }

    public static string Serialize(UiaProbeResponse response) =>
        JsonSerializer.Serialize(response, _jsonOptions);

    public static bool TryDeserialize(
        string json,
        out UiaProbeResponse? response,
        out string? error
    )
    {
        response = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Probe returned no output.";
            return false;
        }

        try
        {
            response = JsonSerializer.Deserialize<UiaProbeResponse>(json, _jsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid probe response JSON: {ex.Message}";
            return false;
        }

        if (response == null || string.IsNullOrWhiteSpace(response.Status))
        {
            error = "Probe response was missing status.";
            response = null;
            return false;
        }

        if (
            !string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(response.Status, "empty", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(response.Status, "error", StringComparison.OrdinalIgnoreCase)
        )
        {
            error = $"Unknown probe response status: {response.Status}";
            response = null;
            return false;
        }

        return true;
    }

    public static string ToArgument(UiaProbeMode mode) =>
        mode switch
        {
            UiaProbeMode.Focused => "focused",
            UiaProbeMode.Caret => "caret",
            UiaProbeMode.Deep => "deep",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static bool TryParseMode(string value, out UiaProbeMode mode)
    {
        if (string.Equals(value, "focused", StringComparison.OrdinalIgnoreCase))
        {
            mode = UiaProbeMode.Focused;
            return true;
        }

        if (string.Equals(value, "caret", StringComparison.OrdinalIgnoreCase))
        {
            mode = UiaProbeMode.Caret;
            return true;
        }

        if (string.Equals(value, "deep", StringComparison.OrdinalIgnoreCase))
        {
            mode = UiaProbeMode.Deep;
            return true;
        }

        mode = default;
        return false;
    }
}
