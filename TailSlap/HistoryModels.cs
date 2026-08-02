using System;

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string Model { get; set; } = "";
    public string OriginalCiphertext { get; set; } = "";
    public string RefinedCiphertext { get; set; } = "";

    /// <summary>
    /// Optional entry state: null/"success" for normal entries, "failed" when
    /// the operation did not complete. Kept as a string for forward compatibility.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// DPAPI-encrypted, truncated error summary for failed entries.
    /// </summary>
    public string? ErrorCiphertext { get; set; }
}

public sealed class TranscriptionHistoryEntry
{
    public DateTime Timestamp { get; set; }
    public string TextCiphertext { get; set; } = "";
    public int RecordingDurationMs { get; set; }

    /// <summary>Optional entry state: null/"success" or "failed" (see HistoryEntry.Status).</summary>
    public string? Status { get; set; }

    /// <summary>DPAPI-encrypted, truncated error summary for failed entries.</summary>
    public string? ErrorCiphertext { get; set; }
}
