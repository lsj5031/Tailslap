namespace TailSlap;

public sealed class RealtimeTranscriptionUpdate
{
    public string Text { get; init; } = string.Empty;
    public bool IsFinal { get; init; }
    public string? ItemId { get; init; }
    public string? PreviousItemId { get; init; }
}
