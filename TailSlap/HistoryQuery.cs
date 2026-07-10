using System;

namespace TailSlap;

/// <summary>
/// Pure helpers for filtering and formatting history for search/export UIs.
/// </summary>
public static class HistoryQuery
{
    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var q = query.Trim();
        foreach (var field in fields)
        {
            if (
                !string.IsNullOrEmpty(field)
                && field.Contains(q, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    public static string FormatRefinementExport(
        DateTime exportedAt,
        System.Collections.Generic.IEnumerable<(
            DateTime Timestamp,
            string Model,
            string Original,
            string Refined
        )> entries
    )
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# TailSlap refinement history export");
        sb.AppendLine($"# Exported: {exportedAt:O}");
        sb.AppendLine("# WARNING: This file is plaintext (not DPAPI-protected).");
        foreach (var (timestamp, model, original, refined) in entries)
        {
            sb.AppendLine("---");
            sb.AppendLine($"[{timestamp:O}] model={model}");
            sb.AppendLine("ORIGINAL:");
            sb.AppendLine(original);
            sb.AppendLine("REFINED:");
            sb.AppendLine(refined);
        }

        return sb.ToString();
    }

    public static string FormatTranscriptionExport(
        DateTime exportedAt,
        System.Collections.Generic.IEnumerable<(
            DateTime Timestamp,
            string Text,
            int RecordingDurationMs
        )> entries
    )
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# TailSlap transcription history export");
        sb.AppendLine($"# Exported: {exportedAt:O}");
        sb.AppendLine("# WARNING: This file is plaintext (not DPAPI-protected).");
        foreach (var (timestamp, text, durationMs) in entries)
        {
            sb.AppendLine("---");
            sb.AppendLine($"[{timestamp:O}] durationMs={durationMs}");
            sb.AppendLine(text);
        }

        return sb.ToString();
    }
}
