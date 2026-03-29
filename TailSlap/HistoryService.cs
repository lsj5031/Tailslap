using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class HistoryService : IHistoryService
{
    private static string Dir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TailSlap"
        );
    private static string FilePath => Path.Combine(Dir, "history.jsonl.encrypted");
    private static string TranscriptionFilePath =>
        Path.Combine(Dir, "transcription-history.jsonl.encrypted");
    private const int MaxEntries = 50;

    private static readonly JsonSerializerOptions JsonLOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = TailSlapJsonContext.Default,
    };

    private int _refinementAppendCount = 0;
    private int _transcriptionAppendCount = 0;
    private const int TrimInterval = 10;

    private static string EncryptString(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return "";
        try
        {
            return Dpapi.Protect(plaintext);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"DPAPI history encryption failed: {ex.Message}");
            }
            catch { }
            // Fail gracefully: return empty string rather than crash
            return "";
        }
    }

    private static string DecryptString(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return "";
        try
        {
            return Dpapi.Unprotect(ciphertext);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"DPAPI history decryption failed: {ex.Message}");
            }
            catch { }
            return ""; // Return empty rather than crash; user can see there's corrupted data
        }
    }

    public void Append(string original, string refined, string model)
    {
        if (
            string.IsNullOrWhiteSpace(original)
            || string.IsNullOrWhiteSpace(refined)
            || string.IsNullOrWhiteSpace(model)
        )
        {
            try
            {
                Logger.Log("Encrypted history append skipped: original/refined/model was empty");
            }
            catch { }
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);

            var entry = new HistoryEntry
            {
                Timestamp = DateTime.Now,
                Model = model, // Model name isn't sensitive
                OriginalCiphertext = EncryptString(original),
                RefinedCiphertext = EncryptString(refined),
            };

            var line = JsonSerializer.Serialize(entry, JsonLOptions);
            int entrySize = line.Length;
            File.AppendAllText(FilePath, line + Environment.NewLine);
            DiagnosticsEventSource.Log.HistoryAppend("refinement", entrySize);
            _refinementAppendCount++;
            if (_refinementAppendCount >= TrimInterval)
            {
                _refinementAppendCount = 0;
                Trim();
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Encrypted history append failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError(
                "Failed to save encrypted history entry. Check disk space and permissions."
            );
        }
    }

    public List<(DateTime Timestamp, string Model, string Original, string Refined)> ReadAll()
    {
        var result = new List<(DateTime, string, string, string)>();
        try
        {
            if (!File.Exists(FilePath))
                return result;

            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(stream);
            foreach (var entryJson in ReadRawJsonEntries(reader))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(entryJson, JsonLOptions);
                    if (entry != null)
                    {
                        if (
                            string.IsNullOrWhiteSpace(entry.OriginalCiphertext)
                            || string.IsNullOrWhiteSpace(entry.RefinedCiphertext)
                        )
                        {
                            try
                            {
                                Logger.Log(
                                    "Encrypted history entry skipped: missing ciphertext payload"
                                );
                            }
                            catch { }
                            continue;
                        }

                        var decryptedOriginal = DecryptString(entry.OriginalCiphertext);
                        var decryptedRefined = DecryptString(entry.RefinedCiphertext);

                        result.Add(
                            (entry.Timestamp, entry.Model, decryptedOriginal, decryptedRefined)
                        );
                    }
                }
                catch (JsonException ex)
                {
                    try
                    {
                        Logger.Log(
                            $"Encrypted history entry parse error (entry too long or invalid): {entryJson.Length} chars. Error: {ex.Message}"
                        );
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Encrypted history read failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError(
                "Failed to read encrypted history. File may be corrupted."
            );
        }
        return result;
    }

    private void Trim()
    {
        TrimJsonlFile(FilePath, "refinement");
    }

    public void AppendTranscription(string text, int recordingDurationMs)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            try
            {
                Logger.Log("Encrypted transcription history append skipped: text was empty");
            }
            catch { }
            return;
        }

        try
        {
            Directory.CreateDirectory(Dir);

            var entry = new TranscriptionHistoryEntry
            {
                Timestamp = DateTime.Now,
                TextCiphertext = EncryptString(text),
                RecordingDurationMs = recordingDurationMs,
            };

            var line = JsonSerializer.Serialize(entry, JsonLOptions);
            int entrySize = line.Length;
            File.AppendAllText(TranscriptionFilePath, line + Environment.NewLine);
            DiagnosticsEventSource.Log.HistoryAppend("transcription", entrySize);
            _transcriptionAppendCount++;
            if (_transcriptionAppendCount >= TrimInterval)
            {
                _transcriptionAppendCount = 0;
                TrimTranscriptions();
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Encrypted transcription history append failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError(
                "Failed to save encrypted transcription history. Check disk space and permissions."
            );
        }
    }

    public List<(DateTime Timestamp, string Text, int RecordingDurationMs)> ReadAllTranscriptions()
    {
        var result = new List<(DateTime, string, int)>();
        try
        {
            if (!File.Exists(TranscriptionFilePath))
                return result;

            using var stream = new FileStream(
                TranscriptionFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            using var reader = new StreamReader(stream);
            foreach (var entryJson in ReadRawJsonEntries(reader))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<TranscriptionHistoryEntry>(
                        entryJson,
                        JsonLOptions
                    );
                    if (entry != null)
                    {
                        if (string.IsNullOrWhiteSpace(entry.TextCiphertext))
                        {
                            try
                            {
                                Logger.Log(
                                    "Encrypted transcription history entry skipped: missing ciphertext payload"
                                );
                            }
                            catch { }
                            continue;
                        }

                        var decryptedText = DecryptString(entry.TextCiphertext);
                        result.Add((entry.Timestamp, decryptedText, entry.RecordingDurationMs));
                    }
                }
                catch (JsonException ex)
                {
                    try
                    {
                        Logger.Log(
                            $"Encrypted transcription history parse error (entry too long or invalid): {entryJson.Length} chars. Error: {ex.Message}"
                        );
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Encrypted transcription history read failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError(
                "Failed to read encrypted transcription history. File may be corrupted."
            );
        }
        return result;
    }

    private void TrimTranscriptions()
    {
        TrimJsonlFile(TranscriptionFilePath, "transcription");
    }

    private void TrimJsonlFile(string filePath, string historyType)
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            using (
                var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                )
            )
            using (var reader = new StreamReader(stream))
            {
                var allEntries = ReadRawJsonEntries(reader);

                if (allEntries.Count <= MaxEntries)
                    return;

                int beforeCount = allEntries.Count;
                var trimmedEntries = allEntries.GetRange(allEntries.Count - MaxEntries, MaxEntries);
                int afterCount = trimmedEntries.Count;

                var tempPath = filePath + ".tmp";
                using (
                    var writeStream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                using (var writer = new StreamWriter(writeStream))
                {
                    foreach (var rawEntry in trimmedEntries)
                    {
                        writer.Write(rawEntry);
                        writer.WriteLine();
                    }
                }

                File.Move(tempPath, filePath, overwrite: true);

                DiagnosticsEventSource.Log.HistoryTrim(historyType, beforeCount, afterCount);
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Encrypted {historyType} trim failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowWarning(
                $"Failed to trim encrypted {historyType} history. File may grow large."
            );
        }
    }

    private static List<string> ReadRawJsonEntries(StreamReader reader)
    {
        var entries = new List<string>();
        var current = new StringBuilder();
        bool started = false;
        bool inString = false;
        bool isEscaped = false;
        int depth = 0;

        while (!reader.EndOfStream)
        {
            int value = reader.Read();
            if (value < 0)
                break;

            char c = (char)value;

            if (!started)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                if (c != '{')
                    continue;

                started = true;
                depth = 1;
                current.Append(c);
                continue;
            }

            current.Append(c);

            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                isEscaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    entries.Add(current.ToString().Trim());
                    current.Clear();
                    started = false;
                }
            }
        }

        if (started && current.Length > 0)
        {
            entries.Add(current.ToString().Trim());
        }

        return entries.Where(entry => !string.IsNullOrWhiteSpace(entry)).ToList();
    }

    public void ClearRefinementHistory()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Failed to clear encrypted refinement history: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError(
                "Failed to clear encrypted refinement history. Check file permissions."
            );
        }
    }

    public void ClearTranscriptionHistory()
    {
        try
        {
            if (File.Exists(TranscriptionFilePath))
                File.Delete(TranscriptionFilePath);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Failed to clear encrypted transcription history: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError(
                "Failed to clear encrypted transcription history. Check file permissions."
            );
        }
    }

    public void ClearAll()
    {
        ClearRefinementHistory();
        ClearTranscriptionHistory();
    }
}
