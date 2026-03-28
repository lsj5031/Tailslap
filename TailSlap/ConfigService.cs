using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

public sealed class AppConfig
{
    public bool AutoPaste { get; set; } = true;
    public bool UseClipboardFallback { get; set; } = true;
    public HotkeyConfig Hotkey { get; set; } = new() { Modifiers = 0x0003, Key = (uint)Keys.R }; // Ctrl+Alt+R for LLM
    public HotkeyConfig TranscriberHotkey { get; set; } =
        new() { Modifiers = 0x0003, Key = (uint)Keys.T }; // Ctrl+Alt+T for Transcriber
    public HotkeyConfig StreamingTranscriberHotkey { get; set; } =
        new() { Modifiers = 0x0003, Key = (uint)Keys.Y }; // Ctrl+Alt+Y for Streaming Transcriber
    public LlmConfig Llm { get; set; } = new();
    public TranscriberConfig Transcriber { get; set; } = new();

    public AppConfig Clone()
    {
        return new AppConfig
        {
            AutoPaste = AutoPaste,
            UseClipboardFallback = UseClipboardFallback,
            Hotkey = Hotkey.Clone(),
            TranscriberHotkey = TranscriberHotkey.Clone(),
            StreamingTranscriberHotkey = StreamingTranscriberHotkey.Clone(),
            Llm = Llm.Clone(),
            Transcriber = Transcriber.Clone(),
        };
    }
}

public sealed class HotkeyConfig
{
    public uint Modifiers { get; set; } = 0x0003; // CTRL + ALT
    public uint Key { get; set; } = (uint)Keys.R;

    public HotkeyConfig Clone()
    {
        return new HotkeyConfig { Modifiers = Modifiers, Key = Key };
    }
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
        set =>
            ApiKeyEncrypted = string.IsNullOrWhiteSpace(value) ? null : Dpapi.Protect(value.Trim());
    }

    public LlmConfig Clone()
    {
        return new LlmConfig
        {
            Enabled = Enabled,
            BaseUrl = BaseUrl,
            Model = Model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            ApiKeyEncrypted = ApiKeyEncrypted,
            HttpReferer = HttpReferer,
            XTitle = XTitle,
        };
    }
}

