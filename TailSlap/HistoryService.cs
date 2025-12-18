using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

public static class HistoryService
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailSlap");
    private static string FilePath => Path.Combine(Dir, "history.jsonl.encrypted");
    private static string TranscriptionFilePath => Path.Combine(Dir, "transcription-history.jsonl.encrypted");
    private const int MaxEntries = 50;

    public sealed class Entry
    {
        public DateTime Timestamp { get; set; }
        public string Model { get; set; } = "";
        public string OriginalCiphertext { get; set; } = "";
        public string RefinedCiphertext { get; set; } = "";
    }

    public sealed class TranscriptionEntry
    {
        public DateTime Timestamp { get; set; }
        public string TextCiphertext { get; set; } = "";
        public int RecordingDurationMs { get; set; }
    }

    private static string EncryptString(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            return Dpapi.Protect(plaintext);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"DPAPI history encryption failed: {ex.Message}"); } catch { }
            // Fail gracefully: return empty string rather than crash
            return "";
        }
    }

    private static string DecryptString(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";
        try
        {
            return Dpapi.Unprotect(ciphertext);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"DPAPI history decryption failed: {ex.Message}"); } catch { }
            return ""; // Return empty rather than crash; user can see there's corrupted data
        }
    }

    public static void Append(string original, string refined, string model)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            
            var entry = new Entry 
            { 
                Timestamp = DateTime.Now, 
                Model = model, // Model name isn't sensitive
                OriginalCiphertext = EncryptString(original),
                RefinedCiphertext = EncryptString(refined)
            };
            
            File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            Trim();
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted history append failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to save encrypted history entry. Check disk space and permissions.");
        }
    }

    public static List<(DateTime Timestamp, string Model, string Original, string Refined)> ReadAll()
    {
        var result = new List<(DateTime, string, string, string)>();
        try
        {
            if (!File.Exists(FilePath)) return result;
            
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try 
                { 
                    var entry = JsonSerializer.Deserialize<Entry>(line); 
                    if (entry != null)
                    {
                        var decryptedOriginal = DecryptString(entry.OriginalCiphertext);
                        var decryptedRefined = DecryptString(entry.RefinedCiphertext);
                        
                        result.Add((entry.Timestamp, entry.Model, decryptedOriginal, decryptedRefined));
                    }
                } 
                catch (JsonException ex)
                {
                    try { Logger.Log($"Encrypted history entry parse error: {ex.Message}"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted history read failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to read encrypted history. File may be corrupted.");
        }
        return result;
    }

    private static void Trim()
    {
        try
        {
            var all = ReadAll();
            if (all.Count <= MaxEntries) return;
            
            var trimmed = all.GetRange(all.Count - MaxEntries, MaxEntries);
            var lines = new List<string>();
            
            foreach (var (timestamp, model, original, refined) in trimmed)
            {
                var entry = new Entry
                {
                    Timestamp = timestamp,
                    Model = model,
                    OriginalCiphertext = EncryptString(original),
                    RefinedCiphertext = EncryptString(refined)
                };
                lines.Add(JsonSerializer.Serialize(entry));
            }
            
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted history trim failed: {ex.Message}"); } catch { }
            NotificationService.ShowWarning("Failed to trim encrypted history. File may grow large.");
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
                TextCiphertext = EncryptString(text),
                RecordingDurationMs = recordingDurationMs
            };
            
            File.AppendAllText(TranscriptionFilePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            TrimTranscriptions();
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted transcription history append failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to save encrypted transcription history. Check disk space and permissions.");
        }
    }

    public static List<(DateTime Timestamp, string Text, int RecordingDurationMs)> ReadAllTranscriptions()
    {
        var result = new List<(DateTime, string, int)>();
        try
        {
            if (!File.Exists(TranscriptionFilePath)) return result;
            
            foreach (var line in File.ReadAllLines(TranscriptionFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try 
                { 
                    var entry = JsonSerializer.Deserialize<TranscriptionEntry>(line); 
                    if (entry != null)
                    {
                        var decryptedText = DecryptString(entry.TextCiphertext);
                        result.Add((entry.Timestamp, decryptedText, entry.RecordingDurationMs));
                    }
                } 
                catch (JsonException ex)
                {
                    try { Logger.Log($"Encrypted transcription history parse error: {ex.Message}"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted transcription history read failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to read encrypted transcription history. File may be corrupted.");
        }
        return result;
    }

    private static void TrimTranscriptions()
    {
        try
        {
            var all = ReadAllTranscriptions();
            if (all.Count <= MaxEntries) return;
            
            var trimmed = all.GetRange(all.Count - MaxEntries, MaxEntries);
            var lines = new List<string>();
            
            foreach (var (timestamp, text, duration) in trimmed)
            {
                var entry = new TranscriptionEntry
                {
                    Timestamp = timestamp,
                    TextCiphertext = EncryptString(text),
                    RecordingDurationMs = duration
                };
                lines.Add(JsonSerializer.Serialize(entry));
            }
            
            File.WriteAllLines(TranscriptionFilePath, lines);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted transcription trim failed: {ex.Message}"); } catch { }
            NotificationService.ShowWarning("Failed to trim encrypted transcription history. File may grow large.");
        }
    }

    public static void ClearAll()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(TranscriptionFilePath)) File.Delete(TranscriptionFilePath);
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Failed to clear encrypted history: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to clear encrypted history files. Check file permissions.");
        }
    }
}