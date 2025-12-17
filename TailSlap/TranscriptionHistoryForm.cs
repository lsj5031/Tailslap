using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

public sealed class TranscriptionHistoryForm : Form
{
    private ListBox _list;
    private TextBox _textBox;
    private System.Windows.Forms.Timer? _refreshTimer;
    private FileSystemWatcher? _fileWatcher;
    private DateTime _lastRefresh;
    private int _lastCount;
    private Button _refreshButton;
    private Button _clearButton;
    private Button _copyButton;

    public TranscriptionHistoryForm()
    {
        Text = "Transcription History";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 800; Height = 500;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = MainForm.LoadMainIcon();

        InitializeRefreshTimer();
        InitializeFileWatcher();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 280 };
        
        _list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        split.Panel1.Controls.Add(_list);

        _textBox = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9), ReadOnly = true };
        split.Panel2.Controls.Add(_textBox);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false
        };
        
        _refreshButton = new Button { Text = "Refresh (F5)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _clearButton = new Button { Text = "Clear History", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _copyButton = new Button { Text = "Copy Text", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        
        _refreshButton.Click += (_, __) => RefreshHistory();
        _clearButton.Click += ClearHistory;
        _copyButton.Click += (_, __) => { try { if (!string.IsNullOrEmpty(_textBox.Text)) { Clipboard.SetText(_textBox.Text); NotificationService.ShowSuccess("Text copied."); } } catch { } };
        
        buttons.Controls.Add(_refreshButton);
        buttons.Controls.Add(_copyButton);
        buttons.Controls.Add(_clearButton);

        Controls.Add(split);
        Controls.Add(buttons);

        Load += (_, __) => { Populate(); _lastRefresh = DateTime.Now; _lastCount = _list.Items.Count; };
        _list.SelectedIndexChanged += (_, __) => ShowSelected();

        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) RefreshHistory(); };
        Activated += (_, __) => { if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(1)) RefreshHistory(); };
    }

    private void Populate()
    {
        var items = HistoryService.ReadAllTranscriptions();
        _list.Items.Clear();
        foreach (var e in items)
        {
            var durationStr = e.RecordingDurationMs > 0 ? $"{e.RecordingDurationMs / 1000.0:F1}s" : "-";
            var preview = e.Text.Length > 50 ? e.Text.Substring(0, 50).Replace('\n', ' ') + "..." : e.Text.Replace('\n', ' ');
            _list.Items.Add($"{e.Timestamp:MM-dd HH:mm} [{durationStr}] {preview}");
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = _list.Items.Count - 1;
    }

    private void ShowSelected()
    {
        var idx = _list.SelectedIndex;
        var all = HistoryService.ReadAllTranscriptions();
        if (idx < 0 || idx >= all.Count) return;
        _textBox.Text = all[idx].Text;
    }

    private void ClearHistory(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to clear all transcription history?",
            "Clear Transcription History",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailSlap", "transcription-history.jsonl");
                if (File.Exists(path)) File.Delete(path);
                Populate();
                NotificationService.ShowSuccess("Transcription history cleared.");
            }
            catch (Exception ex)
            {
                NotificationService.ShowError($"Failed to clear history: {ex.Message}");
            }
        }
    }

    private void InitializeRefreshTimer()
    {
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _refreshTimer.Tick += (_, __) => CheckForNewEntries();
        _refreshTimer.Start();
    }

    private void InitializeFileWatcher()
    {
        try
        {
            string historyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TailSlap");
            if (Directory.Exists(historyDir))
            {
                _fileWatcher = new FileSystemWatcher(historyDir, "transcription-history.jsonl")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _fileWatcher.Changed += (_, __) =>
                {
                    if (InvokeRequired) Invoke(new Action(CheckForNewEntries));
                    else CheckForNewEntries();
                };
                _fileWatcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"TranscriptionHistory FileWatcher init failed: {ex.Message}"); } catch { }
        }
    }

    private void CheckForNewEntries()
    {
        try
        {
            var currentItems = HistoryService.ReadAllTranscriptions();
            if (currentItems.Count != _lastCount) RefreshHistory();
        }
        catch { }
    }

    private void RefreshHistory()
    {
        try
        {
            Populate();
            _lastRefresh = DateTime.Now;
            _lastCount = _list.Items.Count;
            _refreshButton.Text = $"Refresh (F5) - {_lastRefresh:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Failed to refresh: {ex.Message}");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            _fileWatcher?.Dispose();
        }
        catch { }
        base.OnFormClosed(e);
    }
}
