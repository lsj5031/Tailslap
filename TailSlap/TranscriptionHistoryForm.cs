using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

public sealed class TranscriptionHistoryForm : Form
{
    private ListBox _list;
    private TextBox _textBox;
    private Label _statusLabel;
    private Button _refreshButton;
    private System.Windows.Forms.Timer? _refreshTimer;
    private DateTime _lastRefresh;

    public TranscriptionHistoryForm()
    {
        Text = "Encrypted Transcription History";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 900; Height = 550;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = MainForm.LoadMainIcon();

        _list = new ListBox { Dock = DockStyle.Top, HorizontalScrollbar = true, Height = 200 };
        _textBox = new TextBox { Multiline = true, Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window, ScrollBars = ScrollBars.Both };

        _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 20, Text = "Status: Ready", ForeColor = Color.DarkGray, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0) };
        
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var copy = new Button { Text = "Copy Text", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _refreshButton = new Button { Text = "Refresh (F5)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var clear = new Button { Text = "Clear History", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        copy.Click += (_, __) => { try { Clipboard.SetText(_textBox.Text); NotificationService.ShowSuccess("Transcription copied to clipboard."); } catch { NotificationService.ShowError("Failed to copy text."); } };
        _refreshButton.Click += (_, __) => RefreshHistory(true);
        clear.Click += (_, __) =>
        {
            try
            {
                if (MessageBox.Show("Are you sure you want to delete all encrypted transcription history? This action is irreversible.", "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    HistoryService.ClearAll();
                    SafePopulate();
                    NotificationService.ShowSuccess("Encrypted transcription history cleared.");
                }
            }
            catch (Exception ex)
            {
                try { Logger.Log($"Clear encrypted history failed: {ex.Message}"); } catch { }
                NotificationService.ShowError("Failed to clear encrypted history.");
            }
        };

        buttons.Controls.AddRange(new Control[] { copy, _refreshButton, clear });
        
        Controls.AddRange(new Control[] { _statusLabel, buttons, _textBox, _list });

        Load += (_, __) => { SafePopulate(); _lastRefresh = DateTime.Now; InitializeRefreshTimer(); };
        _list.SelectedIndexChanged += (_, __) => SafeShowSelected();

        Activated += (_, __) => { if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(2)) SafePopulate(); };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F5) RefreshHistory(true); };
    }

    private void InitializeRefreshTimer()
    {
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _refreshTimer.Tick += (_, __) => CheckForNewEntries();
        _refreshTimer.Start();
    }

    private void SafePopulate()
    {
        try
        {
            Populate();
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted transcription populate failed: {ex.Message}"); } catch { }
            _statusLabel.Text = "Status: Error populating list";
            _statusLabel.ForeColor = Color.Red;
        }
    }

    private void Populate()
    {
        var items = HistoryService.ReadAllTranscriptions();
        _list.BeginUpdate();
        _list.Items.Clear();
        
        int corruptedCount = 0;
        foreach (var (timestamp, text, duration) in items)
        {
            string preview = Preview(text);
            
            // Detect corrupted/encrypted entries
            if (string.IsNullOrEmpty(text))
            {
                corruptedCount++;
                _list.Items.Add($"{timestamp:yyyy-MM-dd HH:mm} ⚠️ CORRUPTED ({duration}ms)");
            }
            else
            {
                _list.Items.Add($"{timestamp:yyyy-MM-dd HH:mm} {preview} ({duration}ms)");
            }
        }
        _list.EndUpdate();

        if (corruptedCount > 0)
        {
            _statusLabel.Text = $"Status: {items.Count} total entries - {corruptedCount} corrupted (encrypted) entries detected";
            _statusLabel.ForeColor = Color.Orange;
        }
        else
        {
            _statusLabel.Text = $"Status: {items.Count} total entries";
            _statusLabel.ForeColor = Color.DarkGray;
        }

        if (_list.Items.Count > 0) _list.SelectedIndex = _list.Items.Count - 1;
    }

    private string Preview(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        s = s.Replace("  ", " ");
        s = s.Trim();
        return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
    }

    private void SafeShowSelected()
    {
        try
        {
            var items = HistoryService.ReadAllTranscriptions();
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= items.Count) return;
            
            var (timestamp, text, duration) = items[idx];
            
            // Replace NBSP with regular spaces for readability
            var cleanText = (text ?? "").Replace('\u00A0', ' ');
            var sb = new StringBuilder();
            sb.AppendLine($"Date: {timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Duration: {duration}ms");
            sb.AppendLine(new string('-', 50));
            sb.AppendLine();
            sb.AppendLine(cleanText);
            
            _textBox.Text = sb.ToString();
            
            if (string.IsNullOrEmpty(text))
            {
                _statusLabel.Text = "Status: Corrupted entry - decryption may have failed";
                _statusLabel.ForeColor = Color.Orange;
            }
            else
            {
                _statusLabel.Text = "Status: Decrypted successfully";
                _statusLabel.ForeColor = Color.DarkGreen;
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Show encrypted transcription selected failed: {ex.Message}"); } catch { }
            _statusLabel.Text = $"Status: Error showing entry - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
        }
    }

    private void CheckForNewEntries()
    {
        try
        {
            int currentCount = HistoryService.ReadAllTranscriptions().Count;
            if (currentCount != _list.Items.Count)
            {
                SafePopulate();
            }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"Encrypted transcription check for new failed: {ex.Message}"); } catch { }
            _statusLabel.Text = $"Status: Error checking updates - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
        }
    }

    private void RefreshHistory(bool userInitiated = false)
    {
        try
        {
            if (userInitiated) 
            { 
                SafePopulate(); 
                _statusLabel.Text = $"Status: Refreshed at {DateTime.Now:HH:mm:ss}";
                _statusLabel.ForeColor = Color.DarkGray;
            }
            _lastRefresh = DateTime.Now;
            _refreshButton.Text = $"Refresh (F5) - {_lastRefresh:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Status: Error refreshing - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            try { Logger.Log($"Encrypted transcription refresh failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to refresh encrypted transcription history.");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
            }
        }
        catch { }
        base.OnFormClosed(e);
    }
}