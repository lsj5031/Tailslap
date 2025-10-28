using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

public sealed class AppConfig
{
    public bool AutoPaste { get; set; } = true;
    public HotkeyConfig Hotkey { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
}

public sealed class HotkeyConfig 
{ 
    public uint Modifiers { get; set; } = 0x0003;
    public uint Key { get; set; } = (uint)Keys.R;
}

public sealed class LlmConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "llama3.1";
    public double Temperature { get; set; } = 0.2;
    public int? MaxTokens { get; set; } = null;
    public string? ApiKeyEncrypted { get; set; } = null;
    public string? HttpReferer { get; set; } = null;
    public string? XTitle { get; set; } = null;

    [JsonIgnore]
    public string? ApiKey
    {
        get => string.IsNullOrEmpty(ApiKeyEncrypted) ? null : Dpapi.Unprotect(ApiKeyEncrypted);
        set => ApiKeyEncrypted = string.IsNullOrEmpty(value) ? null : Dpapi.Protect(value!);
    }
}

public sealed class ConfigService
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailslapCloud");
    private static string FilePath => Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppConfig LoadOrDefault()
    {
        try
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            if (!File.Exists(FilePath)) { var c = new AppConfig(); Save(c); return c; }
            var txt = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppConfig>(txt, JsonOpts) ?? new AppConfig();
        }
        catch { return new AppConfig(); }
    }

    public void Save(AppConfig cfg)
    {
        if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    public string GetConfigPath() => FilePath;
}
