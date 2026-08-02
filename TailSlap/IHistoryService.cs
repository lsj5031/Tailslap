using System;
using System.Collections.Generic;

public interface IHistoryService
{
    void Append(string original, string refined, string model);
    List<(
        DateTime Timestamp,
        string Model,
        string Original,
        string Refined,
        string? Status,
        string? Error
    )> ReadAll();
    void AppendTranscription(string text, int recordingDurationMs);
    List<(
        DateTime Timestamp,
        string Text,
        int RecordingDurationMs,
        string? Status,
        string? Error
    )> ReadAllTranscriptions();

    /// <summary>Records a failed transcription (with optional partial text) so failures are not lost.</summary>
    void AppendTranscriptionFailure(
        string? partialText,
        int recordingDurationMs,
        string errorSummary
    );

    /// <summary>Records a failed refinement (original text preserved, no refined output).</summary>
    void AppendRefinementFailure(string original, string errorSummary);

    void ClearRefinementHistory();
    void ClearTranscriptionHistory();
    void ClearAll();
}
