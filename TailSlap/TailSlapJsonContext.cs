using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(TranscriptionHistoryEntry))]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(RealtimeTranscriptionMessage))]
[JsonSerializable(typeof(LogEntry))]
internal partial class TailSlapJsonContext : JsonSerializerContext { }
