using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows.Forms;
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
        var autoPasteItem = new ToolStripMenuItem("Auto Paste") { Checked = _currentConfig.AutoPaste };
        autoPasteItem.Click += (_, __) => { _currentConfig.AutoPaste = !_currentConfig.AutoPaste; autoPasteItem.Checked = _currentConfig.AutoPaste; _config.Save(_currentConfig); };
        _menu.Items.Add(autoPasteItem);
        var transcriberAutoPasteItem = new ToolStripMenuItem("Transcriber Auto Paste") { Checked = _currentConfig.Transcriber.AutoPaste };
        transcriberAutoPasteItem.Click += (_, __) => { _currentConfig.Transcriber.AutoPaste = !_currentConfig.Transcriber.AutoPaste; transcriberAutoPasteItem.Checked = _currentConfig.Transcriber.AutoPaste; _config.Save(_currentConfig); };
        _menu.Items.Add(transcriberAutoPasteItem);
        _menu.Items.Add("Change Hotkey", null, (_, __) => ChangeHotkey(_currentConfig));
        _menu.Items.Add("Change Transcriber Hotkey", null, (_, __) => ChangeTranscriberHotkey(_currentConfig));
        _menu.Items.Add("Settings...", null, (_, __) => ShowSettings(_currentConfig));
        _menu.Items.Add("Open Logs...", null, (_, __) => { try { Process.Start("notepad", System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "TailSlap", "app.log")); } catch { NotificationService.ShowError("Failed to open logs."); } });
        _menu.Items.Add("History...", null, (_, __) => { try { using var hf = new HistoryForm(); hf.ShowDialog(); } catch { NotificationService.ShowError("Failed to open history."); } });
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
        if (_isRefining)
        {
            try { NotificationService.ShowWarning("Refinement already in progress. Please wait."); } catch { }
            return;
        }
        _isRefining = true;
        _ = RefineSelectionAsync().ContinueWith(_ => _isRefining = false);
    }

    private void TriggerTranscribe()
    {
        if (!_currentConfig.Transcriber.Enabled)
        {
            try { NotificationService.ShowWarning("Remote transcription is disabled. Enable it in settings first."); } catch { }
            return;
        }
        
        if (_isTranscribing)
        {
            try { NotificationService.ShowWarning("Transcription already in progress. Please wait."); } catch { }
            return;
        }
        _isTranscribing = true;
        _ = TranscribeSelectionAsync().ContinueWith(_ => _isTranscribing = false);
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
        try
        {
            Logger.Log("TranscribeSelectionAsync started");
            StartAnim();
            
            // Record audio from microphone
            string audioFilePath = Path.Combine(Path.GetTempPath(), $"tailslap_recording_{Guid.NewGuid():N}.wav");
            try
            {
                Logger.Log("Starting audio recording from microphone");
                // Record audio from microphone using NAudio or similar
                // For now, we'll use a simple implementation
                await RecordAudioAsync(audioFilePath);
                Logger.Log($"Audio recorded to: {audioFilePath}");
            }
            catch (Exception ex)
            {
                try { NotificationService.ShowError("Failed to record audio from microphone. Please check your microphone permissions."); } catch { }
                Logger.Log($"Audio recording failed: {ex.Message}");
                return;
            }

            // Transcribe audio using remote API
            var transcriber = new RemoteTranscriber(_currentConfig.Transcriber);
            string transcriptionText;
            
            try
            {
                Logger.Log("Starting remote transcription");
                transcriptionText = await transcriber.TranscribeAudioAsync(audioFilePath);
                Logger.Log($"Transcription completed: {transcriptionText?.Length ?? 0} characters");
            }
            catch (TranscriberException ex)
            {
                if (ex.IsRetryable())
                {
                    try { NotificationService.ShowWarning($"Transcription failed, but will retry. Error: {ex.Message}"); } catch { }
                }
                else
                {
                    try { NotificationService.ShowError($"Transcription failed permanently: {ex.Message}"); } catch { }
                    return;
                }
                throw; // Re-throw for outer catch
            }
            catch (Exception ex)
            {
                try { NotificationService.ShowError($"Transcription failed: {ex.Message}"); } catch { }
                Logger.Log("Error: " + ex.Message);
                return;
            }
            
            if (string.IsNullOrWhiteSpace(transcriptionText)) 
            { 
                try { NotificationService.ShowWarning("No speech detected or transcription returned empty result."); } catch { }
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
                bool pasteSuccess = await _clip.PasteAsync().ConfigureAwait(true);
                if (!pasteSuccess)
                {
                    // Error already shown by Paste method
                    try { NotificationService.ShowInfo("Transcription is ready. You can paste manually with Ctrl+V."); } catch { }
                }
            }
            else
            {
                try { NotificationService.ShowTextReadyNotification(); } catch { }
            }
            
            // Log transcription to history (separate from LLM refinement history)
            try 
            { 
                Logger.Log($"Transcription logged: {transcriptionText.Length} characters");
            } 
            catch { }
            
            Logger.Log("Transcription completed successfully.");
        }
        catch (Exception ex) 
        { 
            try { NotificationService.ShowError("Transcription failed: " + ex.Message); } catch { }
            Logger.Log("Error: " + ex.Message);
        }
        finally 
        { 
            StopAnim();
            // Clean up temporary audio file
            try
            {
                if (File.Exists(Path.Combine(Path.GetTempPath(), $"tailslap_recording_{Guid.NewGuid():N}.wav")))
                {
                    File.Delete(Path.Combine(Path.GetTempPath(), $"tailslap_recording_{Guid.NewGuid():N}.wav"));
                }
            }
            catch { }
        }
    }

    private async Task RecordAudioAsync(string audioFilePath)
    {
        // Placeholder implementation for audio recording
        // This would need to be implemented with NAudio or similar library
        // For now, we'll create a silence file for testing
        await Task.Delay(2000); // Simulate recording time
        
        // Create a simple WAV file with silence
        using var fileStream = File.Create(audioFilePath);
        using var writer = new BinaryWriter(fileStream);
        
        // WAV header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36); // File size - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // Subchunk1Size
        writer.Write((short)1); // AudioFormat
        writer.Write((short)1); // NumChannels
        writer.Write(16000); // SampleRate
        writer.Write(32000); // ByteRate
        writer.Write((short)2); // BlockAlign
        writer.Write((short)16); // BitsPerSample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(0); // Subchunk2Size
        
        // Write silence data (2 seconds at 16kHz)
        int sampleRate = 16000;
        int durationMs = 2000;
        int numSamples = (sampleRate * durationMs) / 1000;
        for (int i = 0; i < numSamples; i++)
        {
            writer.Write((short)0);
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

    private void ChangeHotkey(AppConfig cfg)
    {
        using var cap = new HotkeyCaptureForm();
        if (cap.ShowDialog() != DialogResult.OK) return;
        cfg.Hotkey.Modifiers = cap.Modifiers;
        cfg.Hotkey.Key = cap.Key;
        _config.Save(cfg);
        _currentMods = cfg.Hotkey.Modifiers;
        _currentVk = cfg.Hotkey.Key;
        RegisterHotkey(_currentMods, _currentVk, REFINEMENT_HOTKEY_ID);
        NotificationService.ShowSuccess($"Hotkey updated to {cap.Display}");
    }

    private void ChangeTranscriberHotkey(AppConfig cfg)
    {
        using var cap = new HotkeyCaptureForm();
        if (cap.ShowDialog() != DialogResult.OK) return;
        cfg.TranscriberHotkey.Modifiers = cap.Modifiers;
        cfg.TranscriberHotkey.Key = cap.Key;
        _config.Save(cfg);
        _transcriberMods = cfg.TranscriberHotkey.Modifiers;
        _transcriberVk = cfg.TranscriberHotkey.Key;
        
        // Update hotkey registration
        try { UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID); } catch { }
        if (_currentConfig.Transcriber.Enabled)
        {
            RegisterHotkey(_transcriberMods, _transcriberVk, TRANSCRIBER_HOTKEY_ID);
        }
        
        NotificationService.ShowSuccess($"Transcriber hotkey updated to {cap.Display}");
    }

    private void ShowSettings(AppConfig cfg)
    {
        using var dlg = new SettingsForm(cfg);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _config.Save(_currentConfig);
            
            // Re-register transcriber hotkey if transcriber was enabled/disabled
            try { UnregisterHotKey(Handle, TRANSCRIBER_HOTKEY_ID); } catch { }
            if (_currentConfig.Transcriber.Enabled)
            {
                RegisterHotkey(_currentConfig.TranscriberHotkey.Modifiers, _currentConfig.TranscriberHotkey.Key, TRANSCRIBER_HOTKEY_ID);
            }
            
            NotificationService.ShowSuccess("Settings saved.");
        }
    }
}
