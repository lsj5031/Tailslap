using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

public sealed class AppConfig
{
    public bool AutoPaste { get; set; } = true;
    public bool UseClipboardFallback { get; set; } = true;
    public HotkeyConfig Hotkey { get; set; } = new(); // Ctrl+Alt+R for LLM
    public HotkeyConfig TranscriberHotkey { get; set; } =
        new() { Modifiers = 0x0003, Key = (uint)Keys.T }; // Ctrl+Alt+T for Transcriber
    public LlmConfig Llm { get; set; } = new();
    public TranscriberConfig Transcriber { get; set; } = new();
}

public sealed class HotkeyConfig
{
    public uint Modifiers { get; set; } = 0x0003; // ALT + CTRL
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

public sealed class TranscriberConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://localhost:18000/v1/audio/transcriptions";
    public string Model { get; set; } = "glm-nano-2512";
    public string? ApiKeyEncrypted { get; set; } = null;
    public int TimeoutSeconds { get; set; } = 30;
    public bool AutoPaste { get; set; } = true;
    public bool EnableVAD { get; set; } = false;
    public int SilenceThresholdMs { get; set; } = 1000;
    public int PreferredMicrophoneIndex { get; set; } = -1;

    [JsonIgnore]
    public string? ApiKey
    {
        get => string.IsNullOrEmpty(ApiKeyEncrypted) ? null : Dpapi.Unprotect(ApiKeyEncrypted);
        set => ApiKeyEncrypted = string.IsNullOrEmpty(value) ? null : Dpapi.Protect(value!);
    }
}

public sealed class ConfigService : IConfigService
{
    private static string Dir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TailSlap"
        );
    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = TailSlapJsonContext.Default,
    };

    private FileSystemWatcher? _watcher;
    public event Action? ConfigChanged;
    private DateTime _lastRead = DateTime.MinValue;

    public ConfigService()
    {
        SetupWatcher();
    }

    private void SetupWatcher()
    {
        try
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            _watcher = new FileSystemWatcher(Dir, "config.json")
            {
                NotifyFilter =
                    NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Watcher setup failed: {ex.Message}");
            }
            catch { }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: wait 500ms and check if last read was recent
        if (DateTime.Now - _lastRead < TimeSpan.FromMilliseconds(500))
            return;

        _lastRead = DateTime.Now;
        ConfigChanged?.Invoke();
    }

    public AppConfig LoadOrDefault()
    {
        try
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);
            if (!File.Exists(FilePath))
            {
                var c = new AppConfig();
                Save(c);
                return c;
            }
            var txt = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(txt, TailSlapJsonContext.Default.AppConfig)
                ?? new AppConfig();
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Config load failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            try
            {
                NotificationService.ShowError(
                    "Failed to load configuration. Using defaults instead."
                );
            }
            catch { }
            return new AppConfig();
        }
    }

    public void Save(AppConfig cfg)
    {
        try
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(cfg, TailSlapJsonContext.Default.AppConfig)
            );
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"Config save failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            try
            {
                NotificationService.ShowError(
                    "Failed to save configuration. Changes may not persist."
                );
            }
            catch { }
        }
    }

    public string GetConfigPath() => FilePath;

    public static bool IsValidUrl(string url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == "http" || uri.Scheme == "https");
    }

    public static bool IsValidTemperature(double temperature)
    {
        return temperature >= 0 && temperature <= 2;
    }

    public static bool IsValidMaxTokens(int maxTokens)
    {
        return maxTokens > 0 && maxTokens <= 32768;
    }

    public static bool IsValidModelName(string modelName)
    {
        return !string.IsNullOrWhiteSpace(modelName) && modelName.Trim().Length > 0;
    }

    public static bool IsValidTimeout(int timeoutSeconds)
    {
        return timeoutSeconds > 0 && timeoutSeconds <= 300;
    }

    public static bool IsValidSilenceThreshold(int thresholdMs)
    {
        return thresholdMs >= 100 && thresholdMs <= 5000;
    }

    public AppConfig CreateValidatedCopy()
    {
        var cfg = LoadOrDefault();

        // Validate and fix common issues
        if (!IsValidUrl(cfg.Llm.BaseUrl))
        {
            cfg.Llm.BaseUrl = "http://localhost:11434/v1";
        }

        if (!IsValidTemperature(cfg.Llm.Temperature))
        {
            cfg.Llm.Temperature = 0.2;
        }

        if (cfg.Llm.MaxTokens.HasValue && !IsValidMaxTokens(cfg.Llm.MaxTokens.Value))
        {
            cfg.Llm.MaxTokens = null;
        }

        if (!IsValidModelName(cfg.Llm.Model))
        {
            cfg.Llm.Model = "llama3.1";
        }

        // Validate transcriber settings
        if (!IsValidUrl(cfg.Transcriber.BaseUrl))
        {
            cfg.Transcriber.BaseUrl = "http://localhost:18000/v1/audio/transcriptions";
        }

        if (!IsValidModelName(cfg.Transcriber.Model))
        {
            cfg.Transcriber.Model = "glm-nano-2512";
        }

        if (!IsValidTimeout(cfg.Transcriber.TimeoutSeconds))
        {
            cfg.Transcriber.TimeoutSeconds = 30;
        }

        if (!IsValidSilenceThreshold(cfg.Transcriber.SilenceThresholdMs))
        {
            cfg.Transcriber.SilenceThresholdMs = 1000;
        }

        // Default transcriber hotkey to Ctrl+Alt+T
        if (cfg.TranscriberHotkey.Modifiers == 0 && cfg.TranscriberHotkey.Key == 0)
        {
            cfg.TranscriberHotkey.Modifiers = 0x0006; // CTRL + SHIFT
            cfg.TranscriberHotkey.Key = (uint)Keys.OemSemicolon;
        }

        return cfg;
    }
}
