using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TailSlap;

public enum StreamingState
{
    Idle,
    Starting,
    Streaming,
    Stopping,
}

public class MainForm : Form
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _animTimer;
    private int _frame = 0;
    private Icon[] _frames;
    private Icon _idleIcon;
    private long _lastPulseUpdateMs;
    private int _pulseDots;
    private const int AnimationIntervalMs = 75;
    private const int RecordingAnimIntervalMs = 50;
    private const int TranscribingAnimIntervalMs = 200;
    private const int TooltipPulseIntervalMs = 300;
    private const int TooltipPulseMaxDots = 3;
    private const int WM_HOTKEY = 0x0312;
    private const int REFINEMENT_HOTKEY_ID = 1;
    private const int TRANSCRIBER_HOTKEY_ID = 2;
    private const int STREAMING_TRANSCRIBER_HOTKEY_ID = 3;

    private readonly IConfigService _config;
    private readonly IClipboardService _clip;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;
    private readonly IHistoryService _history;
    private readonly IRefinementController _refinementController;
    private readonly ITypelessController _typelessController;
    private readonly ITranscriptionController _transcriptionController;
    private readonly KeyboardHook _keyboardHook;
    private readonly IRealtimeTranscriptionController _realtimeTranscriptionController;
    private readonly IAutoStartService _autoStartService;
    private readonly IHttpClientFactory _httpClientFactory;

    private uint _currentMods;
    private uint _currentVk;
    private uint _transcriberMods;
    private uint _transcriberVk;
    private uint _streamingTranscriberMods;
    private uint _streamingTranscriberVk;
    private AppConfig _currentConfig;
    private bool _isSettingsOpen;

    private ToolStripMenuItem? _llmToggleItem;
    private ToolStripMenuItem? _transcriberToggleItem;
    private readonly bool _allowVisible = false; // intentionally always false for tray-only app
    private RecordingOverlayForm? _recordingOverlay;

    public MainForm(
        IConfigService config,
        IClipboardService clip,
        ITextRefinerFactory textRefinerFactory,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IHistoryService history,
        IRefinementController refinementController,
        ITypelessController typelessController,
        ITranscriptionController transcriptionController,
        KeyboardHook keyboardHook,
        IRealtimeTranscriptionController realtimeTranscriptionController,
        IAutoStartService autoStartService,
        IHttpClientFactory httpClientFactory
    )
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _textRefinerFactory =
            textRefinerFactory ?? throw new ArgumentNullException(nameof(textRefinerFactory));
        _remoteTranscriberFactory =
            remoteTranscriberFactory
            ?? throw new ArgumentNullException(nameof(remoteTranscriberFactory));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _refinementController =
            refinementController ?? throw new ArgumentNullException(nameof(refinementController));
        _typelessController =
            typelessController ?? throw new ArgumentNullException(nameof(typelessController));
        _transcriptionController =
            transcriptionController
            ?? throw new ArgumentNullException(nameof(transcriptionController));
        _keyboardHook = keyboardHook ?? throw new ArgumentNullException(nameof(keyboardHook));
        _realtimeTranscriptionController =
            realtimeTranscriptionController
            ?? throw new ArgumentNullException(nameof(realtimeTranscriptionController));
        _autoStartService =
            autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _httpClientFactory =
            httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true;
        UpdateStyles();

        // Tray-only: hide from taskbar, minimize, zero size, off-screen
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Visible = false;
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(0, 0);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-10000, -10000);
        Opacity = 0;

        _currentConfig = _config.CreateValidatedCopy();

        // Wire up controller events for animation
        _refinementController.OnStarted += () => RunOnUiThread(StartRefinementAnim);
        _refinementController.OnCompleted += () => RunOnUiThread(StopAnim);
        _typelessController.OnStarted += () => RunOnUiThread(StartTypelessAnim);
        _typelessController.OnProcessingStarted += () => RunOnUiThread(SwitchToTranscribingAnim);
        _typelessController.OnCompleted += () => RunOnUiThread(StopAnim);
        _typelessController.OnRmsLevel += rms =>
        {
            RunOnUiThread(() =>
            {
                try
                {
                    _recordingOverlay?.UpdateRms(rms);
                }
                catch { }
            });
        };
        _transcriptionController.OnStarted += () => RunOnUiThread(StartTranscriptionAnim);
        _transcriptionController.OnProcessingStarted += () =>
            RunOnUiThread(SwitchToTranscribingAnim);
        _transcriptionController.OnCompleted += () => RunOnUiThread(StopAnim);
        _transcriptionController.OnRmsLevel += rms =>
        {
            RunOnUiThread(() =>
            {
                try
                {
                    _recordingOverlay?.UpdateRms(rms);
                }
                catch { }
            });
        };
        _realtimeTranscriptionController.OnStarted += () => RunOnUiThread(StartStreamingAnim);
        _realtimeTranscriptionController.OnStopped += () => RunOnUiThread(StopAnim);
        _realtimeTranscriptionController.OnRmsLevel += rms =>
        {
            RunOnUiThread(() =>
            {
                try
                {
                    _recordingOverlay?.UpdateRms(rms);
                }
                catch { }
            });
        };
        _realtimeTranscriptionController.OnTranscription += (text, isFinal) =>
        {
            RunOnUiThread(() =>
            {
                try
                {
                    _recordingOverlay?.UpdateTranscriptionText(text);
                }
                catch { }
            });
        };

        // Wire keyboard hook events to TypelessController.
        // The hook callbacks run on the UI thread, so HandleKeyDownAsync is invoked
        // synchronously: it is non-blocking (recording runs on a background task) and
        // this guarantees the key-down's state transition is observed before any
        // subsequent key-up is processed — otherwise a fast tap could start a
        // recording whose key-up already passed ("push-to-talk doesn't stop").
        _keyboardHook.OnKeyDown += () =>
        {
            try
            {
                _typelessController.HandleKeyDownAsync();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning($"TypelessController key-down dispatch failed: {ex.Message}");
                }
                catch { }
            }
        };
        _keyboardHook.OnKeyUp += () =>
        {
            _ = Task.Run(() => SafeFireAndForget(_typelessController.HandleKeyUpAsync()));
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Refine Now", null, (_, __) => TriggerRefine());
        _menu.Items.Add("Transcribe Now", null, (_, __) => TriggerTranscribe());
        _menu.Items.Add(new ToolStripSeparator());

        // Quick toggles
        _llmToggleItem = new ToolStripMenuItem("Enable LLM Refinement")
        {
            Checked = _currentConfig.Llm.Enabled,
            CheckOnClick = true,
        };
        _llmToggleItem.Click += (_, __) =>
        {
            _currentConfig.Llm.Enabled = _llmToggleItem.Checked;
            _config.Save(_currentConfig);
            NotificationService.ShowInfo(
                _llmToggleItem.Checked ? "LLM refinement enabled." : "LLM refinement disabled."
            );
        };
        _menu.Items.Add(_llmToggleItem);

        _transcriberToggleItem = new ToolStripMenuItem("Enable Transcription")
        {
            Checked = _currentConfig.Transcriber.Enabled,
            CheckOnClick = true,
        };
        _transcriberToggleItem.Click += (_, __) =>
        {
            _currentConfig.Transcriber.Enabled = _transcriberToggleItem.Checked;
            _config.Save(_currentConfig);
            ApplyAllHotkeyRegistrations();
            NotificationService.ShowInfo(
                _transcriberToggleItem.Checked
                    ? "Transcription enabled."
                    : "Transcription disabled."
            );
        };
        _menu.Items.Add(_transcriberToggleItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Run Diagnostics...", null, async (_, __) => await RunDiagnosticsAsync());
        _menu.Items.Add("Recent Errors & Warnings...", null, (_, __) => ShowRecentIssues());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings...", null, (_, __) => ShowSettings(_currentConfig));
        _menu.Items.Add(
            "Open Logs...",
            null,
            (_, __) =>
            {
                try
                {
                    var logPath = Logger.GetLogPath();
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                }
                catch
                {
                    NotificationService.ShowError("Failed to open logs.");
                }
            }
        );
        _menu.Items.Add(
            "Encrypted Refinement History...",
            null,
            (_, __) =>
            {
                try
                {
                    using var hf = new HistoryForm(_history);
                    hf.ShowDialog();
                }
                catch
                {
                    NotificationService.ShowError("Failed to open history.");
                }
            }
        );
        _menu.Items.Add(
            "Encrypted Transcription History...",
            null,
            (_, __) =>
            {
                try
                {
                    using var hf = new TranscriptionHistoryForm(_history);
                    hf.ShowDialog();
                }
                catch
                {
                    NotificationService.ShowError("Failed to open transcription history.");
                }
            }
        );
        var autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _autoStartService.IsEnabled("TailSlap"),
        };
        autoStartItem.Click += (_, __) =>
        {
            _autoStartService.Toggle("TailSlap");
            autoStartItem.Checked = _autoStartService.IsEnabled("TailSlap");
        };
        _menu.Items.Add(autoStartItem);
        _menu.Items.Add(
            "Quit",
            null,
            (_, __) =>
            {
                Application.Exit();
            }
        );

        _idleIcon = LoadIdleIcon();
        _frames = LoadAnimationFrames();

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Visible = true,
            Text = "TailSlap",
        };
        _tray.ContextMenuStrip = _menu;

        NotificationService.Initialize(_tray);

        _animTimer = new System.Windows.Forms.Timer { Interval = AnimationIntervalMs };
        _animTimer.Tick += (_, __) =>
        {
            try
            {
                if (_frames.Length == 0)
                    return;

                int currentFrame = _frame % _frames.Length;
                _tray.Icon = _frames[currentFrame];
                _frame++;
                PulseProcessingTrayText();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning($"Animation tick error: {ex.Message}");
                }
                catch { }
            }
        };

        _currentMods = _currentConfig.Hotkey.Modifiers;
        _currentVk = _currentConfig.Hotkey.Key;
        _transcriberMods = _currentConfig.TranscriberHotkey.Modifiers;
        _transcriberVk = _currentConfig.TranscriberHotkey.Key;
        _streamingTranscriberMods = _currentConfig.StreamingTranscriberHotkey.Modifiers;
        _streamingTranscriberVk = _currentConfig.StreamingTranscriberHotkey.Key;
        Logger.Log(
            $"MainForm initialized. Refinement hotkey mods={_currentMods}, key={_currentVk}. Transcriber hotkey mods={_transcriberMods}, key={_transcriberVk}. Typeless hotkey mods={_currentConfig.TypelessHotkey.Modifiers}, key={_currentConfig.TypelessHotkey.Key}. Streaming hotkey mods={_streamingTranscriberMods}, key={_streamingTranscriberVk}"
        );

        _config.ConfigChanged += () =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ReloadConfigFromDisk));
            }
            else
            {
                ReloadConfigFromDisk();
            }
        };
    }

    private void ShowRecentIssues()
    {
        try
        {
            // Make sure any in-memory log entries are written before reading.
            Logger.Flush();
            var issues = Logger.ReadRecentIssues(maxEntries: 200);
            using var form = new RecentIssuesForm(issues);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Failed to open recent issues: {ex.GetType().Name}: {ex.Message}");
            NotificationService.ShowError($"Could not read the log: {ex.Message}");
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        var runAt = DateTime.Now;
        var rows = new List<DiagnosticRow>();
        var results = new StringBuilder();
        results.AppendLine("TailSlap Diagnostics");
        results.AppendLine("====================");
        results.AppendLine();

        // Check LLM endpoint
        results.AppendLine("LLM Endpoint:");
        string llmUrl = _currentConfig.Llm.BaseUrl.TrimEnd('/');
        string llmStatus = "";
        DiagnosticSeverity llmSeverity = DiagnosticSeverity.Info;
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            using var request = DiagnosticProbe.CreateGetRequest(
                llmUrl + "/models",
                _currentConfig.Llm.ApiKey
            );
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead
            );
            var outcome = DiagnosticProbe.ClassifyHttpStatus(
                (int)response.StatusCode,
                postOnlyEndpoint: false,
                apiKeyConfigured: !string.IsNullOrWhiteSpace(_currentConfig.Llm.ApiKey)
            );
            llmStatus = outcome.Status;
            llmSeverity = outcome.Severity;
            results.AppendLine($"  URL: {llmUrl}");
            results.AppendLine($"  Status: {FormatDiagnosticStatus(outcome)}");
        }
        catch (Exception ex)
        {
            llmStatus = $"Unreachable ({ex.GetType().Name})";
            llmSeverity = DiagnosticSeverity.Error;
            results.AppendLine($"  URL: {_currentConfig.Llm.BaseUrl}");
            results.AppendLine($"  Status: ✗ Unreachable ({ex.GetType().Name})");
        }
        rows.Add(
            new DiagnosticRow
            {
                Section = "LLM Endpoint",
                Label = "URL",
                Value = llmUrl,
                Monospace = true,
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "LLM Endpoint",
                Label = "Status",
                Status = llmStatus,
                Severity = llmSeverity,
            }
        );
        results.AppendLine();

        // Check Transcriber endpoint
        results.AppendLine("Transcription Endpoint:");
        string transcriberUrl = _currentConfig.Transcriber.TranscriptionEndpoint.ToString();
        string transcriberStatus = "";
        DiagnosticSeverity transcriberSeverity = DiagnosticSeverity.Info;
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            using var request = DiagnosticProbe.CreateGetRequest(
                transcriberUrl,
                _currentConfig.Transcriber.ApiKey
            );
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead
            );
            var outcome = DiagnosticProbe.ClassifyHttpStatus(
                (int)response.StatusCode,
                postOnlyEndpoint: true,
                apiKeyConfigured: !string.IsNullOrWhiteSpace(_currentConfig.Transcriber.ApiKey)
            );
            transcriberStatus = outcome.Status;
            transcriberSeverity = outcome.Severity;
            results.AppendLine($"  URL: {transcriberUrl}");
            results.AppendLine($"  Status: {FormatDiagnosticStatus(outcome)}");
        }
        catch (Exception ex)
        {
            transcriberStatus = $"Unreachable ({ex.GetType().Name})";
            transcriberSeverity = DiagnosticSeverity.Error;
            results.AppendLine($"  URL: {_currentConfig.Transcriber.BaseUrl}");
            results.AppendLine($"  Status: ✗ Unreachable ({ex.GetType().Name})");
        }
        rows.Add(
            new DiagnosticRow
            {
                Section = "Transcription Endpoint",
                Label = "URL",
                Value = transcriberUrl,
                Monospace = true,
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Transcription Endpoint",
                Label = "Status",
                Status = transcriberStatus,
                Severity = transcriberSeverity,
            }
        );
        results.AppendLine();

        // Check the optional realtime WebSocket endpoint separately from HTTP transcription.
        string realtimeProvider = string.IsNullOrWhiteSpace(
            _currentConfig.Transcriber.RealtimeProvider
        )
            ? "unknown"
            : _currentConfig.Transcriber.RealtimeProvider;
        string wsSection = $"Realtime WebSocket ({realtimeProvider})";
        results.AppendLine($"{wsSection}:");
        string wsUrl = _currentConfig.Transcriber.WebSocketUrl;
        string wsStatus = "";
        DiagnosticSeverity wsSeverity = DiagnosticSeverity.Info;
        results.AppendLine($"  URL: {wsUrl}");
        if (!_currentConfig.Transcriber.Enabled)
        {
            wsStatus = "Not tested (transcription disabled)";
            results.AppendLine($"  Status: {wsStatus}");
        }
        else
        {
            try
            {
                using var ws = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(_currentConfig.Transcriber.ApiKey))
                {
                    ws.Options.SetRequestHeader(
                        "Authorization",
                        $"Bearer {_currentConfig.Transcriber.ApiKey}"
                    );
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
                wsStatus = "Connectable";
                wsSeverity = DiagnosticSeverity.Success;
                results.AppendLine("  Status: ✓ Connectable");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (Exception ex)
            {
                wsStatus = $"Unavailable ({ex.GetType().Name})";
                wsSeverity = DiagnosticSeverity.Warning;
                results.AppendLine($"  Status: ⚠ {wsStatus} (realtime mode only)");
            }
        }
        rows.Add(
            new DiagnosticRow
            {
                Section = wsSection,
                Label = "URL",
                Value = wsUrl,
                Monospace = true,
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = wsSection,
                Label = "Status",
                Status = wsStatus,
                Severity = wsSeverity,
            }
        );
        results.AppendLine();

        // Check microphone
        results.AppendLine("Microphone:");
        int deviceCount = 0;
        string micStatus = "";
        DiagnosticSeverity micSeverity = DiagnosticSeverity.Info;
        try
        {
            deviceCount = AudioRecorder.GetDeviceCount();
            results.AppendLine($"  Devices found: {deviceCount}");
            if (deviceCount > 0)
            {
                micStatus = "Available";
                micSeverity = DiagnosticSeverity.Success;
                results.AppendLine("  Status: ✓ Available");
            }
            else
            {
                micStatus = "No microphones found";
                micSeverity = DiagnosticSeverity.Error;
                results.AppendLine("  Status: ✗ No microphones found");
            }
        }
        catch (Exception ex)
        {
            micStatus = $"Error checking ({ex.GetType().Name})";
            micSeverity = DiagnosticSeverity.Error;
            results.AppendLine($"  Status: ✗ Error checking ({ex.GetType().Name})");
        }
        rows.Add(
            new DiagnosticRow
            {
                Section = "Microphone",
                Label = "Devices found",
                Value = deviceCount.ToString(),
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Microphone",
                Label = "Status",
                Status = micStatus,
                Severity = micSeverity,
            }
        );
        if (_currentConfig.Transcriber.PreferredMicrophoneIndex >= 0)
        {
            rows.Add(
                new DiagnosticRow
                {
                    Section = "Microphone",
                    Label = "Preferred device index",
                    Value = _currentConfig.Transcriber.PreferredMicrophoneIndex.ToString(),
                }
            );
            results.AppendLine(
                $"  Preferred device index: {_currentConfig.Transcriber.PreferredMicrophoneIndex}"
            );
        }
        results.AppendLine();

        // Configuration summary
        results.AppendLine("Configuration:");
        results.AppendLine($"  LLM Enabled: {(_currentConfig.Llm.Enabled ? "Yes" : "No")}");
        results.AppendLine($"  LLM Model: {_currentConfig.Llm.Model}");
        results.AppendLine(
            $"  Transcription Enabled: {(_currentConfig.Transcriber.Enabled ? "Yes" : "No")}"
        );
        results.AppendLine($"  Transcription Model: {_currentConfig.Transcriber.Model}");
        results.AppendLine(
            $"  VAD Enabled: {(_currentConfig.Transcriber.EnableVAD ? "Yes" : "No")}"
        );
        results.AppendLine(
            $"  Toggle HTTP streaming: {(_currentConfig.Transcriber.StreamResults ? "Yes" : "No")}"
        );
        results.AppendLine($"  Realtime provider: {realtimeProvider}");
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "LLM Enabled",
                Value = _currentConfig.Llm.Enabled ? "Yes" : "No",
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "LLM Model",
                Value = _currentConfig.Llm.Model,
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "Transcription Enabled",
                Value = _currentConfig.Transcriber.Enabled ? "Yes" : "No",
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "Transcription Model",
                Value = _currentConfig.Transcriber.Model,
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "VAD Enabled",
                Value = _currentConfig.Transcriber.EnableVAD ? "Yes" : "No",
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "Toggle HTTP streaming",
                Value = _currentConfig.Transcriber.StreamResults ? "Yes" : "No",
            }
        );
        rows.Add(
            new DiagnosticRow
            {
                Section = "Configuration",
                Label = "Realtime provider",
                Value = realtimeProvider,
            }
        );

        try
        {
            using var form = new DiagnosticsForm(rows, runAt);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                $"Failed to open diagnostics form: {ex.GetType().Name}: {ex.Message}"
            );
            BrandedMessageBox.Show(
                results.ToString(),
                "TailSlap Diagnostics",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        Logger.Log("Diagnostics run:\n" + results.ToString());
    }

    private static string FormatDiagnosticStatus(DiagnosticHttpResult outcome)
    {
        return outcome.Severity switch
        {
            DiagnosticSeverity.Success => "✓ " + outcome.Status,
            DiagnosticSeverity.Warning => "⚠ " + outcome.Status,
            DiagnosticSeverity.Error => "✗ " + outcome.Status,
            _ => outcome.Status,
        };
    }

    private Icon[] LoadAnimationFrames()
    {
        var list = new System.Collections.Generic.List<Icon>(8);
        int preferredSize = GetOptimalIconSize();

        try
        {
            for (int i = 1; i <= 8; i++)
            {
                var icon = TryLoadEmbeddedPngAsIcon(
                    $"{i}.png",
                    preferredSize,
                    cropToContent: false
                );
                if (icon != null)
                    list.Add(icon);
            }
        }
        catch { }

        if (list.Count > 0)
        {
            Logger.Log(
                $"Loaded {list.Count} animation frames from embedded resources at {preferredSize}px"
            );
            return list.ToArray();
        }

        Logger.LogWarning("Animation frames unavailable; using the idle icon");
        return new[] { _idleIcon };
    }

    private static Stream? TryOpenEmbeddedIconsResourceStream(string fileName)
    {
        try
        {
            var assembly = typeof(MainForm).Assembly;
            string suffix = $".Icons.{fileName}";

            string? assemblyName = assembly.GetName().Name;
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                var direct = assembly.GetManifestResourceStream($"{assemblyName}{suffix}");
                if (direct != null)
                    return direct;
            }

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (resourceName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return assembly.GetManifestResourceStream(resourceName);
            }
        }
        catch { }

        return null;
    }

    private static Icon? TryLoadEmbeddedPngAsIcon(
        string fileName,
        int preferredSize,
        bool cropToContent = true
    )
    {
        try
        {
            using var stream = TryOpenEmbeddedIconsResourceStream(fileName);
            if (stream == null)
                return null;

            return TrayIconRenderer.FromPngStream(stream, preferredSize, cropToContent);
        }
        catch
        {
            return null;
        }
    }

    private Icon LoadIdleIcon()
    {
        int preferredSize = GetOptimalIconSize();
        try
        {
            var logo = TryLoadEmbeddedPngAsIcon("icon.png", preferredSize);
            if (logo != null)
            {
                Logger.Log($"Loaded idle icon at {preferredSize}px from embedded icon.png");
                return logo;
            }
        }
        catch { }

        Logger.LogWarning("Embedded idle icon unavailable; using the system application icon");
        return SystemIcons.Application;
    }

    public static Icon LoadMainIcon()
    {
        int preferredSize = GetOptimalIconSize();
        try
        {
            var logo = TryLoadEmbeddedPngAsIcon("icon.png", preferredSize);
            if (logo != null)
            {
                Logger.Log($"Loaded main icon at {preferredSize}px from embedded icon.png");
                return logo;
            }
        }
        catch { }

        Logger.LogWarning("Embedded main icon unavailable; using the system application icon");
        return SystemIcons.Application;
    }

    private static int GetOptimalIconSize()
    {
        try
        {
            var s = SystemInformation.SmallIconSize;
            int size = Math.Max(s.Width, s.Height);
            if (size >= 16 && size <= 64)
                return size;

            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            float dpiX = graphics.DpiX;
            float scaleFactor = dpiX / 96.0f;
            int scaledSize = (int)Math.Round(16.0f * scaleFactor);
            scaledSize = Math.Max(16, Math.Min(64, scaledSize));
            if (scaledSize % 2 != 0)
                scaledSize++;
            return scaledSize;
        }
        catch
        {
            return 16;
        }
    }

    private void PulseProcessingTrayText()
    {
        long nowMs = Environment.TickCount64;
        if (_lastPulseUpdateMs != 0 && nowMs - _lastPulseUpdateMs < TooltipPulseIntervalMs)
            return;

        _pulseDots = (_pulseDots + 1) % (TooltipPulseMaxDots + 1);
        string dots = _pulseDots == 0 ? "" : new string('.', _pulseDots);

        string stateText;
        if (_typelessController.IsRecording)
            stateText = "Recording";
        else if (_transcriptionController.IsRecording)
            stateText = "Recording";
        else if (_refinementController.IsRefining)
            stateText = "Refining";
        else if (_typelessController.IsProcessing)
            stateText = "Transcribing";
        else if (_transcriptionController.IsTranscribing)
            stateText = "Transcribing";
        else if (_realtimeTranscriptionController.IsStreaming)
            stateText = "Streaming";
        else
            stateText = "Processing";

        TrySetTrayText($"TailSlap - {stateText}{dots}");
        _lastPulseUpdateMs = nowMs;
    }

    private void TrySetTrayText(string text)
    {
        try
        {
            _tray.Text = text;
        }
        catch { }
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed || Disposing)
            return;

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void SetVisibleCore(bool value)
    {
        // Prevent the form from becoming visible (tray-only app).
        // Application.Run() tries to show the form; we block that here.
        // We still need to call base once to create the handle for hotkeys.
        if (!_allowVisible && IsHandleCreated)
            return;
        base.SetVisibleCore(false);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TOOLWINDOW: prevents appearing in taskbar and Alt+Tab
            cp.ExStyle |= 0x80;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyAllHotkeyRegistrations();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try
        {
            UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID);
        }
        catch { }
        try
        {
            UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID);
        }
        catch { }
        try
        {
            UnregisterHotKey(Handle, STREAMING_TRANSCRIBER_HOTKEY_ID);
        }
        catch { }
        try
        {
            _keyboardHook?.Dispose();
        }
        catch { }
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            _keyboardHook?.Dispose();
        }
        catch { }
        _tray.Visible = false;
        _tray.Dispose();

        try
        {
            _recordingOverlay?.Dispose();
        }
        catch { }

        foreach (var frame in _frames)
        {
            if (!ReferenceEquals(frame, _idleIcon))
                frame.Dispose();
        }
        _idleIcon.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            if (_isSettingsOpen)
            {
                Logger.Log("WM_HOTKEY ignored because Settings is open");
                return;
            }

            var hotkeyId = m.WParam.ToInt32();
            Logger.Log($"WM_HOTKEY received with ID: {hotkeyId}");

            if (hotkeyId == REFINEMENT_HOTKEY_ID)
            {
                TriggerRefine();
            }
            else if (hotkeyId == TRANSCRIBER_HOTKEY_ID)
            {
                TriggerTranscribe();
            }
            else if (hotkeyId == STREAMING_TRANSCRIBER_HOTKEY_ID)
            {
                TriggerStreamingTranscribe();
            }
        }
        base.WndProc(ref m);
    }

    private static void SafeFireAndForget(Task task)
    {
        task.ContinueWith(
            t => Logger.Error($"Unhandled async error: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted
        );
    }

    private void TriggerRefine()
    {
        SafeFireAndForget(_refinementController.TriggerRefineAsync());
    }

    private void TriggerTranscribe()
    {
        SafeFireAndForget(_transcriptionController.TriggerTranscribeAsync());
    }

    private void TriggerStreamingTranscribe()
    {
        SafeFireAndForget(_realtimeTranscriptionController.TriggerStreamingAsync());
    }

    private void ReloadConfigFromDisk()
    {
        if (_isSettingsOpen)
        {
            Logger.Log(
                "Configuration change detected while Settings is open. Deferring hot-reload."
            );
            return;
        }

        try
        {
            Logger.Log("Detected config file change on disk. Reloading...");
            _currentConfig = _config.CreateValidatedCopy();

            // Update toggle states
            if (_llmToggleItem != null)
                _llmToggleItem.Checked = _currentConfig.Llm.Enabled;
            if (_transcriberToggleItem != null)
                _transcriberToggleItem.Checked = _currentConfig.Transcriber.Enabled;

            ApplyAllHotkeyRegistrations();

            NotificationService.ShowInfo("Configuration reloaded from disk.");
            Logger.Log("Configuration hot-reload complete.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Error during config hot-reload: {ex.Message}");
        }
    }

    private void EnsureOverlay()
    {
        if (_recordingOverlay == null || _recordingOverlay.IsDisposed)
        {
            _recordingOverlay = new RecordingOverlayForm();
        }
    }

    private void StartRefinementAnim()
    {
        try
        {
            Logger.Log("Refinement animation START");
        }
        catch { }
        _frame = 0;
        _lastPulseUpdateMs = 0;
        _pulseDots = 0;
        _animTimer.Interval = AnimationIntervalMs;
        TrySetTrayText("TailSlap - Processing");
        _animTimer.Start();

        try
        {
            EnsureOverlay();
            _recordingOverlay!.ShowOverlay("Refining...", RecordingOverlayForm.OverlayMode.Pulse);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Overlay show error: {ex.Message}");
            }
            catch { }
        }
    }

    private void StartTypelessAnim()
    {
        try
        {
            Logger.Log("Typeless animation START");
        }
        catch { }
        _frame = 0;
        _lastPulseUpdateMs = 0;
        _pulseDots = 0;
        _animTimer.Interval = RecordingAnimIntervalMs;
        TrySetTrayText("TailSlap - Recording");
        _animTimer.Start();

        try
        {
            EnsureOverlay();
            _recordingOverlay!.ShowOverlay(
                "Recording...",
                RecordingOverlayForm.OverlayMode.Waveform
            );
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Recording overlay show error: {ex.Message}");
            }
            catch { }
        }
    }

    private void StartTranscriptionAnim()
    {
        try
        {
            Logger.Log("Transcription animation START");
        }
        catch { }
        _frame = 0;
        _lastPulseUpdateMs = 0;
        _pulseDots = 0;
        _animTimer.Interval = RecordingAnimIntervalMs;
        TrySetTrayText("TailSlap - Recording");
        _animTimer.Start();

        try
        {
            EnsureOverlay();
            _recordingOverlay!.ShowOverlay(
                "Recording...",
                RecordingOverlayForm.OverlayMode.Waveform
            );
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Overlay show error: {ex.Message}");
            }
            catch { }
        }
    }

    private void StartStreamingAnim()
    {
        try
        {
            Logger.Log("Streaming animation START");
        }
        catch { }
        _frame = 0;
        _lastPulseUpdateMs = 0;
        _pulseDots = 0;
        _animTimer.Interval = RecordingAnimIntervalMs;
        TrySetTrayText("TailSlap - Streaming");
        _animTimer.Start();

        try
        {
            EnsureOverlay();
            _recordingOverlay!.ShowOverlay(
                "Streaming...",
                RecordingOverlayForm.OverlayMode.Waveform
            );
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Overlay show error: {ex.Message}");
            }
            catch { }
        }
    }

    private void SwitchToTranscribingAnim()
    {
        try
        {
            Logger.Log("Animation: switching to transcribing");
        }
        catch { }
        _lastPulseUpdateMs = 0;
        _pulseDots = 0;
        _animTimer.Interval = TranscribingAnimIntervalMs;
        TrySetTrayText("TailSlap - Transcribing");

        // Update overlay to transcribing state
        try
        {
            _recordingOverlay?.ShowTranscribing();
        }
        catch { }
    }

    private void StopAnim()
    {
        try
        {
            Logger.Log("Animation STOP");
        }
        catch { }
        _animTimer.Stop();
        _frame = 0;
        _animTimer.Interval = AnimationIntervalMs;
        _tray.Icon = _idleIcon;
        TrySetTrayText("TailSlap");

        // Hide floating recording overlay
        try
        {
            _recordingOverlay?.HideOverlay();
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterHotkey(uint mods, uint vk, int hotkeyId)
    {
        try
        {
            if (Handle != IntPtr.Zero)
                UnregisterHotKey(Handle, hotkeyId);
        }
        catch { }
        // NOTE: do not silently rewrite mods==0/vk==0 here — a zero config is a
        // misconfiguration that must be surfaced, not hidden. Settings validates
        // hotkeys before saving.
        var ok = RegisterHotKey(Handle, hotkeyId, mods, vk);
        var lastError = ok ? 0 : Marshal.GetLastPInvokeError();
        Logger.Log(
            $"RegisterHotKey mods={mods}, key={vk}, id={hotkeyId}, ok={ok}, err={lastError}"
        );
        if (!ok)
        {
            string keyName = ((Keys)vk).ToString();
            string modNames = "";
            if ((mods & 0x0001) != 0)
                modNames += "Alt+";
            if ((mods & 0x0002) != 0)
                modNames += "Ctrl+";
            if ((mods & 0x0004) != 0)
                modNames += "Shift+";
            if ((mods & 0x0008) != 0)
                modNames += "Win+";

            var message =
                lastError == 1409
                    ? $"Failed to register hotkey: {modNames}{keyName}. It is already registered by another application."
                    : $"Failed to register hotkey: {modNames}{keyName}. Windows error {lastError}.";
            Logger.Error(message);
            NotificationService.ShowError(message);
        }
    }

    private void UnregisterAppHotkeys()
    {
        try
        {
            UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID);
        }
        catch { }
        try
        {
            UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID);
        }
        catch { }
        try
        {
            UnregisterHotKey(Handle, STREAMING_TRANSCRIBER_HOTKEY_ID);
        }
        catch { }
    }

    private void SuspendHotkeysForSettings()
    {
        UnregisterAppHotkeys();

        try
        {
            _keyboardHook.Uninstall();
        }
        catch { }
    }

    private void ApplyAllHotkeyRegistrations()
    {
        UnregisterAppHotkeys();

        _currentMods = _currentConfig.Hotkey.Modifiers;
        _currentVk = _currentConfig.Hotkey.Key;
        RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);

        _transcriberMods = _currentConfig.TranscriberHotkey.Modifiers;
        _transcriberVk = _currentConfig.TranscriberHotkey.Key;
        _streamingTranscriberMods = _currentConfig.StreamingTranscriberHotkey.Modifiers;
        _streamingTranscriberVk = _currentConfig.StreamingTranscriberHotkey.Key;

        if (_currentConfig.Transcriber.Enabled)
        {
            // Register toggle transcription hotkey (Ctrl+Alt+T)
            RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);

            // Install/reconfigure keyboard hook for typeless push-to-talk hotkey
            if (!_typelessController.IsRecording && !_typelessController.IsProcessing)
            {
                _keyboardHook.Reconfigure(_currentConfig.TypelessHotkey);
            }

            RegisterHotkey(
                _streamingTranscriberMods,
                _streamingTranscriberVk,
                STREAMING_TRANSCRIBER_HOTKEY_ID
            );
        }
        else
        {
            // Uninstall keyboard hook when transcriber is disabled
            _keyboardHook.Uninstall();
        }
    }

    private void ShowSettings(AppConfig cfg)
    {
        _isSettingsOpen = true;
        SuspendHotkeysForSettings();
        try
        {
            var clone = cfg.Clone();
            using var dlg = new SettingsForm(clone, _textRefinerFactory, _remoteTranscriberFactory);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                Logger.Log(
                    $"Settings OK clicked. LLM hotkey before save: mods={clone.Hotkey.Modifiers}, key={clone.Hotkey.Key}"
                );
                Logger.Log(
                    $"Transcriber hotkey before save: mods={clone.TranscriberHotkey.Modifiers}, key={clone.TranscriberHotkey.Key}"
                );

                _currentConfig = clone;
                _config.Save(_currentConfig);

                // Update toggle states
                if (_llmToggleItem != null)
                    _llmToggleItem.Checked = _currentConfig.Llm.Enabled;
                if (_transcriberToggleItem != null)
                    _transcriberToggleItem.Checked = _currentConfig.Transcriber.Enabled;

                NotificationService.ShowSuccess("Settings saved.");
            }
        }
        finally
        {
            _isSettingsOpen = false;
            ApplyAllHotkeyRegistrations();
        }
    }
}
