using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public static class HistoryService
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailSlap");
    private static string FilePath => Path.Combine(Dir, "history.jsonl");
    private const int MaxEntries = 50;

    public sealed class Entry
    {
        public DateTime Timestamp { get; set; }
        public string Model { get; set; } = "";
        public string Original { get; set; } = "";
        public string Refined { get; set; } = "";
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
        catch { }
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
                try { var e = JsonSerializer.Deserialize<Entry>(line); if (e != null) list.Add(e); } catch { }
            }
        }
        catch { }
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
        catch { }
    }
}
