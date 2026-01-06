using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    private readonly ITranscriptionController _transcriptionController;
    private readonly IRealtimeTranscriptionController _realtimeTranscriptionController;
    private readonly IAutoStartService _autoStartService;

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

    public MainForm(
        IConfigService config,
        IClipboardService clip,
        ITextRefinerFactory textRefinerFactory,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IHistoryService history,
        IRefinementController refinementController,
        ITranscriptionController transcriptionController,
        IRealtimeTranscriptionController realtimeTranscriptionController,
        IAutoStartService autoStartService
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
        _transcriptionController =
            transcriptionController
            ?? throw new ArgumentNullException(nameof(transcriptionController));
        _realtimeTranscriptionController =
            realtimeTranscriptionController
            ?? throw new ArgumentNullException(nameof(realtimeTranscriptionController));
        _autoStartService =
            autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));

        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true;
        UpdateStyles();

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Visible = false;

        _currentConfig = _config.CreateValidatedCopy();

        // Wire up controller events for animation
        _refinementController.OnStarted += StartAnim;
        _refinementController.OnCompleted += StopAnim;
        _transcriptionController.OnStarted += StartAnim;
        _transcriptionController.OnCompleted += StopAnim;
        _realtimeTranscriptionController.OnStarted += StartAnim;
        _realtimeTranscriptionController.OnStopped += StopAnim;

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

            if (_transcriberToggleItem.Checked)
            {
                RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
                RegisterHotkey(
                    _streamingTranscriberMods,
                    _streamingTranscriberVk,
                    STREAMING_TRANSCRIBER_HOTKEY_ID
                );
            }
            else
            {
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

            NotificationService.ShowInfo(
                _transcriberToggleItem.Checked
                    ? "Transcription enabled."
                    : "Transcription disabled."
            );
        };
        _menu.Items.Add(_transcriberToggleItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Run Diagnostics...", null, async (_, __) => await RunDiagnosticsAsync());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings...", null, (_, __) => ShowSettings(_currentConfig));
        _menu.Items.Add(
            "Open Logs...",
            null,
            (_, __) =>
            {
                try
                {
                    Process.Start(
                        "notepad",
                        Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "TailSlap",
                            "app.log"
                        )
                    );
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
                    Logger.Log($"Animation tick error: {ex.Message}");
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
            $"MainForm initialized. Refinement hotkey mods={_currentMods}, key={_currentVk}. Transcriber hotkey mods={_transcriberMods}, key={_transcriberVk}. Streaming hotkey mods={_streamingTranscriberMods}, key={_streamingTranscriberVk}"
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

    private async Task RunDiagnosticsAsync()
    {
        var results = new StringBuilder();
        results.AppendLine("TailSlap Diagnostics");
        results.AppendLine("====================");
        results.AppendLine();

        // Check LLM endpoint
        results.AppendLine("LLM Endpoint:");
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var llmUrl = _currentConfig.Llm.BaseUrl.TrimEnd('/');
            var response = await httpClient.GetAsync(llmUrl + "/models");
            results.AppendLine($"  URL: {llmUrl}");
            results.AppendLine(
                $"  Status: {(response.IsSuccessStatusCode ? "✓ Reachable" : $"⚠ Response ({(int)response.StatusCode})")}"
            );
        }
        catch (Exception ex)
        {
            results.AppendLine($"  URL: {_currentConfig.Llm.BaseUrl}");
            results.AppendLine($"  Status: ✗ Unreachable ({ex.GetType().Name})");
        }
        results.AppendLine();

        // Check Transcriber endpoint
        results.AppendLine("Transcription Endpoint:");
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var transcriberUrl = _currentConfig.Transcriber.BaseUrl.TrimEnd('/');
            var response = await httpClient.GetAsync(transcriberUrl);
            results.AppendLine($"  URL: {transcriberUrl}");
            results.AppendLine(
                $"  Status: {(response.IsSuccessStatusCode ? "✓ Reachable" : $"⚠ Response ({(int)response.StatusCode})")}"
            );
        }
        catch (Exception ex)
        {
            results.AppendLine($"  URL: {_currentConfig.Transcriber.BaseUrl}");
            results.AppendLine($"  Status: ✗ Unreachable ({ex.GetType().Name})");
        }
        results.AppendLine();

        // Check WebSocket endpoint
        results.AppendLine("WebSocket Endpoint:");
        results.AppendLine($"  URL: {_currentConfig.Transcriber.WebSocketUrl}");
        try
        {
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(_currentConfig.Transcriber.WebSocketUrl), cts.Token);
            results.AppendLine("  Status: ✓ Connectable");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (Exception ex)
        {
            results.AppendLine($"  Status: ✗ Cannot connect ({ex.GetType().Name})");
        }
        results.AppendLine();

        // Check microphone
        results.AppendLine("Microphone:");
        try
        {
            int deviceCount = AudioRecorder.GetDeviceCount();
            results.AppendLine($"  Devices found: {deviceCount}");
            if (deviceCount > 0)
            {
                results.AppendLine("  Status: ✓ Available");
                if (_currentConfig.Transcriber.PreferredMicrophoneIndex >= 0)
                {
                    results.AppendLine(
                        $"  Preferred device index: {_currentConfig.Transcriber.PreferredMicrophoneIndex}"
                    );
                }
            }
            else
            {
                results.AppendLine("  Status: ✗ No microphones found");
            }
        }
        catch (Exception ex)
        {
            results.AppendLine($"  Status: ✗ Error checking ({ex.GetType().Name})");
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
            $"  Streaming Enabled: {(_currentConfig.Transcriber.StreamResults ? "Yes" : "No")}"
        );

        BrandedMessageBox.Show(
            results.ToString(),
            "TailSlap Diagnostics",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
        Logger.Log("Diagnostics run:\n" + results.ToString());
    }

    private Icon[] LoadAnimationFrames()
    {
        var list = new System.Collections.Generic.List<Icon>(8);
        string iconsDir = Path.Combine(Application.StartupPath, "Icons");
        int preferredSize = GetOptimalIconSize();

        try
        {
            for (int i = 1; i <= 8; i++)
            {
                var icon = TryLoadPngAsIcon(Path.Combine(iconsDir, $"{i}.png"), preferredSize);
                if (icon != null)
                    list.Add(icon);
            }
        }
        catch { }

        if (list.Count > 0)
        {
            Logger.Log(
                $"Loaded {list.Count} animation frames (PNG) from files at {preferredSize}px"
            );
            return list.ToArray();
        }

        try
        {
            for (int i = 1; i <= 8; i++)
            {
                var icon = TryLoadEmbeddedPngAsIcon($"{i}.png", preferredSize);
                if (icon != null)
                    list.Add(icon);
            }
        }
        catch { }

        if (list.Count > 0)
        {
            Logger.Log(
                $"Loaded {list.Count} animation frames (PNG) from embedded resources at {preferredSize}px"
            );
            return list.ToArray();
        }

        return new[] { _idleIcon };
    }

    private static Icon? TryLoadIco(string filePath, int preferredSize)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                return new Icon(filePath, preferredSize, preferredSize);
            }
            catch
            {
                return new Icon(filePath);
            }
        }
        catch
        {
            return null;
        }
    }

    private static Icon? TryLoadPngAsIcon(string filePath, int preferredSize)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var bytes = File.ReadAllBytes(filePath);
            using var ms = new MemoryStream(bytes);
            using var original = new Bitmap(ms);
            return CreateIconFromBitmap(original, preferredSize);
        }
        catch
        {
            return null;
        }
    }

    private static Icon? CreateIconFromBitmap(Bitmap original, int preferredSize)
    {
        try
        {
            using var scaled = new Bitmap(
                preferredSize,
                preferredSize,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb
            );
            using (var g = Graphics.FromImage(scaled))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(original, new Rectangle(0, 0, preferredSize, preferredSize));
            }

            MakeEdgeBackgroundTransparent(scaled, tolerance: 40);

            IntPtr hIcon = scaled.GetHicon();
            try
            {
                using var tempIcon = Icon.FromHandle(hIcon);
                return (Icon)tempIcon.Clone();
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                    DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
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

    private static Icon? TryLoadEmbeddedPngAsIcon(string fileName, int preferredSize)
    {
        try
        {
            using var stream = TryOpenEmbeddedIconsResourceStream(fileName);
            if (stream == null)
                return null;

            using var original = new Bitmap(stream);
            return CreateIconFromBitmap(original, preferredSize);
        }
        catch
        {
            return null;
        }
    }

    private static Icon? TryLoadEmbeddedIcoAsIcon(string fileName, int preferredSize)
    {
        try
        {
            using var stream = TryOpenEmbeddedIconsResourceStream(fileName);
            if (stream == null)
                return null;

            try
            {
                return new Icon(stream, preferredSize, preferredSize);
            }
            catch
            {
                return new Icon(stream);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void MakeEdgeBackgroundTransparent(Bitmap bitmap, int tolerance)
    {
        try
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            if (w <= 0 || h <= 0)
                return;

            var bg = GetLikelyBackgroundColor(bitmap);

            var visited = new bool[w * h];
            var q = new System.Collections.Generic.Queue<Point>(w + h);

            void Enqueue(int x, int y)
            {
                int idx = (y * w) + x;
                if (visited[idx])
                    return;
                visited[idx] = true;
                q.Enqueue(new Point(x, y));
            }

            for (int x = 0; x < w; x++)
            {
                Enqueue(x, 0);
                if (h > 1)
                    Enqueue(x, h - 1);
            }
            for (int y = 1; y < h - 1; y++)
            {
                Enqueue(0, y);
                if (w > 1)
                    Enqueue(w - 1, y);
            }

            while (q.Count > 0)
            {
                var p = q.Dequeue();
                var c = bitmap.GetPixel(p.X, p.Y);

                if (!IsSimilarColor(c, bg, tolerance))
                    continue;

                if (c.A != 0)
                    bitmap.SetPixel(p.X, p.Y, Color.FromArgb(0, c.R, c.G, c.B));

                int x = p.X;
                int y = p.Y;
                if (x > 0)
                    Enqueue(x - 1, y);
                if (x < w - 1)
                    Enqueue(x + 1, y);
                if (y > 0)
                    Enqueue(x, y - 1);
                if (y < h - 1)
                    Enqueue(x, y + 1);
            }
        }
        catch { }
    }

    private static Color GetLikelyBackgroundColor(Bitmap bitmap)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        if (w <= 0 || h <= 0)
            return Color.White;

        var c1 = bitmap.GetPixel(0, 0);
        var c2 = bitmap.GetPixel(w - 1, 0);
        var c3 = bitmap.GetPixel(0, h - 1);
        var c4 = bitmap.GetPixel(w - 1, h - 1);

        var best = c1;
        int bestScore = best.R + best.G + best.B;

        int s2 = c2.R + c2.G + c2.B;
        if (s2 > bestScore)
        {
            best = c2;
            bestScore = s2;
        }

        int s3 = c3.R + c3.G + c3.B;
        if (s3 > bestScore)
        {
            best = c3;
            bestScore = s3;
        }

        int s4 = c4.R + c4.G + c4.B;
        if (s4 > bestScore)
        {
            best = c4;
        }

        return best;
    }

    private static bool IsSimilarColor(Color a, Color b, int tolerance)
    {
        int dr = a.R - b.R;
        if (dr < 0)
            dr = -dr;
        if (dr > tolerance)
            return false;

        int dg = a.G - b.G;
        if (dg < 0)
            dg = -dg;
        if (dg > tolerance)
            return false;

        int db = a.B - b.B;
        if (db < 0)
            db = -db;
        return db <= tolerance;
    }

    private Icon LoadIdleIcon()
    {
        try
        {
            string iconsDir = Path.Combine(Application.StartupPath, "Icons");
            int preferredSize = GetOptimalIconSize();

            var frame1 = TryLoadPngAsIcon(Path.Combine(iconsDir, "1.png"), preferredSize);
            if (frame1 != null)
            {
                Logger.Log($"Loaded idle icon at {preferredSize}px from 1.png");
                return frame1;
            }

            var favicon = TryLoadIco(Path.Combine(iconsDir, "favicon.ico"), preferredSize);
            if (favicon != null)
            {
                Logger.Log($"Loaded idle icon at {preferredSize}px from favicon.ico");
                return favicon;
            }

            var embeddedFrame1 = TryLoadEmbeddedPngAsIcon("1.png", preferredSize);
            if (embeddedFrame1 != null)
            {
                Logger.Log($"Loaded idle icon at {preferredSize}px from embedded 1.png");
                return embeddedFrame1;
            }

            var embeddedFavicon = TryLoadEmbeddedIcoAsIcon("favicon.ico", preferredSize);
            if (embeddedFavicon != null)
            {
                Logger.Log($"Loaded idle icon at {preferredSize}px from embedded favicon.ico");
                return embeddedFavicon;
            }
        }
        catch { }

        try
        {
            return (Icon)Properties.Resources.IconIdle.Clone();
        }
        catch { }

        return SystemIcons.Application;
    }

    public static Icon LoadMainIcon()
    {
        try
        {
            string iconsDir = Path.Combine(Application.StartupPath, "Icons");
            int preferredSize = GetOptimalIconSize();

            var frame1 = TryLoadPngAsIcon(Path.Combine(iconsDir, "1.png"), preferredSize);
            if (frame1 != null)
            {
                Logger.Log($"Loaded main icon at {preferredSize}px from 1.png");
                return frame1;
            }

            var favicon = TryLoadIco(Path.Combine(iconsDir, "favicon.ico"), preferredSize);
            if (favicon != null)
            {
                Logger.Log($"Loaded main icon at {preferredSize}px from favicon.ico");
                return favicon;
            }

            var embeddedFrame1 = TryLoadEmbeddedPngAsIcon("1.png", preferredSize);
            if (embeddedFrame1 != null)
            {
                Logger.Log($"Loaded main icon at {preferredSize}px from embedded 1.png");
                return embeddedFrame1;
            }

            var embeddedFavicon = TryLoadEmbeddedIcoAsIcon("favicon.ico", preferredSize);
            if (embeddedFavicon != null)
            {
                Logger.Log($"Loaded main icon at {preferredSize}px from embedded favicon.ico");
                return embeddedFavicon;
            }
        }
        catch { }

        try
        {
            return (Icon)Properties.Resources.IconIdle.Clone();
        }
        catch { }

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
        if (_refinementController.IsRefining)
            stateText = "Refining";
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);
        if (_currentConfig.Transcriber.Enabled)
        {
            RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
            RegisterHotkey(
                _streamingTranscriberMods,
                _streamingTranscriberVk,
                STREAMING_TRANSCRIBER_HOTKEY_ID
            );
        }
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
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
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

    private void TriggerRefine()
    {
        _ = _refinementController.TriggerRefineAsync();
    }

    private void TriggerTranscribe()
    {
        _ = _transcriptionController.TriggerTranscribeAsync();
    }

    private void TriggerStreamingTranscribe()
    {
        _ = _realtimeTranscriptionController.TriggerStreamingAsync();
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
            var newConfig = _config.CreateValidatedCopy();

            bool refinementHotkeyChanged =
                newConfig.Hotkey.Modifiers != _currentMods || newConfig.Hotkey.Key != _currentVk;
            bool transcriberHotkeyChanged =
                newConfig.TranscriberHotkey.Modifiers != _transcriberMods
                || newConfig.TranscriberHotkey.Key != _transcriberVk;
            bool streamingTranscriberHotkeyChanged =
                newConfig.StreamingTranscriberHotkey.Modifiers != _streamingTranscriberMods
                || newConfig.StreamingTranscriberHotkey.Key != _streamingTranscriberVk;
            bool transcriberStatusChanged =
                newConfig.Transcriber.Enabled != _currentConfig.Transcriber.Enabled;

            _currentConfig = newConfig;

            // Update toggle states
            if (_llmToggleItem != null)
                _llmToggleItem.Checked = _currentConfig.Llm.Enabled;
            if (_transcriberToggleItem != null)
                _transcriberToggleItem.Checked = _currentConfig.Transcriber.Enabled;

            if (refinementHotkeyChanged)
            {
                UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID);
                _currentMods = _currentConfig.Hotkey.Modifiers;
                _currentVk = _currentConfig.Hotkey.Key;
                RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);
            }

            if (transcriberHotkeyChanged || transcriberStatusChanged)
            {
                UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID);
                if (_currentConfig.Transcriber.Enabled)
                {
                    _transcriberMods = _currentConfig.TranscriberHotkey.Modifiers;
                    _transcriberVk = _currentConfig.TranscriberHotkey.Key;
                    RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
                }
            }

            if (streamingTranscriberHotkeyChanged || transcriberStatusChanged)
            {
                UnregisterHotKey(Handle, STREAMING_TRANSCRIBER_HOTKEY_ID);
                if (_currentConfig.Transcriber.Enabled)
                {
                    _streamingTranscriberMods = _currentConfig.StreamingTranscriberHotkey.Modifiers;
                    _streamingTranscriberVk = _currentConfig.StreamingTranscriberHotkey.Key;
                    RegisterHotkey(
                        _streamingTranscriberMods,
                        _streamingTranscriberVk,
                        STREAMING_TRANSCRIBER_HOTKEY_ID
                    );
                }
            }

            NotificationService.ShowInfo("Configuration reloaded from disk.");
            Logger.Log("Configuration hot-reload complete.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Error during config hot-reload: {ex.Message}");
        }
    }

    private void StartAnim()
    {
        try
        {
            Logger.Log("Animation START");
        }
        catch { }
        _frame = 0;
        _lastPulseUpdateMs = 0;
        _pulseDots = 0;
        TrySetTrayText("TailSlap - Processing");
        _animTimer.Start();
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
        _tray.Icon = _idleIcon;
        TrySetTrayText("TailSlap");
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private void RegisterHotkey(uint mods, uint vk, int hotkeyId)
    {
        try
        {
            if (Handle != IntPtr.Zero)
                UnregisterHotKey(Handle, hotkeyId);
        }
        catch { }
        if (mods == 0)
            mods = 0x0003;
        if (vk == 0)
            vk = (uint)Keys.R;
        var ok = RegisterHotKey(Handle, hotkeyId, mods, vk);
        Logger.Log($"RegisterHotKey mods={mods}, key={vk}, id={hotkeyId}, ok={ok}");
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

            NotificationService.ShowError(
                $"Failed to register hotkey: {modNames}{keyName}. It may be in use by another application."
            );
        }
    }

    private void ShowSettings(AppConfig cfg)
    {
        _isSettingsOpen = true;
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

                Logger.Log(
                    $"LLM hotkey after reload: mods={_currentConfig.Hotkey.Modifiers}, key={_currentConfig.Hotkey.Key}"
                );
                Logger.Log(
                    $"Transcriber hotkey after reload: mods={_currentConfig.TranscriberHotkey.Modifiers}, key={_currentConfig.TranscriberHotkey.Key}"
                );

                try
                {
                    UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID);
                }
                catch { }
                _currentMods = _currentConfig.Hotkey.Modifiers;
                _currentVk = _currentConfig.Hotkey.Key;
                RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);

                try
                {
                    UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID);
                }
                catch { }
                _transcriberMods = _currentConfig.TranscriberHotkey.Modifiers;
                _transcriberVk = _currentConfig.TranscriberHotkey.Key;
                if (_currentConfig.Transcriber.Enabled)
                {
                    RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
                }

                try
                {
                    UnregisterHotKey(Handle, STREAMING_TRANSCRIBER_HOTKEY_ID);
                }
                catch { }
                _streamingTranscriberMods = _currentConfig.StreamingTranscriberHotkey.Modifiers;
                _streamingTranscriberVk = _currentConfig.StreamingTranscriberHotkey.Key;
                if (_currentConfig.Transcriber.Enabled)
                {
                    RegisterHotkey(
                        _streamingTranscriberMods,
                        _streamingTranscriberVk,
                        STREAMING_TRANSCRIBER_HOTKEY_ID
                    );
                }

                NotificationService.ShowSuccess("Settings saved.");
            }
        }
        finally
        {
            _isSettingsOpen = false;
        }
    }
}
