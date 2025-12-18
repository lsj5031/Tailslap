using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public sealed class HistoryForm : Form
{
    private ListBox _list;
    private TextBox _orig;
    private TextBox _ref;
    private RichTextBox _diff;
    private TabControl _tabControl;
    private System.Windows.Forms.Timer? _refreshTimer;
    private DateTime _lastRefresh;
    private int _lastCount;
    private Button _refreshButton;
    private Label _statusLabel;

    public HistoryForm()
    {
        Text = "Encrypted Refinement History";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 950; Height = 650;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = MainForm.LoadMainIcon();
        
        InitializeRefreshTimer();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 300 };
        _list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        split.Panel1.Controls.Add(_list);

        _tabControl = new TabControl { Dock = DockStyle.Fill };
        _orig = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9), ReadOnly = true };
        _ref = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9), ReadOnly = true };
        _diff = new RichTextBox { Dock = DockStyle.Fill, ScrollBars = RichTextBoxScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9), ReadOnly = true, WordWrap = false };
        _tabControl.TabPages.Add(new TabPage("Original") { Controls = { _orig } });
        _tabControl.TabPages.Add(new TabPage("Refined") { Controls = { _ref } });
        _tabControl.TabPages.Add(new TabPage("Diff") { Controls = { _diff } });
        split.Panel2.Controls.Add(_tabControl);

        // Add status label
        _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 20, Text = "Status: Ready", ForeColor = Color.DarkGray, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0) };
        Controls.Add(_statusLabel);
        
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        var copyR = new Button { Text = "Copy Refined", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var copyO = new Button { Text = "Copy Original", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var copyD = new Button { Text = "Copy Diff", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _refreshButton = new Button { Text = "Refresh (F5)", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        
        copyR.Click += (_, __) => { try { Clipboard.SetText(_ref.Text); NotificationService.ShowSuccess("Refined text copied to clipboard."); } catch { NotificationService.ShowError("Failed to copy refined text."); } };
        copyO.Click += (_, __) => { try { Clipboard.SetText(_orig.Text); NotificationService.ShowSuccess("Original text copied to clipboard."); } catch { NotificationService.ShowError("Failed to copy original text."); } };
        copyD.Click += (_, __) => { try { Clipboard.SetText(_diff.Text); NotificationService.ShowSuccess("Diff text copied to clipboard."); } catch { NotificationService.ShowError("Failed to copy diff text."); } };
        _refreshButton.Click += (_, __) => RefreshHistory();
        
        buttons.Controls.Add(copyR); buttons.Controls.Add(copyO); buttons.Controls.Add(copyD); buttons.Controls.Add(_refreshButton);

        Controls.Add(split);
        Controls.Add(buttons);

        Load += (_, __) => { Populate(); _tabControl.SelectedIndex = 2; _lastRefresh = DateTime.Now; _lastCount = _list.Items.Count; };
        _list.SelectedIndexChanged += (_, __) => ShowSelected();
        
        // Add keyboard shortcut for refresh
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) RefreshHistory(); };
        
        // Refresh when form gains focus
        Activated += (_, __) => { if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(1)) RefreshHistory(); };
    }

    private void Populate()
    {
        try
        {
            var items = HistoryService.ReadAll();
            _list.Items.Clear();
            
            int corruptedCount = 0;
            foreach (var (timestamp, model, original, refined) in items)
            {
                string previewOriginal = Preview(original);
                string previewRefined = Preview(refined);
                
                // Detect if decryption failed (empty strings are either truly empty or failed decryption)
                if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(refined))
                {
                    corruptedCount++;
                    _list.Items.Add($"{timestamp:MM-dd HH:mm} [{model}] ⚠️ CORRUPTED ENTRY");
                }
                else
                {
                    _list.Items.Add($"{timestamp:MM-dd HH:mm} [{model}] {previewOriginal} -> {previewRefined}");
                }
            }
            
            if (corruptedCount > 0)
            {
                _statusLabel.Text = $"Status: Ready - {corruptedCount} corrupted (encrypted) entries detected";
                _statusLabel.ForeColor = Color.Orange;
            }
            else
            {
                _statusLabel.Text = "Status: Ready";
                _statusLabel.ForeColor = Color.DarkGray;
            }
            
            if (_list.Items.Count > 0) _list.SelectedIndex = _list.Items.Count - 1;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Status: Error - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            try { Logger.Log($"Encrypted history populate failed: {ex.Message}"); } catch { }
        }
    }

    private string Preview(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }

    private void ShowSelected()
    {
        try
        {
            var idx = _list.SelectedIndex;
            var all = HistoryService.ReadAll();
            if (idx < 0 || idx >= all.Count) return;
            
            var (timestamp, model, original, refined) = all[idx];
            
            _orig.Text = original;
            _ref.Text = refined;
            
            // Show decryption status
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(refined))
            {
                _statusLabel.Text = "Status: Encrypted entry - decryption may have failed";
                _statusLabel.ForeColor = Color.Orange;
            }
            else
            {
                _statusLabel.Text = "Status: Decrypted successfully";
                _statusLabel.ForeColor = Color.DarkGreen;
            }
            
            RenderColoredDiff(original, refined);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Status: Error showing entry - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            try { Logger.Log($"Show selected encrypted history failed: {ex.Message}"); } catch { }
        }
    }

    private void RenderColoredDiff(string a, string b)
    {
        var aLines = (a ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var bLines = (b ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        int n = Math.Min(aLines.Length, bLines.Length);
        
        _diff.Clear();
        _diff.SelectionStart = 0;
        
        for (int i = 0; i < n; i++)
        {
            if (aLines[i] == bLines[i])
            {
                _diff.SelectionColor = Color.Gray;
                _diff.SelectionBackColor = Color.White;
                _diff.AppendText("  " + aLines[i] + "\n");
            }
            else
            {
                RenderWordDiff(aLines[i], bLines[i]);
            }
        }
        
        for (int i = n; i < aLines.Length; i++)
        {
            _diff.SelectionColor = Color.FromArgb(220, 50, 50);
            _diff.SelectionBackColor = Color.FromArgb(255, 220, 220);
            _diff.AppendText("- " + aLines[i] + "\n");
        }
        
        for (int i = n; i < bLines.Length; i++)
        {
            _diff.SelectionColor = Color.FromArgb(40, 160, 40);
            _diff.SelectionBackColor = Color.FromArgb(220, 255, 220);
            _diff.AppendText("+ " + bLines[i] + "\n");
        }
        
        _diff.SelectionStart = 0;
        _diff.SelectionLength = 0;
    }

    private void RenderWordDiff(string oldLine, string newLine)
    {
        var oldWords = oldLine.Split(' ');
        var newWords = newLine.Split(' ');
        
        _diff.SelectionColor = Color.FromArgb(180, 50, 50);
        _diff.SelectionBackColor = Color.FromArgb(255, 235, 235);
        _diff.AppendText("- ");
        
        for (int i = 0; i < oldWords.Length; i++)
        {
            if (i < newWords.Length && oldWords[i] == newWords[i])
            {
                _diff.SelectionColor = Color.FromArgb(140, 140, 140);
                _diff.SelectionBackColor = Color.FromArgb(255, 245, 245);
            }
            else
            {
                _diff.SelectionColor = Color.FromArgb(200, 30, 30);
                _diff.SelectionBackColor = Color.FromArgb(255, 200, 200);
            }
            _diff.AppendText(oldWords[i] + (i < oldWords.Length - 1 ? " " : ""));
        }
        _diff.AppendText("\n");
        
        _diff.SelectionColor = Color.FromArgb(40, 140, 40);
        _diff.SelectionBackColor = Color.FromArgb(235, 255, 235);
        _diff.AppendText("+ ");
        
        for (int i = 0; i < newWords.Length; i++)
        {
            if (i < oldWords.Length && newWords[i] == oldWords[i])
            {
                _diff.SelectionColor = Color.FromArgb(140, 140, 140);
                _diff.SelectionBackColor = Color.FromArgb(245, 255, 245);
            }
            else
            {
                _diff.SelectionColor = Color.FromArgb(30, 160, 30);
                _diff.SelectionBackColor = Color.FromArgb(200, 255, 200);
            }
            _diff.AppendText(newWords[i] + (i < newWords.Length - 1 ? " " : ""));
        }
        _diff.AppendText("\n");
    }

    private void InitializeRefreshTimer()
    {
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 }; // 2.5 seconds
        _refreshTimer.Tick += (_, __) => CheckForNewEntries();
        _refreshTimer.Start();
    }

    private void CheckForNewEntries()
    {
        try
        {
            var currentItems = HistoryService.ReadAll();
            if (currentItems.Count != _lastCount)
            {
                RefreshHistory();
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Status: Error checking updates - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            try { Logger.Log($"Encrypted checkForNewEntries failed: {ex.Message}"); } catch { }
        }
    }

    private void RefreshHistory()
    {
        try
        {
            // Preserve current selection
            int selectedIndex = _list.SelectedIndex;
            string? selectedItem = selectedIndex >= 0 && selectedIndex < _list.Items.Count ? _list.Items[selectedIndex].ToString() : null;
            
            Populate();
            
            // Try to restore selection
            if (!string.IsNullOrEmpty(selectedItem))
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    if (_list.Items[i].ToString() == selectedItem)
                    {
                        _list.SelectedIndex = i;
                        break;
                    }
                }
            }
            
            _lastRefresh = DateTime.Now;
            _lastCount = _list.Items.Count;
            
            // Update button text to show last refresh
            _refreshButton.Text = $"Refresh (F5) - {_lastRefresh:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Status: Error refreshing - {ex.Message}";
            _statusLabel.ForeColor = Color.Red;
            try { Logger.Log($"Encrypted refresh history failed: {ex.Message}"); } catch { }
            NotificationService.ShowError("Failed to refresh encrypted history.");
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