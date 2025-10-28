using System;
using System.Drawing;
using System.Diagnostics;
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
    private const int HOTKEY_ID = 1;

    private readonly ConfigService _config;
    private readonly ClipboardService _clip;
    private readonly CloudRefiner _refiner;
    private uint _currentMods;
    private uint _currentVk;

    public MainForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Visible = false;

        _config = new ConfigService();
        var cfg = _config.LoadOrDefault();
        _clip = new ClipboardService();
        _refiner = new CloudRefiner(cfg.Llm);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Refine Now", null, async (_, __) => await RefineSelectionAsync());
        var autoPasteItem = new ToolStripMenuItem("Auto Paste") { Checked = cfg.AutoPaste };
        autoPasteItem.Click += (_, __) => { cfg.AutoPaste = !cfg.AutoPaste; autoPasteItem.Checked = cfg.AutoPaste; _config.Save(cfg); };
        _menu.Items.Add(autoPasteItem);
        _menu.Items.Add("Change Hotkey", null, (_, __) => ChangeHotkey(cfg));
        _menu.Items.Add("Settings...", null, (_, __) => ShowSettings(cfg));
        _menu.Items.Add("Open Config...", null, (_, __) => { try { Process.Start("notepad", _config.GetConfigPath()); } catch { Notify("Failed to open config.", true); } });
        _menu.Items.Add("Open Logs...", null, (_, __) => { try { Process.Start("notepad", System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "TailslapCloud", "app.log")); } catch { Notify("Failed to open logs.", true); } });
        _menu.Items.Add("History...", null, (_, __) => { try { using var hf = new HistoryForm(); hf.ShowDialog(); } catch { Notify("Failed to open history.", true); } });
        var autoStartItem = new ToolStripMenuItem("Start with Windows") { Checked = AutoStartService.IsEnabled("TailslapCloud") };
        autoStartItem.Click += (_, __) => { AutoStartService.Toggle("TailslapCloud"); autoStartItem.Checked = AutoStartService.IsEnabled("TailslapCloud"); };
        _menu.Items.Add(autoStartItem);
        _menu.Items.Add("Quit", null, (_, __) => { Application.Exit(); });

        _idleIcon = LoadIdleIcon();
        _frames = LoadChewingFramesOrFallback();

        _tray = new NotifyIcon { Icon = _idleIcon, Visible = true, Text = "Tailslap Cloud" };
        _tray.ContextMenuStrip = _menu;
        _animTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _animTimer.Tick += (_, __) => { _tray.Icon = _frames[_frame++ % _frames.Length]; };
        _currentMods = cfg.Hotkey.Modifiers;
        _currentVk = cfg.Hotkey.Key;
        try { Logger.Log($"MainForm initialized. Planned hotkey mods={_currentMods}, key={_currentVk}"); } catch { }
    }

    private Icon[] LoadChewingFramesOrFallback()
    {
        try
        {
            var list = new System.Collections.Generic.List<Icon>(4);
            // Prefer icons shipped next to the exe: .\Icons\Chewing1-4.ico
            string baseDir = Application.StartupPath;
            string iconsDir = System.IO.Path.Combine(baseDir, "Icons");
            for (int i = 1; i <= 4; i++)
            {
                string p = System.IO.Path.Combine(iconsDir, $"Chewing{i}.ico");
                if (!System.IO.File.Exists(p)) p = System.IO.Path.Combine(iconsDir, $"chewing{i}.ico");
                if (System.IO.File.Exists(p)) { try { list.Add(new Icon(p)); } catch { } }
            }
            if (list.Count > 0) return list.ToArray();
        }
        catch { }
        // Fallback to idle icon to ensure at least one frame exists
        return new[] { _idleIcon };
    }

    private Icon LoadIdleIcon()
    {
        try
        {
            string iconsDir = System.IO.Path.Combine(Application.StartupPath, "Icons");
            string p = System.IO.Path.Combine(iconsDir, "Chewing1.ico");
            if (!System.IO.File.Exists(p)) p = System.IO.Path.Combine(iconsDir, "chewing1.ico");
            if (System.IO.File.Exists(p)) return new Icon(p);
        }
        catch { }
        return SystemIcons.Application;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey(_currentMods, _currentVk);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
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
            try { Logger.Log("WM_HOTKEY received"); } catch { }
            _ = RefineSelectionAsync();
        }
        base.WndProc(ref m);
    }

    private async Task RefineSelectionAsync()
    {
        try
        {
            StartAnim();
            var cfg = _config.LoadOrDefault();
            try { Logger.Log("Starting capture from selection/clipboard"); } catch { }
            var text = await _clip.CaptureSelectionOrClipboardAsync(cfg.UseClipboardFallback);
            try { Logger.Log($"Captured length: {text?.Length ?? 0}, sha256={Sha256Hex(text ?? string.Empty)}"); } catch { }
            if (string.IsNullOrWhiteSpace(text)) { Notify("No text selected or in clipboard.", true); return; }
            var refined = await _refiner.RefineAsync(text);
            try { Logger.Log($"Refined length: {refined?.Length ?? 0}, sha256={Sha256Hex(refined ?? string.Empty)}"); } catch { }
            if (string.IsNullOrWhiteSpace(refined)) { Notify("Provider returned empty result.", true); return; }
            _clip.SetText(refined);
            await Task.Delay(100);
            if (cfg.AutoPaste) { try { Logger.Log("Auto-paste attempt"); } catch { } _clip.Paste(); }
            try { HistoryService.Append(text, refined, cfg.Llm.Model); } catch { }
            Notify("Refinement complete.");
            Logger.Log("Refinement completed successfully.");
        }
        catch (Exception ex) 
        { 
            Notify("LLM request failed: " + ex.Message, true); 
            Logger.Log("Error: " + ex.Message); 
        }
        finally { StopAnim(); }
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

    private void StartAnim() => _animTimer.Start();
    private void StopAnim() { _animTimer.Stop(); _tray.Icon = _idleIcon; }
    private void Notify(string msg, bool error = false) => _tray.ShowBalloonTip(2000, "Tailslap", msg, error ? ToolTipIcon.Error : ToolTipIcon.Info);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void RegisterHotkey(uint mods, uint vk)
    {
        try { if (Handle != IntPtr.Zero) UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
        if (mods == 0) mods = 0x0003;
        if (vk == 0) vk = (uint)Keys.R;
        var ok = RegisterHotKey(Handle, HOTKEY_ID, mods, vk);
        try { Logger.Log($"RegisterHotKey mods={mods}, key={vk}, ok={ok}"); } catch { }
        if (!ok) Notify("Failed to register hotkey.", true);
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
        RegisterHotkey(_currentMods, _currentVk);
        Notify($"Hotkey updated to {cap.Display}");
    }

    private void ShowSettings(AppConfig cfg)
    {
        using var dlg = new SettingsForm(cfg);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _config.Save(cfg);
            Notify("Settings saved.");
        }
    }
}
