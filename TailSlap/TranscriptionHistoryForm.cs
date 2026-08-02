using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using TailSlap;

public sealed class TranscriptionHistoryForm : Form
{
    // Fields assigned from Build* helpers; null! is the definite-assignment contract.
    private ListView _list = null!;
    private TextBox _textBox = null!;
    private Label _statusLabel = null!;
    private Label _statusLamp = null!;
    private Label _headerSubtitle = null!;
    private Button _refreshButton = null!;
    private System.Windows.Forms.Timer? _refreshTimer;
    private DateTime _lastRefresh;
    private readonly IHistoryService _history;
    private TextBox _searchBox = null!;
    private List<(
        DateTime Timestamp,
        string Text,
        int RecordingDurationMs,
        string? Status,
        string? Error
    )> _allItems = new();
    private List<(
        DateTime Timestamp,
        string Text,
        int RecordingDurationMs,
        string? Status,
        string? Error
    )> _visibleItems = new();

    public TranscriptionHistoryForm(IHistoryService history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        Text = "Encrypted Transcription History";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(DpiHelper.Scale(920), DpiHelper.Scale(580));
        MinimumSize = new Size(DpiHelper.Scale(720), DpiHelper.Scale(460));
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = MainForm.LoadMainIcon();
        BackColor = UiTheme.Ground;
        Font = UiTheme.BodyFont;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = DpiHelper.Scale(new Padding(14)),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // header
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // search
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); // list
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); // viewer
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons + status

        root.Controls.Add(BuildHeader(), 0, 0);

        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search transcriptions…",
            Height = DpiHelper.Scale(28),
            Margin = new Padding(0, 0, 0, DpiHelper.Scale(8)),
        };
        _searchBox.TextChanged += (_, __) => ApplyFilter();
        root.Controls.Add(_searchBox, 0, 1);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            GridLines = false,
            ShowItemToolTips = true,
            Font = UiTheme.MonoFont,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Panel,
        };
        _list.Columns.Add("Time", DpiHelper.Scale(118));
        _list.Columns.Add("State", DpiHelper.Scale(64));
        _list.Columns.Add("Duration", DpiHelper.Scale(76));
        _list.Columns.Add("Text", -2);
        _list.Resize += (_, __) => UiTheme.FillLastListViewColumn(_list);
        UiTheme.FillLastListViewColumn(_list);
        _list.SelectedIndexChanged += (_, __) => SafeShowSelected();
        root.Controls.Add(_list, 0, 2);

        _textBox = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = UiTheme.Panel,
            ScrollBars = ScrollBars.Both,
            Font = UiTheme.MonoFont,
            BorderStyle = BorderStyle.FixedSingle,
        };
        root.Controls.Add(_textBox, 0, 3);

        root.Controls.Add(BuildBottomBar(), 0, 4);
        Controls.Add(root);

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
                RefreshHistory(true);
        };

        Load += (_, __) =>
        {
            SafePopulate();
            _lastRefresh = DateTime.Now;
            InitializeRefreshTimer();
        };

        Activated += (_, __) =>
        {
            if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(2))
                SafePopulate();
        };
    }

    private TableLayoutPanel BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, DpiHelper.Scale(10)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var logo = new PictureBox
        {
            Image = BrandedMessageBox.GetLogoBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Size = new Size(DpiHelper.Scale(40), DpiHelper.Scale(40)),
            Margin = new Padding(0, 0, DpiHelper.Scale(12), 0),
        };

        var titleBlock = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
        };
        var title = new Label
        {
            Text = "Transcription History",
            Font = UiTheme.TitleFont,
            ForeColor = UiTheme.Ink,
            AutoSize = true,
        };
        _headerSubtitle = new Label { AutoSize = true, ForeColor = UiTheme.Muted };

        titleBlock.Controls.Add(title, 0, 0);
        titleBlock.Controls.Add(_headerSubtitle, 0, 1);

        header.Controls.Add(logo, 0, 0);
        header.Controls.Add(titleBlock, 1, 0);
        return header;
    }

    private TableLayoutPanel BuildBottomBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = DpiHelper.Scale(new Padding(0, 6, 0, 0)),
        };
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = DpiHelper.Scale(new Padding(0, 6, 0, 0)),
        };
        var copy = new Button
        {
            Text = "Copy Text",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _refreshButton = new Button
        {
            Text = "Refresh (F5)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var clear = new Button
        {
            Text = "Clear History",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var export = new Button
        {
            Text = "Export…",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        UiTheme.StyleButton(copy, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(_refreshButton, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(clear, UiTheme.ButtonKind.Danger);
        UiTheme.StyleButton(export, UiTheme.ButtonKind.Secondary);

        copy.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(_textBox.Text);
                NotificationService.ShowSuccess("Transcription copied to clipboard.");
            }
            catch
            {
                NotificationService.ShowError("Failed to copy text.");
            }
        };
        _refreshButton.Click += (_, __) => RefreshHistory(true);
        clear.Click += (_, __) =>
        {
            try
            {
                if (
                    BrandedMessageBox.Show(
                        "Are you sure you want to delete all encrypted transcription history? This action is irreversible.",
                        "Confirm Delete",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    ) == DialogResult.Yes
                )
                {
                    _history.ClearTranscriptionHistory();
                    SafePopulate();
                    NotificationService.ShowSuccess("Encrypted transcription history cleared.");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning($"Clear encrypted history failed: {ex.Message}");
                }
                catch { }
                NotificationService.ShowError("Failed to clear encrypted history.");
            }
        };
        export.Click += (_, __) => ExportVisible();

        buttons.Controls.Add(copy);
        buttons.Controls.Add(export);
        buttons.Controls.Add(_refreshButton);
        buttons.Controls.Add(clear);
        bar.Controls.Add(buttons, 0, 0);

        var statusRow = UiTheme.StatusRow(out _statusLamp, out _statusLabel);
        bar.Controls.Add(statusRow, 0, 1);
        return bar;
    }

    private void SetStatus(string text, Color lamp)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor =
            lamp == UiTheme.SuccessText ? UiTheme.SuccessText
            : lamp == UiTheme.ErrorText ? UiTheme.ErrorText
            : lamp == UiTheme.WarnText ? UiTheme.WarnText
            : UiTheme.Muted;
        _statusLamp.BackColor = lamp;
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
            try
            {
                Logger.LogWarning($"Encrypted transcription populate failed: {ex.Message}");
            }
            catch { }
            SetStatus("Status: Error populating list", UiTheme.ErrorText);
        }
    }

    private void Populate()
    {
        _allItems = _history.ReadAllTranscriptions();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _searchBox?.Text ?? "";
        _visibleItems = _allItems.Where(e => HistoryQuery.Matches(query, e.Text, e.Error)).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        int corruptedCount = 0;
        int failedCount = 0;
        foreach (var (timestamp, text, duration, status, error) in _visibleItems)
        {
            bool isFailed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
            bool isCorrupt = !isFailed && string.IsNullOrEmpty(text);

            string stateText;
            Color stateColor;
            Color back;
            string preview;
            if (isFailed)
            {
                failedCount++;
                stateText = "FAIL";
                stateColor = UiTheme.ErrorText;
                back = UiTheme.ErrorBack;
                preview = Preview(string.IsNullOrWhiteSpace(error) ? "failed" : error);
            }
            else if (isCorrupt)
            {
                corruptedCount++;
                stateText = "CORRUPT";
                stateColor = UiTheme.WarnText;
                back = UiTheme.WarnBack;
                preview = "(decryption failed)";
            }
            else
            {
                stateText = "OK";
                stateColor = UiTheme.SuccessText;
                back = UiTheme.Panel;
                preview = Preview(text);
            }

            var item = new ListViewItem(timestamp.ToString("yyyy-MM-dd HH:mm"))
            {
                BackColor = back,
                ForeColor = UiTheme.Ink,
                UseItemStyleForSubItems = false,
                ToolTipText = $"{timestamp:yyyy-MM-dd HH:mm}  {status ?? "ok"}\n{preview}",
            };
            item.SubItems.Add(stateText);
            item.SubItems.Add($"{duration}ms");
            item.SubItems.Add(preview);

            item.SubItems[1].ForeColor = stateColor;
            item.SubItems[1].Font = UiTheme.MonoBoldFont;
            item.SubItems[2].ForeColor = UiTheme.Muted;
            item.SubItems[3].ForeColor = UiTheme.Ink;

            _list.Items.Add(item);
        }
        _list.EndUpdate();

        string suffix = $"{_visibleItems.Count}/{_allItems.Count} shown";
        if (failedCount > 0)
            suffix += $" — {failedCount} failed";
        if (corruptedCount > 0)
            suffix += $" — {corruptedCount} corrupted";

        var lamp =
            failedCount > 0 ? UiTheme.ErrorText
            : corruptedCount > 0 ? UiTheme.WarnText
            : UiTheme.SuccessText;
        SetStatus(
            $"Status: {suffix}" + (string.IsNullOrWhiteSpace(query) ? "" : " (filtered)"),
            lamp
        );

        _headerSubtitle.Text =
            $"{_allItems.Count} entr{(_allItems.Count == 1 ? "y" : "ies")} · encrypted (DPAPI)";
        _headerSubtitle.ForeColor = UiTheme.Muted;

        if (_list.Items.Count > 0)
            _list.SelectedIndices.Add(_list.Items.Count - 1);
        else
            _textBox.Clear();
    }

    private void ExportVisible()
    {
        try
        {
            if (_visibleItems.Count == 0)
            {
                NotificationService.ShowWarning("Nothing to export.");
                return;
            }

            if (
                BrandedMessageBox.Show(
                    "Export writes decrypted transcriptions as plaintext (not DPAPI-protected). Continue?",
                    "Confirm Export",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                ) != DialogResult.Yes
            )
            {
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title = "Export transcription history (plaintext)",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"tailslap-transcription-history-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            var content = HistoryQuery.FormatTranscriptionExport(DateTime.UtcNow, _visibleItems);
            File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
            NotificationService.ShowSuccess("History exported.");
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Transcription history export failed: {ex.GetType().Name}");
            }
            catch { }
            NotificationService.ShowError("Failed to export history.");
        }
    }

    private string Preview(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "(empty)";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        s = s.Replace("  ", " ");
        s = s.Trim();
        return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
    }

    private void SafeShowSelected()
    {
        try
        {
            int idx = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
            if (idx < 0 || idx >= _visibleItems.Count)
                return;

            var (timestamp, text, duration, status, error) = _visibleItems[idx];

            var cleanText = (text ?? "").Replace('\u00A0', ' ');
            var sb = new StringBuilder();
            sb.AppendLine($"Date: {timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Duration: {duration}ms");
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("Status: FAILED");
            sb.AppendLine(new string('-', 50));
            sb.AppendLine();
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"Error: {error ?? "unknown"}");
                sb.AppendLine();
                sb.AppendLine("Partial text:");
            }
            sb.AppendLine(cleanText);

            _textBox.Text = sb.ToString();

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Status: Failed entry — see error above", UiTheme.ErrorText);
            }
            else if (string.IsNullOrEmpty(text))
            {
                SetStatus("Status: Corrupted entry - decryption may have failed", UiTheme.WarnText);
            }
            else
            {
                SetStatus("Status: Decrypted successfully", UiTheme.SuccessText);
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Show encrypted transcription selected failed: {ex.Message}");
            }
            catch { }
            SetStatus($"Status: Error showing entry - {ex.Message}", UiTheme.ErrorText);
        }
    }

    private void CheckForNewEntries()
    {
        try
        {
            int currentCount = _history.ReadAllTranscriptions().Count;
            if (currentCount != _allItems.Count)
            {
                SafePopulate();
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"Encrypted transcription check for new failed: {ex.Message}");
            }
            catch { }
            SetStatus($"Status: Error checking updates - {ex.Message}", UiTheme.ErrorText);
        }
    }

    private void RefreshHistory(bool userInitiated = false)
    {
        try
        {
            if (userInitiated)
            {
                SafePopulate();
                SetStatus($"Status: Refreshed at {DateTime.Now:HH:mm:ss}", UiTheme.Muted);
            }
            _lastRefresh = DateTime.Now;
            _refreshButton.Text = $"Refresh (F5) - {_lastRefresh:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SetStatus($"Status: Error refreshing - {ex.Message}", UiTheme.ErrorText);
            try
            {
                Logger.LogWarning($"Encrypted transcription refresh failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError("Failed to refresh encrypted transcription history.");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
        }
        catch { }
        base.OnFormClosed(e);
    }
}
