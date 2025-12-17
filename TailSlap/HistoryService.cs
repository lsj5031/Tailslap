using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

public static class HistoryService
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailSlap");
    private static string FilePath => Path.Combine(Dir, "history.jsonl");
    private static string TranscriptionFilePath => Path.Combine(Dir, "transcription-history.jsonl");
    private const int MaxEntries = 50; // MaxEntries bounds history size; Trim() intentionally re-reads and rewrites only the last 50 entries for simplicity with small limits

    public sealed class Entry
    {
        public DateTime Timestamp { get; set; }
        public string Model { get; set; } = "";
        public string Original { get; set; } = "";
        public string Refined { get; set; } = "";
    }

    public sealed class TranscriptionEntry
    {
        public DateTime Timestamp { get; set; }
        public string Text { get; set; } = "";
        public int RecordingDurationMs { get; set; }
    }

    public static void Append(string original, string refined, string model)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var entry = new Entry { Timestamp = DateTime.Now, Model = model, Original = original, Refined = refined };
            File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            Trim();
        }
        catch (Exception ex)
        {
            try { Logger.Log($"History append failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to save history entry. Check disk space and permissions.");
        }
    }

    public static List<Entry> ReadAll()
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { var e = JsonSerializer.Deserialize<Entry>(line); if (e != null) list.Add(e); } 
                catch (JsonException ex)
                {
                    try { Logger.Log($"History entry parse error: {ex.Message}"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"History read failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to read history. File may be corrupted.");
        }
        return list;
    }

    private static void Trim()
    {
        try
        {
            var all = ReadAll();
            if (all.Count <= MaxEntries) return;
            var trimmed = all.GetRange(all.Count - MaxEntries, MaxEntries);
            var lines = new List<string>();
            foreach (var e in trimmed) lines.Add(JsonSerializer.Serialize(e));
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"History trim failed: {ex.Message}"); } catch { }
            NotificationService.ShowWarning("Failed to trim history file. File may grow large.");
        }
    }

    public static void AppendTranscription(string text, int recordingDurationMs)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var entry = new TranscriptionEntry 
            { 
                Timestamp = DateTime.Now, 
                Text = text,
                RecordingDurationMs = recordingDurationMs
            };
            File.AppendAllText(TranscriptionFilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            TrimTranscriptions();
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Transcription history append failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to save transcription history entry. Check disk space and permissions.");
        }
    }

    public static List<TranscriptionEntry> ReadAllTranscriptions()
    {
        var list = new List<TranscriptionEntry>();
        try
        {
            if (!File.Exists(TranscriptionFilePath)) return list;
            foreach (var line in File.ReadAllLines(TranscriptionFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { var e = JsonSerializer.Deserialize<TranscriptionEntry>(line); if (e != null) list.Add(e); } 
                catch (JsonException ex)
                {
                    try { Logger.Log($"Transcription history entry parse error: {ex.Message}"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Transcription history read failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to read transcription history. File may be corrupted.");
        }
        return list;
    }

    private static void TrimTranscriptions()
    {
        try
        {
            var all = ReadAllTranscriptions();
            if (all.Count <= MaxEntries) return;
            var trimmed = all.GetRange(all.Count - MaxEntries, MaxEntries);
            var lines = new List<string>();
            foreach (var e in trimmed) lines.Add(JsonSerializer.Serialize(e));
            File.WriteAllLines(TranscriptionFilePath, lines);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Transcription history trim failed: {ex.Message}"); } catch { }
            NotificationService.ShowWarning("Failed to trim transcription history file. File may grow large.");
        }
    }

}