public sealed class TranscriberConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://localhost:18000/v1";
    public string Model { get; set; } = "glm-nano-2512";
    public string? ApiKeyEncrypted { get; set; } = null;
    public int TimeoutSeconds { get; set; } = 30;
    public bool AutoPaste { get; set; } = true;
    public bool EnableVAD { get; set; } = true;
    public int SilenceThresholdMs { get; set; } = 2000;
    public int PreferredMicrophoneIndex { get; set; } = -1;
    public bool StreamResults { get; set; } = false;

    // VAD sensitivity thresholds (RMS values for 16-bit audio)
    // Higher values = less sensitive (requires louder speech to trigger)
    public int VadActivationThreshold { get; set; } = 900; // Threshold to START detecting speech
    public int VadSustainThreshold { get; set; } = 550; // Threshold to CONTINUE detecting speech
    public int VadSilenceThreshold { get; set; } = 120; // Threshold for standard recording silence detection

    // WebRTC VAD (smarter ML-based speech detection)
    public bool UseWebRtcVad { get; set; } = true; // Use WebRTC VAD instead of RMS thresholds
    public int WebRtcVadSensitivity { get; set; } = 2; // 0=Low, 1=Medium, 2=High, 3=VeryHigh

    // Auto-enhancement for longer transcriptions
    public bool EnableAutoEnhance { get; set; } = true; // Auto-enhance long transcriptions with LLM
    public int AutoEnhanceThresholdChars { get; set; } = 100; // Minimum chars to trigger enhancement

    [JsonIgnore]
    public string? ApiKey
    {
        get => string.IsNullOrEmpty(ApiKeyEncrypted) ? null : Dpapi.Unprotect(ApiKeyEncrypted);
        set =>
            ApiKeyEncrypted = string.IsNullOrWhiteSpace(value) ? null : Dpapi.Protect(value.Trim());
    }

    [JsonIgnore]
    public string WebSocketUrl
    {
        get
        {
            var builder = CreateBaseUriBuilder(forWebSocket: true);
            builder.Path = BuildTranscriptionPath(builder.Path, includeStream: true);
            return builder.ToString();
        }
    }

    [JsonIgnore]
    public Uri TranscriptionEndpoint
    {
        get
        {
            var builder = CreateBaseUriBuilder();
            builder.Path = BuildTranscriptionPath(builder.Path, includeStream: false);
            return builder.Uri;
        }
    }

    private UriBuilder CreateBaseUriBuilder(bool forWebSocket = false)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
        {
            baseUri = new Uri("http://localhost:18000/v1");
        }

        var builder = new UriBuilder(baseUri);
        if (forWebSocket)
        {
            builder.Scheme = builder.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";
        }

        return builder;
    }

    private static string BuildTranscriptionPath(string basePath, bool includeStream)
    {
        var path = string.IsNullOrWhiteSpace(basePath) ? "/v1" : basePath;
        path = path.TrimEnd('/');

        if (!path.StartsWith("/"))
        {
            path = "/" + path;
        }

        if (path.EndsWith("/audio/transcriptions/stream", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/stream".Length];
        }

        if (!path.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
        {
            path += "/audio/transcriptions";
        }

        if (includeStream)
        {
            path += "/stream";
        }

        return path;
    }

    public TranscriberConfig Clone()
    {
        return new TranscriberConfig
        {
            Enabled = Enabled,
            BaseUrl = BaseUrl,
            Model = Model,
            ApiKeyEncrypted = ApiKeyEncrypted,
            TimeoutSeconds = TimeoutSeconds,
            AutoPaste = AutoPaste,
            EnableVAD = EnableVAD,
            SilenceThresholdMs = SilenceThresholdMs,
            PreferredMicrophoneIndex = PreferredMicrophoneIndex,
            StreamResults = StreamResults,
            VadActivationThreshold = VadActivationThreshold,
            VadSustainThreshold = VadSustainThreshold,
            VadSilenceThreshold = VadSilenceThreshold,
            UseWebRtcVad = UseWebRtcVad,
            WebRtcVadSensitivity = WebRtcVadSensitivity,
            EnableAutoEnhance = EnableAutoEnhance,
            AutoEnhanceThresholdChars = AutoEnhanceThresholdChars,
        };
    }
}

public sealed class ConfigService : IConfigService, IDisposable
{
    private static string Dir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TailSlap"
        );
    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = TailSlapJsonContext.Default,
    };

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private readonly object _debounceLock = new();
    public event Action? ConfigChanged;
    private DateTime _lastRead = DateTime.MinValue;
    private bool _disposed;

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
            _watcher.Renamed += OnFileRenamed;
            _watcher.Deleted += OnFileChanged;
            _watcher.EnableRaisingEvents = true;

            _debounceTimer = new System.Threading.Timer(
                _ =>
                {
                    try
                    {
                        ConfigChanged?.Invoke();
                    }
                    catch { }
                },
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
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
        try
        {
            var timer = _debounceTimer;
            if (timer == null)
            {
                ConfigChanged?.Invoke();
                return;
            }

            lock (_debounceLock)
            {
                timer.Change(500, Timeout.Infinite);
            }
        }
        catch
        {
            ConfigChanged?.Invoke();
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        OnFileChanged(sender, e);
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
            var cfg = JsonSerializer.Deserialize(txt, TailSlapJsonContext.Default.AppConfig);
            if (cfg == null)
            {
                Logger.Log("Config deserialization returned null. Using defaults.");
                return new AppConfig();
            }
            Logger.Log($"Config loaded successfully from {FilePath}");
            return cfg;
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
            _lastRead = DateTime.Now;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, WriteOptions));
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
            cfg.Transcriber.SilenceThresholdMs = 2000;
        }

        // Default transcriber hotkey to Ctrl+Alt+T
        if (cfg.TranscriberHotkey.Modifiers == 0 && cfg.TranscriberHotkey.Key == 0)
        {
            cfg.TranscriberHotkey.Modifiers = 0x0003; // CTRL + ALT
            cfg.TranscriberHotkey.Key = (uint)Keys.T;
        }

        return cfg;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_watcher != null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                }
                catch { }

                try
                {
                    _watcher.Changed -= OnFileChanged;
                    _watcher.Created -= OnFileChanged;
                    _watcher.Renamed -= OnFileRenamed;
                    _watcher.Deleted -= OnFileChanged;
                }
                catch { }

                try
                {
                    _watcher.Dispose();
                }
                catch { }

                _watcher = null;
            }

            if (_debounceTimer != null)
            {
                try
                {
                    _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _debounceTimer.Dispose();
                }
                catch { }

                _debounceTimer = null;
            }
        }
        catch { }
    }
}
