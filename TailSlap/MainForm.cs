using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

public class MainForm : Form
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _animTimer;
    private int _frame = 0;
    private Icon[] _frames;
    private Icon _idleIcon;
    private const int WM_HOTKEY = 0x0312;
    private const int REFINEMENT_HOTKEY_ID = 1;
    private const int TRANSCRIBER_HOTKEY_ID = 2;

    private readonly ConfigService _config;
    private readonly ClipboardService _clip;
    private uint _currentMods;
    private uint _currentVk;
    private uint _transcriberMods;
    private uint _transcriberVk;
    private AppConfig _currentConfig;
    private bool _isRefining;
    private bool _isTranscribing;
    private CancellationTokenSource? _transcriberCts;

    public MainForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Visible = false;

        _config = new ConfigService();
        _currentConfig = _config.LoadOrDefault();
        _clip = new ClipboardService();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Refine Now", null, (_, __) => TriggerRefine());
        _menu.Items.Add("Transcribe Now", null, (_, __) => TriggerTranscribe());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings...", null, (_, __) => ShowSettings(_currentConfig));
        _menu.Items.Add("Open Logs...", null, (_, __) => { try { Process.Start("notepad", System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "TailSlap", "app.log")); } catch { NotificationService.ShowError("Failed to open logs."); } });
        _menu.Items.Add("Encrypted Refinement History...", null, (_, __) => { try { using var hf = new HistoryForm(); hf.ShowDialog(); } catch { NotificationService.ShowError("Failed to open history."); } });
        _menu.Items.Add("Encrypted Transcription History...", null, (_, __) => { try { using var hf = new TranscriptionHistoryForm(); hf.ShowDialog(); } catch { NotificationService.ShowError("Failed to open transcription history."); } });
        var autoStartItem = new ToolStripMenuItem("Start with Windows") { Checked = AutoStartService.IsEnabled("TailSlap") };
        autoStartItem.Click += (_, __) => { AutoStartService.Toggle("TailSlap"); autoStartItem.Checked = AutoStartService.IsEnabled("TailSlap"); };
        _menu.Items.Add(autoStartItem);
        _menu.Items.Add("Quit", null, (_, __) => { Application.Exit(); });

        _idleIcon = LoadIdleIcon();
        _frames = LoadChewingFramesOrFallback(); // Icons are preloaded here to avoid per-frame allocations during animation

        _tray = new NotifyIcon { Icon = _idleIcon, Visible = true, Text = "TailSlap" };
        _tray.ContextMenuStrip = _menu;
        
        // Initialize notification service
        NotificationService.Initialize(_tray);
        
        _animTimer = new System.Windows.Forms.Timer { Interval = 100 }; // Faster animation for better visibility
        _animTimer.Tick += (_, __) => { 
            try 
            {
                int currentFrame = _frame % _frames.Length;
                _tray.Icon = _frames[currentFrame]; 
                _frame++;
                // Add subtle pulsing effect during animation
                if (_frame % 4 == 0) _tray.Text = "TailSlap - Processing...";
                else _tray.Text = "TailSlap";
            }
            catch (Exception ex) 
            {
                try { Logger.Log($"Animation tick error: {ex.Message}"); } catch { }
            }
        };
        
        // Animation is managed by RefineSelectionAsync (StartAnim on start, StopAnim in finally)
        // so we don't need to subscribe to CaptureStarted/CaptureEnded events
        
        _currentMods = _currentConfig.Hotkey.Modifiers;
        _currentVk = _currentConfig.Hotkey.Key;
        _transcriberMods = _currentConfig.TranscriberHotkey.Modifiers;
        _transcriberVk = _currentConfig.TranscriberHotkey.Key;
        Logger.Log($"MainForm initialized. Refinement hotkey mods={_currentMods}, key={_currentVk}. Transcriber hotkey mods={_transcriberMods}, key={_transcriberVk}");
    }

    private Icon[] LoadChewingFramesOrFallback()
    {
        try
        {
            var list = new System.Collections.Generic.List<Icon>(4);
            string baseDir = Application.StartupPath;
            string iconsDir = System.IO.Path.Combine(baseDir, "Icons");
            
            // Determine optimal icon size based on DPI
            int preferredSize = GetOptimalIconSize();
            
            for (int i = 1; i <= 4; i++)
            {
                // Try to load enhanced icons first, then fallback to standard
                string[] iconPaths = {
                    System.IO.Path.Combine(iconsDir, $"Chewing{i}_enhanced.ico"),
                    System.IO.Path.Combine(iconsDir, $"Chewing{i}.ico"),
                    System.IO.Path.Combine(iconsDir, $"chewing{i}.ico")
                };
                
                foreach (string p in iconPaths)
                {
                    if (System.IO.File.Exists(p)) 
                    { 
                        try 
                        { 
                            var icon = new Icon(p, preferredSize, preferredSize);
                            list.Add(icon);
                            break; // Use first available for this frame
                        } 
                        catch 
                        { 
                            try { list.Add(new Icon(p)); } catch { } 
                        } 
                    }
                }
                
                // Try loading from embedded resources if file not found
                if (list.Count < i)
                {
                    var icon = LoadIconFromResources($"TailSlap.Icons.Chewing{i}.ico");
                    if (icon != null) list.Add(icon);
                }
            }
            
            if (list.Count > 0) 
            {
                Logger.Log($"Loaded {list.Count} animation frames at {preferredSize}px");
                return list.ToArray();
            }
        }
        catch { }
        // Fallback to idle icon to ensure at least one frame exists
        return new[] { _idleIcon };
    }
    
    private static Icon? LoadIconFromResources(string resourceName)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    return new Icon(stream);
                }
            }
        }
        catch { }
        return null;
    }

    private Icon LoadIdleIcon()
    {
        try
        {
            string iconsDir = System.IO.Path.Combine(Application.StartupPath, "Icons");
            int preferredSize = GetOptimalIconSize();
            
            // Try enhanced icons first, then standard icons
            string[] iconPaths = {
                System.IO.Path.Combine(iconsDir, "Idle_enhanced.ico"),
                System.IO.Path.Combine(iconsDir, "Chewing1.ico"),
                System.IO.Path.Combine(iconsDir, "chewing1.ico")
            };
            
            foreach (string p in iconPaths)
            {
                if (System.IO.File.Exists(p)) 
                { 
                    try 
                    { 
                        var icon = new Icon(p, preferredSize, preferredSize);
                        Logger.Log($"Loaded idle icon at {preferredSize}px from {p}");
                        return icon;
                    } 
                    catch 
                    { 
                        try { return new Icon(p); } catch { } 
                    } 
                }
            }
            
            // Try loading from embedded resources
            var resourceIcon = LoadIconFromResources("TailSlap.Icons.Chewing1.ico");
            if (resourceIcon != null)
            {
                Logger.Log("Loaded idle icon from embedded resources");
                return resourceIcon;
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
            string mainIconPath = System.IO.Path.Combine(iconsDir, "TailSlap.ico");
            int preferredSize = GetOptimalIconSize();
            
            if (System.IO.File.Exists(mainIconPath))
            {
                try 
                { 
                    var icon = new Icon(mainIconPath, preferredSize, preferredSize);
                    Logger.Log($"Loaded main icon at {preferredSize}px from {mainIconPath}");
                    return icon;
                } 
                catch 
                { 
                    try { return new Icon(mainIconPath); } catch { } 
                } 
            }
            
            // Try loading from embedded resources
            var resourceIcon = LoadIconFromResources("TailSlap.Icons.TailSlap.ico");
            if (resourceIcon != null)
            {
                Logger.Log("Loaded main icon from embedded resources");
                return resourceIcon;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    private static int GetOptimalIconSize()
    {
        try
        {
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                float dpiX = graphics.DpiX;
                // Scale icon size based on DPI: 96dpi = 16px, 192dpi = 32px, etc.
                int baseSize = 16;
                float scaleFactor = dpiX / 96.0f;
                int scaledSize = (int)(baseSize * scaleFactor);
                
                // Clamp to reasonable sizes and ensure even numbers
                scaledSize = Math.Max(16, Math.Min(48, scaledSize));
                if (scaledSize % 2 != 0) scaledSize++;
                
                return scaledSize;
            }
        }
        catch
        {
            return 16; // Fallback to standard size
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);
        if (_currentConfig.Transcriber.Enabled)
        {
            RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID); } catch { }
        try { UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID); } catch { }
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
        }
        base.WndProc(ref m);
    }

    private void TriggerRefine()
    {
        if (!_currentConfig.Llm.Enabled)
        {
            try { NotificationService.ShowWarning("LLM processing is disabled. Enable it in settings first."); } catch { }
            return;
        }
        if (_isRefining)
        {
            try { NotificationService.ShowWarning("Refinement already in progress. Please wait."); } catch { }
            return;
        }
        _isRefining = true;
        _ = RefineSelectionAsync().ContinueWith(_ => _isRefining = false);
    }

    private async void TriggerTranscribe()
    {
        bool hasActiveCts = _transcriberCts != null && !_transcriberCts.IsCancellationRequested;
        Logger.Log($"TriggerTranscribe called. Transcriber enabled: {_currentConfig.Transcriber.Enabled}, hasActiveCts: {hasActiveCts}, _isTranscribing: {_isTranscribing}");
        
        if (!_currentConfig.Transcriber.Enabled)
        {
            Logger.Log("Transcriber is disabled");
            try { NotificationService.ShowWarning("Remote transcription is disabled. Enable it in settings first."); } catch { }
            return;
        }
        
        // If recording is in progress (CTS exists and is not cancelled), stop it
        if (hasActiveCts)
        {
            Logger.Log("Stopping recording via cancellation token");
            try 
            { 
                _transcriberCts?.Cancel(); 
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
            try { NotificationService.ShowWarning("Transcription in progress. Please wait for completion."); } catch { }
            return;
        }
        
        Logger.Log("Starting new transcription task");
        _isTranscribing = true;
        
        try
        {
            await TranscribeSelectionAsync();
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

    private async Task RefineSelectionAsync()
    {
        try
        {
            Logger.Log("RefineSelectionAsync started");
            StartAnim();
            Logger.Log("Starting capture from selection/clipboard");
            var text = await _clip.CaptureSelectionOrClipboardAsync(_currentConfig.UseClipboardFallback);
            Logger.Log($"Captured length: {text?.Length ?? 0}, sha256={Sha256Hex(text ?? string.Empty)}");
            if (string.IsNullOrWhiteSpace(text)) 
            { 
                try { NotificationService.ShowWarning("No text selected or in clipboard."); } catch { }
                return; 
            }
            var refiner = new TextRefiner(_currentConfig.Llm);
            var refined = await refiner.RefineAsync(text);
            Logger.Log($"Refined length: {refined?.Length ?? 0}, sha256={Sha256Hex(refined ?? string.Empty)}");
            if (string.IsNullOrWhiteSpace(refined)) 
            { 
                try { NotificationService.ShowError("Provider returned empty result."); } catch { }
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
                    try { NotificationService.ShowInfo("Text is ready. You can paste manually with Ctrl+V."); } catch { }
                }
            }
            else
            {
                try { NotificationService.ShowTextReadyNotification(); } catch { }
            }
            
            try { HistoryService.Append(text, refined, _currentConfig.Llm.Model); } catch { }
            Logger.Log("Refinement completed successfully.");
        }
        catch (Exception ex) 
        { 
            try { NotificationService.ShowError("Refinement failed: " + ex.Message); } catch { }
            Logger.Log("Error: " + ex.Message);
        }
        finally { StopAnim(); }
    }

    private async Task TranscribeSelectionAsync()
    {
        string audioFilePath = "";
        RecordingStats? recordingStats = null;
        try
        {
            Logger.Log("TranscribeSelectionAsync started");
            Logger.Log($"Transcriber config: BaseUrl={_currentConfig.Transcriber.BaseUrl}, Model={_currentConfig.Transcriber.Model}, Timeout={_currentConfig.Transcriber.TimeoutSeconds}s");
            _transcriberCts = new CancellationTokenSource();
            Logger.Log($"Created new CancellationTokenSource: {_transcriberCts?.GetHashCode()}");
            
            // Start animation and show recording notification
            StartAnim();
            try { NotificationService.ShowInfo("🎤 Recording... Press hotkey again to stop."); } catch { }
            
            // Record audio from microphone
            audioFilePath = Path.Combine(Path.GetTempPath(), $"tailslap_recording_{Guid.NewGuid():N}.wav");
            Logger.Log($"Audio file path: {audioFilePath}");
            try
            {
                Logger.Log("Starting audio recording from microphone");
                recordingStats = await RecordAudioAsync(audioFilePath);
                
                if (recordingStats.SilenceDetected)
                {
                    Logger.Log($"Audio recording stopped early due to silence detection at {recordingStats.DurationMs}ms");
                }
                Logger.Log($"Audio recorded to: {audioFilePath}, duration={recordingStats.DurationMs}ms");
                
                if (recordingStats.DurationMs < 500)
                {
                    Logger.Log("Recording too short (< 500ms), skipping transcription.");
                    try { NotificationService.ShowWarning("Recording too short. Please speak longer."); } catch { }
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Audio recording was stopped by user");
                // If user stopped it very quickly, we should also check duration here
                if (recordingStats != null && recordingStats.DurationMs < 500)
                {
                     try { NotificationService.ShowWarning("Recording cancelled (too short)."); } catch { }
                     return;
                }
            }
            catch (Exception ex)
            {
                try { NotificationService.ShowError("Failed to record audio from microphone. Please check your microphone permissions."); } catch { }
                Logger.Log($"Audio recording failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // Show transcribing animation
            try { NotificationService.ShowInfo("Sending to transcriber..."); } catch { }
            
            // Transcribe audio using remote API
            Logger.Log($"Creating RemoteTranscriber with BaseUrl: {_currentConfig.Transcriber.BaseUrl}");
            using var transcriber = new RemoteTranscriber(_currentConfig.Transcriber);
            string transcriptionText;
            
            try
            {
                Logger.Log($"Starting remote transcription of {audioFilePath}");
                transcriptionText = await transcriber.TranscribeAudioAsync(audioFilePath);
                Logger.Log($"Transcription completed: {transcriptionText?.Length ?? 0} characters, text={transcriptionText?.Substring(0, Math.Min(100, transcriptionText?.Length ?? 0))}");
            }
            catch (TranscriberException ex)
            {
                Logger.Log($"TranscriberException: ErrorType={ex.ErrorType}, StatusCode={ex.StatusCode}, Message={ex.Message}");
                try { NotificationService.ShowError($"Transcription failed: {ex.Message}"); } catch { }
                Logger.Log($"Transcription error: {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                Logger.Log($"Unexpected exception: {ex.GetType().Name}: {ex.Message}, StackTrace: {ex.StackTrace}");
                try { NotificationService.ShowError($"Transcription failed: {ex.Message}"); } catch { }
                Logger.Log("Error: " + ex.Message);
                return;
            }
            
            if (string.IsNullOrWhiteSpace(transcriptionText) || 
                transcriptionText.Equals("[Empty transcription]", StringComparison.OrdinalIgnoreCase) ||
                transcriptionText.Equals("(empty)", StringComparison.OrdinalIgnoreCase) ||
                transcriptionText.Equals("[silence]", StringComparison.OrdinalIgnoreCase)) 
            { 
                try { NotificationService.ShowWarning("No speech detected."); } catch { }
                return; 
            }
            
            // Set transcription result to clipboard
            bool setTextSuccess = _clip.SetText(transcriptionText);
            if (!setTextSuccess)
            {
                return; // Error already shown by SetText
            }
            
            await Task.Delay(100);
            if (_currentConfig.Transcriber.AutoPaste) 
            { 
                Logger.Log("Transcriber auto-paste attempt");
                // Run paste on UI thread to ensure SendKeys works correctly
                this.Invoke((MethodInvoker)async delegate 
                {
                    bool pasteSuccess = await _clip.PasteAsync();
                    if (!pasteSuccess)
                    {
                        try { NotificationService.ShowInfo("Transcription is ready. You can paste manually with Ctrl+V."); } catch { }
                    }
                });
            }
            else
            {
                try { NotificationService.ShowTextReadyNotification(); } catch { }
            }
            
            // Log transcription to history (separate from LLM refinement history)
            try 
            { 
                HistoryService.AppendTranscription(transcriptionText, recordingStats?.DurationMs ?? 0);
                Logger.Log($"Transcription logged: {transcriptionText.Length} characters, duration={recordingStats?.DurationMs}ms");
            } 
            catch (Exception ex)
            {
                try { Logger.Log($"Failed to log transcription to history: {ex.Message}"); } catch { }
            }
            
            Logger.Log("Transcription completed successfully.");
        }
        catch (Exception ex) 
        { 
            try { NotificationService.ShowError("Transcription failed: " + ex.Message); } catch { }
            Logger.Log($"TranscribeSelectionAsync error: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Logger.Log($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
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

    private async Task<RecordingStats> RecordAudioAsync(string audioFilePath)
    {
        Logger.Log($"RecordAudioAsync started. PreferredMic: {_currentConfig.Transcriber.PreferredMicrophoneIndex}, EnableVAD: {_currentConfig.Transcriber.EnableVAD}, VADThreshold: {_currentConfig.Transcriber.SilenceThresholdMs}ms");
        using var recorder = new AudioRecorder(_currentConfig.Transcriber.PreferredMicrophoneIndex);
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
            
            Logger.Log($"Recording completed: {stats.DurationMs}ms, {stats.BytesRecorded} bytes, silence_detected={stats.SilenceDetected}");
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
            Logger.Log($"RecordAudioAsync unexpected error: {ex.GetType().Name}: {ex.Message}, StackTrace: {ex.StackTrace}");
            throw;
        }
    }

    private static string Sha256Hex(string s)
    {
        try
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(s);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
        catch { return ""; }
    }

    private void StartAnim() { try { Logger.Log("Animation START"); } catch { } _frame = 0; _animTimer.Start(); }
    private void StopAnim() { try { Logger.Log("Animation STOP"); } catch { } _animTimer.Stop(); _frame = 0; _tray.Icon = _idleIcon; _tray.Text = "TailSlap"; }
    // Legacy Notify method kept for compatibility but should use NotificationService instead
    private void Notify(string msg, bool error = false) 
    { 
        if (error) NotificationService.ShowError(msg);
        else NotificationService.ShowInfo(msg);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterHotkey(uint mods, uint vk, int hotkeyId)
    {
        try { if (Handle != IntPtr.Zero) UnregisterHotKey(Handle, hotkeyId); } catch { }
        if (mods == 0) mods = 0x0003;
        if (vk == 0) vk = (uint)Keys.R;
        var ok = RegisterHotKey(Handle, hotkeyId, mods, vk);
        Logger.Log($"RegisterHotKey mods={mods}, key={vk}, id={hotkeyId}, ok={ok}");
        if (!ok) NotificationService.ShowError("Failed to register hotkey.");
    }

    private void ShowSettings(AppConfig cfg)
    {
        using var dlg = new SettingsForm(cfg);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            Logger.Log($"Settings OK clicked. LLM hotkey before save: mods={_currentConfig.Hotkey.Modifiers}, key={_currentConfig.Hotkey.Key}");
            Logger.Log($"Transcriber hotkey before save: mods={_currentConfig.TranscriberHotkey.Modifiers}, key={_currentConfig.TranscriberHotkey.Key}");
            
            _config.Save(_currentConfig);
            // Reload config from disk to ensure all validation/normalization is applied
            _currentConfig = _config.LoadOrDefault();
            
            Logger.Log($"LLM hotkey after reload: mods={_currentConfig.Hotkey.Modifiers}, key={_currentConfig.Hotkey.Key}");
            Logger.Log($"Transcriber hotkey after reload: mods={_currentConfig.TranscriberHotkey.Modifiers}, key={_currentConfig.TranscriberHotkey.Key}");

            // Re-register refinement hotkey
            try { UnregisterHotKey(Handle, REFINEMENT_HOTKEY_ID); } catch { }
            _currentMods = _currentConfig.Hotkey.Modifiers;
            _currentVk = _currentConfig.Hotkey.Key;
            RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);

            // Re-register transcriber hotkey if transcriber was enabled/disabled
            try { UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID); } catch { }
            _transcriberMods = _currentConfig.TranscriberHotkey.Modifiers;
            _transcriberVk = _currentConfig.TranscriberHotkey.Key;
            if (_currentConfig.Transcriber.Enabled)
            {
                RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
            }

            NotificationService.ShowSuccess("Settings saved.");
        }
    }
}
