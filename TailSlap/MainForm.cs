using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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

    private uint _currentMods;
    private uint _currentVk;
    private uint _transcriberMods;
    private uint _transcriberVk;
    private uint _streamingTranscriberMods;
    private uint _streamingTranscriberVk;
    private AppConfig _currentConfig;
    private bool _isRefining;
    private bool _isTranscribing;
    private StreamingState _streamingState = StreamingState.Idle;
    private readonly object _streamingStateLock = new();
    private bool _isSettingsOpen;
    private CancellationTokenSource? _transcriberCts;
    private RealtimeTranscriber? _realtimeTranscriber;
    private AudioRecorder? _realtimeRecorder;
    private string _realtimeTranscriptionText = "";
    private int _lastTypedLength = 0;
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
    private readonly System.Collections.Generic.List<byte> _streamingBuffer = new();
    private const int SEND_BUFFER_SIZE = 16000; // 500ms aggregation for smoother feedback
    private IntPtr _streamingTargetWindow = IntPtr.Zero; // Track foreground window when streaming started
    private int _cleanupInProgress = 0; // Idempotency flag for cleanup
    private DateTime _streamingStartTime = DateTime.MinValue; // Track when streaming started for no-speech timeout
    private const int NO_SPEECH_TIMEOUT_SECONDS = 30; // Auto-stop if no speech detected after this duration

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public MainForm(
        IConfigService config,
        IClipboardService clip,
        ITextRefinerFactory textRefinerFactory,
        IRemoteTranscriberFactory remoteTranscriberFactory,
        IHistoryService history
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

        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true;
        UpdateStyles();

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Visible = false;

        _currentConfig = _config.CreateValidatedCopy();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Refine Now", null, (_, __) => TriggerRefine());
        _menu.Items.Add("Transcribe Now", null, (_, __) => TriggerTranscribe());
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
                        System.IO.Path.Combine(
                            System.Environment.GetFolderPath(
                                System.Environment.SpecialFolder.ApplicationData
                            ),
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
            Checked = AutoStartService.IsEnabled("TailSlap"),
        };
        autoStartItem.Click += (_, __) =>
        {
            AutoStartService.Toggle("TailSlap");
            autoStartItem.Checked = AutoStartService.IsEnabled("TailSlap");
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
        _frames = LoadAnimationFrames(); // Icons are preloaded here to avoid per-frame allocations during animation

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Visible = true,
            Text = "TailSlap",
        };
        _tray.ContextMenuStrip = _menu;

        // Initialize notification service
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

        // Animation is managed by RefineSelectionAsync (StartAnim on start, StopAnim in finally)
        // so we don't need to subscribe to CaptureStarted/CaptureEnded events

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

    private Icon[] LoadAnimationFrames()
    {
        var list = new System.Collections.Generic.List<Icon>(8);
        string iconsDir = System.IO.Path.Combine(Application.StartupPath, "Icons");
        int preferredSize = GetOptimalIconSize();

        try
        {
            for (int i = 1; i <= 8; i++)
            {
                var icon = TryLoadPngAsIcon(
                    System.IO.Path.Combine(iconsDir, $"{i}.png"),
                    preferredSize
                );
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
            if (!System.IO.File.Exists(filePath))
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
            if (!System.IO.File.Exists(filePath))
                return null;

            var bytes = System.IO.File.ReadAllBytes(filePath);
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
            string iconsDir = System.IO.Path.Combine(Application.StartupPath, "Icons");
            int preferredSize = GetOptimalIconSize();

            var frame1 = TryLoadPngAsIcon(System.IO.Path.Combine(iconsDir, "1.png"), preferredSize);
            if (frame1 != null)
            {
                Logger.Log($"Loaded idle icon at {preferredSize}px from 1.png");
                return frame1;
            }

            var favicon = TryLoadIco(
                System.IO.Path.Combine(iconsDir, "favicon.ico"),
                preferredSize
            );
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
        return SystemIcons.Application;
    }

    public static Icon LoadMainIcon()
    {
        try
        {
            string iconsDir = System.IO.Path.Combine(Application.StartupPath, "Icons");
            int preferredSize = GetOptimalIconSize();

            var frame1 = TryLoadPngAsIcon(System.IO.Path.Combine(iconsDir, "1.png"), preferredSize);
            if (frame1 != null)
            {
                Logger.Log($"Loaded main icon at {preferredSize}px from 1.png");
                return frame1;
            }

            var favicon = TryLoadIco(
                System.IO.Path.Combine(iconsDir, "favicon.ico"),
                preferredSize
            );
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
            return 16; // Fallback to standard size
        }
    }

    private void PulseProcessingTrayText()
    {
        long nowMs = Environment.TickCount64;
        if (_lastPulseUpdateMs != 0 && nowMs - _lastPulseUpdateMs < TooltipPulseIntervalMs)
            return;

        _pulseDots = (_pulseDots + 1) % (TooltipPulseMaxDots + 1);
        string dots = _pulseDots == 0 ? "" : new string('.', _pulseDots);
        TrySetTrayText($"TailSlap - Processing{dots}");
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
        if (!_currentConfig.Llm.Enabled)
        {
            try
            {
                NotificationService.ShowWarning(
                    "LLM processing is disabled. Enable it in settings first."
                );
            }
            catch { }
            return;
        }
        if (_isRefining)
        {
            try
            {
                NotificationService.ShowWarning("Refinement already in progress. Please wait.");
            }
            catch { }
            return;
        }
        _isRefining = true;
        _ = RefineSelectionAsync().ContinueWith(_ => _isRefining = false);
    }

    private async void TriggerTranscribe()
    {
        bool hasActiveCts = _transcriberCts != null && !_transcriberCts.IsCancellationRequested;
        Logger.Log(
            $"TriggerTranscribe called. Transcriber enabled: {_currentConfig.Transcriber.Enabled}, hasActiveCts: {hasActiveCts}, _isTranscribing: {_isTranscribing}"
        );

        if (!_currentConfig.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            try
            {
                NotificationService.ShowWarning(
                    "Remote transcription is disabled. Enable it in settings first."
                );
            }
            catch { }
            return;
        }

        // If recording is in progress (CTS exists and is not cancelled), stop it
        if (hasActiveCts)
        {
            Logger.Log("Stopping recording via cancellation token");
            try
            {
                _transcriberCts?.Cancel();
                NotificationService.ShowInfo("Stopping recording... Processing audio.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error cancelling transcription task: {ex.Message}");
            }
            return;
        }

        // If transcription is already in progress but recording is done, wait (don't allow new recording yet)
        if (_isTranscribing)
        {
            Logger.Log("Transcription already in progress - waiting for completion");
            try
            {
                NotificationService.ShowWarning(
                    "Transcription in progress. Please wait for completion."
                );
            }
            catch { }
            return;
        }

        Logger.Log("Starting new transcription task");
        _isTranscribing = true;

        try
        {
            await TranscribeSelectionAsync(useStreaming: _currentConfig.Transcriber.StreamResults);
        }
        catch (Exception ex)
        {
            Logger.Log($"CRITICAL: Transcription task failed at top level: {ex.Message}");
        }
        finally
        {
            Logger.Log("Transcription task completed top-level finally");
            _isTranscribing = false;
        }
    }

    private async void TriggerStreamingTranscribe()
    {
        StreamingState currentState;
        lock (_streamingStateLock)
        {
            currentState = _streamingState;
        }

        Logger.Log(
            $"TriggerStreamingTranscribe called. Transcriber enabled: {_currentConfig.Transcriber.Enabled}, state: {currentState}"
        );

        if (!_currentConfig.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            try
            {
                NotificationService.ShowWarning(
                    "Remote transcription is disabled. Enable it in settings first."
                );
            }
            catch { }
            return;
        }

        // State machine: only act on Idle or Streaming states, ignore transitions in progress
        lock (_streamingStateLock)
        {
            if (
                _streamingState == StreamingState.Starting
                || _streamingState == StreamingState.Stopping
            )
            {
                Logger.Log(
                    $"TriggerStreamingTranscribe: Ignoring hotkey, transition in progress (state={_streamingState})"
                );
                return;
            }

            if (_streamingState == StreamingState.Streaming)
            {
                _streamingState = StreamingState.Stopping;
            }
            else
            {
                _streamingState = StreamingState.Starting;
            }
            currentState = _streamingState;
        }

        if (currentState == StreamingState.Stopping)
        {
            await StopRealtimeStreamingAsync();
        }
        else
        {
            await StartRealtimeStreamingAsync();
        }
    }

    private async Task StartRealtimeStreamingAsync()
    {
        Logger.Log("StartRealtimeStreamingAsync: Starting real-time WebSocket transcription");
        _realtimeTranscriptionText = "";
        _lastTypedLength = 0;

        try
        {
            StartAnim();
            NotificationService.ShowInfo("Real-time transcription started. Speak now...");

            _realtimeTranscriber = new RealtimeTranscriber(_currentConfig.Transcriber.WebSocketUrl);
            _realtimeTranscriber.OnTranscription += OnRealtimeTranscription;
            _realtimeTranscriber.OnError += OnRealtimeError;
            _realtimeTranscriber.OnDisconnected += OnRealtimeDisconnected;

            await _realtimeTranscriber.ConnectAsync();
            Logger.Log("StartRealtimeStreamingAsync: WebSocket connected");

            _realtimeRecorder = new AudioRecorder(
                _currentConfig.Transcriber.PreferredMicrophoneIndex
            );
            _realtimeRecorder.SetVadThresholds(
                _currentConfig.Transcriber.VadSilenceThreshold,
                _currentConfig.Transcriber.VadActivationThreshold,
                _currentConfig.Transcriber.VadSustainThreshold
            );
            _realtimeRecorder.OnAudioChunk += OnRealtimeAudioChunk;
            _realtimeRecorder.OnSilenceDetected += OnRealtimeSilenceDetected;

            // Capture the foreground window to detect if user switches apps during dictation
            _streamingTargetWindow = GetForegroundWindow();
            _streamingStartTime = DateTime.UtcNow;
            Logger.Log(
                $"StartRealtimeStreamingAsync: Target window captured: 0x{_streamingTargetWindow:X}"
            );

            // Transition to Streaming state now that setup is complete
            lock (_streamingStateLock)
            {
                _streamingState = StreamingState.Streaming;
            }

            _transcriberCts = new CancellationTokenSource();
            await _realtimeRecorder.StartStreamingAsync(
                _transcriberCts.Token,
                enableVAD: _currentConfig.Transcriber.EnableVAD,
                silenceThresholdMs: _currentConfig.Transcriber.SilenceThresholdMs
            );
        }
        catch (Exception ex)
        {
            Logger.Log($"StartRealtimeStreamingAsync: Error - {ex.Message}");
            try
            {
                NotificationService.ShowError($"Real-time transcription failed: {ex.Message}");
            }
            catch { }

            await CleanupRealtimeStreamingAsync();
        }
    }

    private void OnRealtimeAudioChunk(ArraySegment<byte> chunk)
    {
        // Check state before processing audio
        StreamingState state;
        lock (_streamingStateLock)
        {
            state = _streamingState;
        }

        if (state != StreamingState.Streaming)
            return;

        // Check for no-speech timeout (user never spoke above activation threshold)
        if (
            _streamingStartTime != DateTime.MinValue
            && _realtimeTranscriptionText.Length == 0
            && (DateTime.UtcNow - _streamingStartTime).TotalSeconds >= NO_SPEECH_TIMEOUT_SECONDS
        )
        {
            Logger.Log(
                $"OnRealtimeAudioChunk: No speech detected after {NO_SPEECH_TIMEOUT_SECONDS}s, triggering auto-stop"
            );
            // Fire silence detected to initiate stop (on a separate task to avoid blocking audio processing)
            _ = Task.Run(() => OnRealtimeSilenceDetected());
            return;
        }

        if (_realtimeTranscriber?.IsConnected == true)
        {
            // Accumulate chunks to reduce server load (send every 500ms instead of 50ms)
            // This prevents TCP buffer filling up when server is slow to process
            lock (_streamingBuffer)
            {
                if (chunk.Array != null)
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        _streamingBuffer.Add(chunk.Array[chunk.Offset + i]);
                    }
                }

                if (_streamingBuffer.Count >= SEND_BUFFER_SIZE)
                {
                    var dataToSend = _streamingBuffer.ToArray();
                    _streamingBuffer.Clear();

                    // Fire-and-forget sending the aggregated chunk
                    _ = _realtimeTranscriber.SendAudioChunkAsync(
                        new ArraySegment<byte>(dataToSend)
                    );
                }
            }
        }
    }

    private void OnRealtimeTranscription(string text, bool isFinal)
    {
        Logger.Log($"OnRealtimeTranscription: text.Length={text.Length}, final={isFinal}");

        // Notify recorder that server detected speech (for auto-stop purposes)
        if (!string.IsNullOrEmpty(text) && !isFinal)
        {
            _realtimeRecorder?.NotifySpeechDetected();
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => HandleRealtimeTranscription(text, isFinal)));
        }
        else
        {
            HandleRealtimeTranscription(text, isFinal);
        }
    }

    private async void HandleRealtimeTranscription(string text, bool isFinal)
    {
        // Ensure strictly sequential processing of updates to prevent overlapping pastes/typing
        await _transcriptionLock.WaitAsync();
        try
        {
            // Check state - ignore if we're idle (cleanup already happened)
            StreamingState state;
            lock (_streamingStateLock)
            {
                state = _streamingState;
            }
            if (state == StreamingState.Idle)
            {
                Logger.Log("HandleRealtimeTranscription: Ignoring, state=Idle");
                return;
            }

            if (string.IsNullOrEmpty(text))
                return;

            // Check if foreground window changed - if so, reset baseline to prevent incorrect backspacing
            if (!IsForegroundWindowSafe())
            {
                Logger.Log("HandleRealtimeTranscription: Window changed, resetting baseline");
                _realtimeTranscriptionText = text;
                _lastTypedLength = 0;
                // Update target window for subsequent typing
                _streamingTargetWindow = GetForegroundWindow();
                return;
            }

            // Server behavior: Each partial is the CUMULATIVE transcription of all audio so far.
            // The server refines its transcription over time, so later partials may correct earlier ones.
            // We track what we've typed and update accordingly.

            if (!isFinal)
            {
                // Calculate common prefix to determine what needs to change
                int commonPrefixLen = 0;
                int minLen = Math.Min(_realtimeTranscriptionText.Length, text.Length);
                for (int i = 0; i < minLen; i++)
                {
                    if (_realtimeTranscriptionText[i] == text[i])
                        commonPrefixLen++;
                    else
                        break;
                }

                // Check for "New Segment" (Server cleared buffer)
                // If overlap is very low (< 5 chars) and we had substantial text before (> 10 chars),
                // assume the server started a new sentence/segment.
                if (commonPrefixLen < 5 && _lastTypedLength > 10)
                {
                    Logger.Log(
                        $"HandleRT: New segment detected (overlap {commonPrefixLen}). Committing previous."
                    );
                    _realtimeTranscriptionText = text;
                    _lastTypedLength = 0;

                    // Insert a space separator for the new segment
                    TypeTextDirectly(" ");

                    // Proceed to type the new text as if it's fresh
                    if (text.Length > 0)
                    {
                        Logger.Log($"HandleRT: Typing new segment: {text.Length} chars");
                        if (text.Length > 5)
                        {
                            bool pasteSuccess = await _clip.SetTextAndPasteAsync(text);
                            if (!pasteSuccess)
                                TypeTextDirectly(text);
                        }
                        else
                        {
                            TypeTextDirectly(text);
                        }
                        _lastTypedLength = text.Length;
                    }
                    return;
                }

                // If the change is too drastic (rewriting more than 20 chars), treat it as a correction
                // and wait (to avoid massive backspacing/typing loops on unstable transcriptions)
                // UNLESS we haven't typed much yet
                int backspaceCount = _lastTypedLength - commonPrefixLen;

                // Only apply incremental updates if we don't have to backspace too much
                // or if we are just appending
                if (backspaceCount >= 0 && backspaceCount < 10)
                {
                    if (backspaceCount > 0)
                    {
                        Logger.Log($"HandleRT: Backspacing {backspaceCount} chars for correction");
                        SendBackspace(backspaceCount);
                        _lastTypedLength = commonPrefixLen;
                        // Small delay to ensure backspaces process
                        await Task.Delay(20);
                    }

                    if (text.Length > _lastTypedLength)
                    {
                        var newText = text.Substring(_lastTypedLength);
                        Logger.Log($"HandleRT: Typing {newText.Length} chars");

                        if (newText.Length > 5)
                        {
                            bool pasteSuccess = await _clip.SetTextAndPasteAsync(newText);
                            if (!pasteSuccess)
                            {
                                TypeTextDirectly(newText);
                            }
                        }
                        else
                        {
                            TypeTextDirectly(newText);
                        }

                        _lastTypedLength = text.Length;
                    }
                    _realtimeTranscriptionText = text;
                }
                else
                {
                    // Correction is too large/unstable, just update baseline but don't type
                    // We'll catch up on the next stable update or final
                    Logger.Log(
                        $"HandleRT: Large correction detected (backspace {backspaceCount}), updating baseline only"
                    );
                    _realtimeTranscriptionText = text;
                }
            }
            else
            {
                // For final: apply the complete corrected text
                Logger.Log($"OnRealtimeTranscription: Final transcription received");

                // Calculate what needs to change
                int commonPrefixLen = 0;
                int minLen = Math.Min(_realtimeTranscriptionText.Length, text.Length);
                for (int i = 0; i < minLen; i++)
                {
                    if (_realtimeTranscriptionText[i] == text[i])
                        commonPrefixLen++;
                    else
                        break;
                }

                int backspaceCount = _lastTypedLength - commonPrefixLen;
                if (backspaceCount > 0)
                {
                    SendBackspace(backspaceCount);
                    _lastTypedLength = commonPrefixLen;
                    await Task.Delay(20);
                }

                if (text.Length > _lastTypedLength)
                {
                    var newText = text.Substring(_lastTypedLength);
                    if (newText.Length > 5)
                    {
                        bool pasteSuccess = await _clip.SetTextAndPasteAsync(newText);
                        if (!pasteSuccess)
                        {
                            TypeTextDirectly(newText);
                        }
                    }
                    else
                    {
                        TypeTextDirectly(newText);
                    }
                }

                // Reset for next utterance
                _lastTypedLength = 0;
                _realtimeTranscriptionText = "";
            }
        }
        finally
        {
            _transcriptionLock.Release();
        }
    }

    private bool IsForegroundWindowSafe()
    {
        if (_streamingTargetWindow == IntPtr.Zero)
            return true;

        var current = GetForegroundWindow();
        if (current != _streamingTargetWindow)
        {
            Logger.Log(
                $"IsForegroundWindowSafe: Window changed from 0x{_streamingTargetWindow:X} to 0x{current:X}, skipping destructive operation"
            );
            return false;
        }
        return true;
    }

    private void SendBackspace(int count)
    {
        if (count <= 0)
            return;

        if (!IsForegroundWindowSafe())
        {
            Logger.Log($"SendBackspace: Skipping {count} backspaces, foreground window changed");
            return;
        }

        try
        {
            SendKeys.SendWait("{BS " + count + "}");
            SendKeys.Flush();
        }
        catch (Exception ex)
        {
            Logger.Log($"SendBackspace failed: {ex.Message}");
        }
    }

    private async void OnRealtimeError(string error)
    {
        try
        {
            Logger.Log($"OnRealtimeError: {error}");
            try
            {
                NotificationService.ShowError($"Real-time transcription error: {error}");
            }
            catch { }

            // Initiate stop on error if we're streaming
            lock (_streamingStateLock)
            {
                if (_streamingState != StreamingState.Streaming)
                {
                    Logger.Log($"OnRealtimeError: Ignoring stop, state={_streamingState}");
                    return;
                }
                _streamingState = StreamingState.Stopping;
            }

            await StopRealtimeStreamingAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"OnRealtimeError: ERROR during handling - {ex.Message}");
        }
    }

    private async void OnRealtimeDisconnected()
    {
        try
        {
            Logger.Log("OnRealtimeDisconnected: WebSocket disconnected");
            bool shouldInitiateStop = false;
            lock (_streamingStateLock)
            {
                // If we're streaming, transition to stopping
                if (_streamingState == StreamingState.Streaming)
                {
                    _streamingState = StreamingState.Stopping;
                    shouldInitiateStop = true;
                }
                // If already stopping, just help with cleanup
                else if (_streamingState == StreamingState.Stopping)
                {
                    _realtimeRecorder?.StopRecording();
                    _transcriberCts?.Cancel();
                    return;
                }
            }

            if (shouldInitiateStop)
            {
                await StopRealtimeStreamingAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"OnRealtimeDisconnected: ERROR - {ex.Message}");
        }
    }

    private async void OnRealtimeSilenceDetected()
    {
        try
        {
            Logger.Log("OnRealtimeSilenceDetected: Silence detected, stopping streaming");

            // Only initiate stop if we're actively streaming
            lock (_streamingStateLock)
            {
                if (_streamingState != StreamingState.Streaming)
                {
                    Logger.Log($"OnRealtimeSilenceDetected: Ignoring, state={_streamingState}");
                    return;
                }
                _streamingState = StreamingState.Stopping;
            }

            await StopRealtimeStreamingAsync();
        }
        catch (Exception ex)
        {
            Logger.Log($"OnRealtimeSilenceDetected: ERROR - {ex.Message}");
        }
    }

    private async Task StopRealtimeStreamingAsync()
    {
        Logger.Log("StopRealtimeStreamingAsync: Stopping real-time transcription");
        try
        {
            NotificationService.ShowInfo("Stopping real-time transcription...");
        }
        catch { }

        _realtimeRecorder?.StopRecording();

        if (_realtimeTranscriber?.IsConnected == true)
        {
            try
            {
                // Create completion sources to wait for server completion
                var serverClosedTcs = new TaskCompletionSource<bool>();
                var finalMessageTcs = new TaskCompletionSource<bool>();

                void OnServerDisconnected()
                {
                    serverClosedTcs.TrySetResult(true);
                }

                void OnTranscriptionReceived(string text, bool isFinal)
                {
                    if (isFinal)
                        finalMessageTcs.TrySetResult(true);
                }

                _realtimeTranscriber.OnDisconnected += OnServerDisconnected;
                _realtimeTranscriber.OnTranscription += OnTranscriptionReceived;

                // Flush any remaining audio buffer before stopping
                byte[]? remainingData = null;
                lock (_streamingBuffer)
                {
                    if (_streamingBuffer.Count > 0)
                    {
                        remainingData = _streamingBuffer.ToArray();
                        _streamingBuffer.Clear();
                    }
                }

                if (remainingData != null)
                {
                    await _realtimeTranscriber.SendAudioChunkAsync(
                        new ArraySegment<byte>(remainingData)
                    );
                }

                // Send stop signal and padding
                await _realtimeTranscriber.StopAsync();

                Logger.Log(
                    "StopRealtimeStreamingAsync: Waiting for server to close connection or send final message..."
                );

                // Wait for server to send final transcription OR close connection
                // Give it up to 10 seconds (inference can be slow)
                await Task.WhenAny(serverClosedTcs.Task, finalMessageTcs.Task, Task.Delay(10000));

                _realtimeTranscriber.OnDisconnected -= OnServerDisconnected;
                _realtimeTranscriber.OnTranscription -= OnTranscriptionReceived;

                Logger.Log("StopRealtimeStreamingAsync: Wait complete or timed out");
            }
            catch (Exception ex)
            {
                Logger.Log($"StopRealtimeStreamingAsync: Error sending stop - {ex.Message}");
            }
        }

        _transcriberCts?.Cancel();

        // Explicitly cleanup now that we've waited for the final message
        await CleanupRealtimeStreamingAsync();
    }

    private async Task CleanupRealtimeStreamingAsync()
    {
        // Idempotency: ensure only one cleanup runs at a time
        if (Interlocked.Exchange(ref _cleanupInProgress, 1) == 1)
        {
            Logger.Log("CleanupRealtimeStreamingAsync: Already in progress, returning");
            return;
        }

        try
        {
            Logger.Log("CleanupRealtimeStreamingAsync: Cleaning up");

            // Wait for any pending transcription processing to finish (e.g. final message)
            // This prevents race condition where cleanup runs before HandleRealtimeTranscription updates text
            await _transcriptionLock.WaitAsync();
            _transcriptionLock.Release();

            // Take local snapshots, then null out fields to prevent other handlers from using them
            var transcriber = _realtimeTranscriber;
            var recorder = _realtimeRecorder;
            var cts = _transcriberCts;

            _realtimeTranscriber = null;
            _realtimeRecorder = null;
            _transcriberCts = null;

            // Unsubscribe and dispose transcriber
            if (transcriber != null)
            {
                transcriber.OnTranscription -= OnRealtimeTranscription;
                transcriber.OnError -= OnRealtimeError;
                transcriber.OnDisconnected -= OnRealtimeDisconnected;

                try
                {
                    await transcriber.DisconnectAsync();
                }
                catch { }

                transcriber.Dispose();
            }

            // Dispose recorder
            if (recorder != null)
            {
                recorder.OnAudioChunk -= OnRealtimeAudioChunk;
                recorder.OnSilenceDetected -= OnRealtimeSilenceDetected;
                recorder.Dispose();
            }

            cts?.Dispose();

            lock (_streamingBuffer)
            {
                _streamingBuffer.Clear();
            }

            lock (_streamingStateLock)
            {
                _streamingState = StreamingState.Idle;
            }
            _streamingTargetWindow = IntPtr.Zero;
            _streamingStartTime = DateTime.MinValue;
            StopAnim();

            // Type any remaining text that wasn't typed due to corrections
            // Run this logic on the UI thread to ensure it processes AFTER any pending HandleRealtimeTranscription
            // and to access updated text variables safely
            if (InvokeRequired)
            {
                Invoke(
                    new Action(async () =>
                    {
                        if (_realtimeTranscriptionText.Length > _lastTypedLength)
                        {
                            var remainingText = _realtimeTranscriptionText.Substring(
                                _lastTypedLength
                            );
                            Logger.Log(
                                $"CleanupRealtimeStreamingAsync: Typing remaining {remainingText.Length} chars"
                            );

                            if (remainingText.Length > 5)
                            {
                                await _clip.SetTextAndPasteAsync(remainingText);
                            }
                            else
                            {
                                TypeTextDirectly(remainingText);
                            }
                        }
                    })
                );
            }
            else
            {
                if (_realtimeTranscriptionText.Length > _lastTypedLength)
                {
                    var remainingText = _realtimeTranscriptionText.Substring(_lastTypedLength);
                    Logger.Log(
                        $"CleanupRealtimeStreamingAsync: Typing remaining {remainingText.Length} chars"
                    );

                    if (remainingText.Length > 5)
                    {
                        await _clip.SetTextAndPasteAsync(remainingText);
                    }
                    else
                    {
                        TypeTextDirectly(remainingText);
                    }
                }
            }

            if (!string.IsNullOrEmpty(_realtimeTranscriptionText))
            {
                try
                {
                    NotificationService.ShowSuccess("Real-time transcription complete.");
                }
                catch { }
            }

            Logger.Log("CleanupRealtimeStreamingAsync: Done");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupInProgress, 0);
        }
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

            // Check if hotkeys changed
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

    private async Task RefineSelectionAsync()
    {
        try
        {
            Logger.Log("RefineSelectionAsync started");
            StartAnim();
            Logger.Log("Starting capture from selection/clipboard");
            var text = await _clip.CaptureSelectionOrClipboardAsync(
                _currentConfig.UseClipboardFallback
            );
            Logger.Log(
                $"Captured length: {text?.Length ?? 0}, sha256={Sha256Hex(text ?? string.Empty)}"
            );
            if (string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    NotificationService.ShowWarning("No text selected or in clipboard.");
                }
                catch { }
                return;
            }
            var refiner = _textRefinerFactory.Create(_currentConfig.Llm);
            var refined = await refiner.RefineAsync(text);
            Logger.Log(
                $"Refined length: {refined?.Length ?? 0}, sha256={Sha256Hex(refined ?? string.Empty)}"
            );
            if (string.IsNullOrWhiteSpace(refined))
            {
                try
                {
                    NotificationService.ShowError("Provider returned empty result.");
                }
                catch { }
                return;
            }

            bool setTextSuccess = _clip.SetText(refined);
            if (!setTextSuccess)
            {
                return; // Error already shown by SetText
            }

            await Task.Delay(100);
            if (_currentConfig.AutoPaste)
            {
                Logger.Log("Auto-paste attempt");
                bool pasteSuccess = await _clip.PasteAsync().ConfigureAwait(true);
                if (!pasteSuccess)
                {
                    // Error already shown by Paste method, but we can continue
                    try
                    {
                        NotificationService.ShowInfo(
                            "Text is ready. You can paste manually with Ctrl+V."
                        );
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    NotificationService.ShowTextReadyNotification();
                }
                catch { }
            }

            try
            {
                _history.Append(text, refined, _currentConfig.Llm.Model);
            }
            catch { }
            Logger.Log("Refinement completed successfully.");
        }
        catch (Exception ex)
        {
            try
            {
                NotificationService.ShowError("Refinement failed: " + ex.Message);
            }
            catch { }
            Logger.Log("Error: " + ex.Message);
        }
        finally
        {
            StopAnim();
        }
    }

    private async Task TranscribeSelectionAsync(bool useStreaming = false)
    {
        string audioFilePath = "";
        RecordingStats? recordingStats = null;
        try
        {
            Logger.Log("TranscribeSelectionAsync started");
            Logger.Log(
                $"Transcriber config: BaseUrl={_currentConfig.Transcriber.BaseUrl}, Model={_currentConfig.Transcriber.Model}, Timeout={_currentConfig.Transcriber.TimeoutSeconds}s"
            );
            _transcriberCts = new CancellationTokenSource();
            Logger.Log($"Created new CancellationTokenSource: {_transcriberCts?.GetHashCode()}");

            // Start animation and show recording notification
            StartAnim();
            try
            {
                NotificationService.ShowInfo("🎤 Recording... Press hotkey again to stop.");
            }
            catch { }

            // Record audio from microphone
            audioFilePath = Path.Combine(
                Path.GetTempPath(),
                $"tailslap_recording_{Guid.NewGuid():N}.wav"
            );
            Logger.Log($"Audio file path: {audioFilePath}");
            try
            {
                Logger.Log("Starting audio recording from microphone");
                recordingStats = await RecordAudioAsync(audioFilePath);

                if (recordingStats.SilenceDetected)
                {
                    Logger.Log(
                        $"Audio recording stopped early due to silence detection at {recordingStats.DurationMs}ms"
                    );
                }
                Logger.Log(
                    $"Audio recorded to: {audioFilePath}, duration={recordingStats.DurationMs}ms"
                );

                if (recordingStats.DurationMs < 500)
                {
                    Logger.Log("Recording too short (< 500ms), skipping transcription.");
                    try
                    {
                        NotificationService.ShowWarning(
                            "Recording too short. Please speak longer."
                        );
                    }
                    catch { }
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Audio recording was stopped by user");
                // If user stopped it very quickly, we should also check duration here
                if (recordingStats != null && recordingStats.DurationMs < 500)
                {
                    try
                    {
                        NotificationService.ShowWarning("Recording cancelled (too short).");
                    }
                    catch { }
                    return;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    NotificationService.ShowError(
                        "Failed to record audio from microphone. Please check your microphone permissions."
                    );
                }
                catch { }
                Logger.Log($"Audio recording failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // Show transcribing animation
            try
            {
                NotificationService.ShowInfo("Sending to transcriber...");
            }
            catch { }

            // Transcribe audio using remote API
            Logger.Log(
                $"Creating RemoteTranscriber with BaseUrl: {_currentConfig.Transcriber.BaseUrl}"
            );
            var transcriber = _remoteTranscriberFactory.Create(_currentConfig.Transcriber);
            string transcriptionText = "";

            // Use streaming if requested for faster feedback
            if (useStreaming)
            {
                Logger.Log("Using streaming transcription for faster feedback");
                var fullText = new System.Text.StringBuilder();

                try
                {
                    await foreach (var chunk in transcriber.TranscribeStreamingAsync(audioFilePath))
                    {
                        if (string.IsNullOrEmpty(chunk))
                            continue;

                        fullText.Append(chunk);

                        // Type each chunk directly to the cursor position
                        if (_currentConfig.Transcriber.AutoPaste)
                        {
                            this.Invoke(
                                (MethodInvoker)
                                    delegate
                                    {
                                        TypeTextDirectly(chunk);
                                    }
                            );
                        }
                    }

                    transcriptionText = fullText.ToString();
                    Logger.Log(
                        $"Streaming transcription completed: {transcriptionText.Length} characters"
                    );

                    if (IsEmptyTranscription(transcriptionText))
                    {
                        try
                        {
                            NotificationService.ShowWarning("No speech detected.");
                        }
                        catch { }
                        return;
                    }

                    // Copy final result to clipboard for convenience
                    _clip.SetText(transcriptionText);

                    if (!_currentConfig.Transcriber.AutoPaste)
                    {
                        try
                        {
                            NotificationService.ShowTextReadyNotification();
                        }
                        catch { }
                    }
                }
                catch (TranscriberException ex)
                {
                    Logger.Log(
                        $"Streaming TranscriberException: ErrorType={ex.ErrorType}, StatusCode={ex.StatusCode}, Message={ex.Message}"
                    );
                    try
                    {
                        NotificationService.ShowError($"Transcription failed: {ex.Message}");
                    }
                    catch { }
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log(
                        $"Streaming unexpected exception: {ex.GetType().Name}: {ex.Message}"
                    );
                    try
                    {
                        NotificationService.ShowError($"Transcription failed: {ex.Message}");
                    }
                    catch { }
                    return;
                }
            }
            else
            {
                // Non-streaming path (original behavior)
                try
                {
                    Logger.Log($"Starting remote transcription of {audioFilePath}");
                    transcriptionText = await transcriber.TranscribeAudioAsync(audioFilePath);
                    Logger.Log(
                        $"Transcription completed: {transcriptionText?.Length ?? 0} characters, text={transcriptionText?.Substring(0, Math.Min(100, transcriptionText?.Length ?? 0))}"
                    );
                }
                catch (TranscriberException ex)
                {
                    Logger.Log(
                        $"TranscriberException: ErrorType={ex.ErrorType}, StatusCode={ex.StatusCode}, Message={ex.Message}"
                    );
                    try
                    {
                        NotificationService.ShowError($"Transcription failed: {ex.Message}");
                    }
                    catch { }
                    Logger.Log($"Transcription error: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log(
                        $"Unexpected exception: {ex.GetType().Name}: {ex.Message}, StackTrace: {ex.StackTrace}"
                    );
                    try
                    {
                        NotificationService.ShowError($"Transcription failed: {ex.Message}");
                    }
                    catch { }
                    Logger.Log("Error: " + ex.Message);
                    return;
                }

                if (IsEmptyTranscription(transcriptionText))
                {
                    try
                    {
                        NotificationService.ShowWarning("No speech detected.");
                    }
                    catch { }
                    return;
                }

                // Set transcription result to clipboard
                bool setTextSuccess = _clip.SetText(transcriptionText ?? "");
                if (!setTextSuccess)
                {
                    return; // Error already shown by SetText
                }

                await Task.Delay(100);
                if (_currentConfig.Transcriber.AutoPaste)
                {
                    Logger.Log("Transcriber auto-paste attempt");
                    // Run paste on UI thread to ensure SendKeys works correctly
                    this.Invoke(
                        (MethodInvoker)
                            async delegate
                            {
                                bool pasteSuccess = await _clip.PasteAsync();
                                if (!pasteSuccess)
                                {
                                    try
                                    {
                                        NotificationService.ShowInfo(
                                            "Transcription is ready. You can paste manually with Ctrl+V."
                                        );
                                    }
                                    catch { }
                                }
                            }
                    );
                }
                else
                {
                    try
                    {
                        NotificationService.ShowTextReadyNotification();
                    }
                    catch { }
                }
            }

            // Log transcription to history (separate from LLM refinement history)
            try
            {
                _history.AppendTranscription(
                    transcriptionText ?? "",
                    recordingStats?.DurationMs ?? 0
                );
                Logger.Log(
                    $"Transcription logged: {transcriptionText?.Length ?? 0} characters, duration={recordingStats?.DurationMs}ms"
                );
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log($"Failed to log transcription to history: {ex.Message}");
                }
                catch { }
            }

            Logger.Log("Transcription completed successfully.");
        }
        catch (Exception ex)
        {
            try
            {
                NotificationService.ShowError("Transcription failed: " + ex.Message);
            }
            catch { }
            Logger.Log($"TranscribeSelectionAsync error: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Logger.Log(
                    $"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}"
                );
            }
        }
        finally
        {
            StopAnim();

            // Clean up cancellation token
            _transcriberCts?.Dispose();
            _transcriberCts = null;

            // Clean up temporary audio file
            try
            {
                if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
                {
                    File.Delete(audioFilePath);
                }
            }
            catch { }
        }
    }

    private static bool IsEmptyTranscription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var trimmed = text.Trim();
        return trimmed.Equals("[Empty transcription]", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("(empty)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("[silence]", StringComparison.OrdinalIgnoreCase);
    }

    private static void TypeTextDirectly(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            // Escape special SendKeys characters: +^%~(){}[]
            var escaped = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (
                    c == '+'
                    || c == '^'
                    || c == '%'
                    || c == '~'
                    || c == '('
                    || c == ')'
                    || c == '{'
                    || c == '}'
                    || c == '['
                    || c == ']'
                )
                {
                    escaped.Append('{').Append(c).Append('}');
                }
                else if (c == '\n')
                {
                    escaped.Append("{ENTER}");
                }
                else if (c == '\r')
                {
                    // Skip carriage returns
                }
                else
                {
                    escaped.Append(c);
                }
            }

            SendKeys.SendWait(escaped.ToString());
            SendKeys.Flush();
        }
        catch (Exception ex)
        {
            Logger.Log($"TypeTextDirectly failed: {ex.Message}");
        }
    }

    private async Task<RecordingStats> RecordAudioAsync(string audioFilePath)
    {
        Logger.Log(
            $"RecordAudioAsync started. PreferredMic: {_currentConfig.Transcriber.PreferredMicrophoneIndex}, EnableVAD: {_currentConfig.Transcriber.EnableVAD}, VADThreshold: {_currentConfig.Transcriber.SilenceThresholdMs}ms"
        );
        using var recorder = new AudioRecorder(_currentConfig.Transcriber.PreferredMicrophoneIndex);
        recorder.SetVadThresholds(
            _currentConfig.Transcriber.VadSilenceThreshold,
            _currentConfig.Transcriber.VadActivationThreshold,
            _currentConfig.Transcriber.VadSustainThreshold
        );
        try
        {
            // maxDurationMs = 0 means record until cancellation (hotkey-based toggle)
            Logger.Log($"Starting recorder with CancellationToken");
            var stats = await recorder.RecordAsync(
                audioFilePath,
                maxDurationMs: 0,
                ct: _transcriberCts?.Token ?? CancellationToken.None,
                enableVAD: _currentConfig.Transcriber.EnableVAD,
                silenceThresholdMs: _currentConfig.Transcriber.SilenceThresholdMs
            );

            Logger.Log(
                $"Recording completed: {stats.DurationMs}ms, {stats.BytesRecorded} bytes, silence_detected={stats.SilenceDetected}"
            );
            if (!File.Exists(audioFilePath))
            {
                Logger.Log($"ERROR: Audio file not created at {audioFilePath}");
            }
            else
            {
                var fileInfo = new FileInfo(audioFilePath);
                Logger.Log($"Audio file created: {audioFilePath}, size: {fileInfo.Length} bytes");
            }
            return stats;
        }
        catch (OperationCanceledException ex)
        {
            Logger.Log($"Recording cancelled: {ex.Message}");
            throw;
        }
        catch (InvalidOperationException ex)
        {
            Logger.Log($"AudioRecorder error: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"RecordAudioAsync unexpected error: {ex.GetType().Name}: {ex.Message}, StackTrace: {ex.StackTrace}"
            );
            throw;
        }
    }

    private static string Sha256Hex(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        try
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(s);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(inputBytes, hash);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
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

    // Legacy Notify method kept for compatibility but should use NotificationService instead
    private void Notify(string msg, bool error = false)
    {
        if (error)
            NotificationService.ShowError(msg);
        else
            NotificationService.ShowInfo(msg);
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
            // Create a clone to edit so we don't modify the live config until OK is clicked
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

                // Update the live config object with the values from the clone
                _currentConfig = clone;
                _config.Save(_currentConfig);

                Logger.Log(
                    $"LLM hotkey after reload: mods={_currentConfig.Hotkey.Modifiers}, key={_currentConfig.Hotkey.Key}"
                );
                Logger.Log(
                    $"Transcriber hotkey after reload: mods={_currentConfig.TranscriberHotkey.Modifiers}, key={_currentConfig.TranscriberHotkey.Key}"
                );

                // Re-register refinement hotkey
                try
                {
                    UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID);
                }
                catch { }
                _currentMods = _currentConfig.Hotkey.Modifiers;
                _currentVk = _currentConfig.Hotkey.Key;
                RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);

                // Re-register transcriber hotkey if transcriber was enabled/disabled
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

                // Re-register streaming transcriber hotkey
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
