using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using TailSlap;

public sealed class HistoryForm : Form
{
    // Fields assigned from Build* helpers; null! is the definite-assignment contract.
    private ListView _list = null!;
    private TextBox _orig = null!;
    private TextBox _ref = null!;
    private RichTextBox _diff = null!;
    private TabControl _tabControl = null!;
    private System.Windows.Forms.Timer? _refreshTimer;
    private DateTime _lastRefresh;
    private Button _refreshButton = null!;
    private Label _statusLabel = null!;
    private Label _statusLamp = null!;
    private TextBox _searchBox = null!;
    private Label _headerSubtitle = null!;
    private readonly IHistoryService _history;
    private List<(
        DateTime Timestamp,
        string Model,
        string Original,
        string Refined,
        string? Status,
        string? Error
    )> _allItems = new();
    private List<(
        DateTime Timestamp,
        string Model,
        string Original,
        string Refined,
        string? Status,
        string? Error
    )> _visibleItems = new();

    public HistoryForm(IHistoryService history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        Text = "Encrypted Refinement History";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(DpiHelper.Scale(980), DpiHelper.Scale(660));
        MinimumSize = new Size(DpiHelper.Scale(760), DpiHelper.Scale(500));
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
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // split
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status

        root.Controls.Add(BuildHeader(), 0, 0);
        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search original, refined, or model…",
            Height = DpiHelper.Scale(28),
            Margin = new Padding(0, 0, 0, DpiHelper.Scale(8)),
        };
        _searchBox.TextChanged += (_, __) => ApplyFilter();
        root.Controls.Add(_searchBox, 0, 1);
        root.Controls.Add(BuildContent(), 0, 2);
        root.Controls.Add(BuildButtons(), 0, 3);
        root.Controls.Add(BuildStatus(), 0, 4);
        Controls.Add(root);

        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.F5)
                RefreshHistory();
        };

        Load += (_, __) =>
        {
            Populate();
            _tabControl.SelectedIndex = 2;
            _lastRefresh = DateTime.Now;
        };

        InitializeRefreshTimer();

        Activated += (_, __) =>
        {
            if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(1))
                RefreshHistory();
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
            Text = "Refinement History",
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

    private SplitContainer BuildContent()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = DpiHelper.Scale(380),
            SplitterWidth = DpiHelper.Scale(5),
        };

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
        _list.Columns.Add("Model", DpiHelper.Scale(150));
        _list.Columns.Add("Original → Refined", -2);
        _list.Resize += (_, __) => UiTheme.FillLastListViewColumn(_list);
        UiTheme.FillLastListViewColumn(_list);
        split.Panel1.Controls.Add(_list);

        _tabControl = new TabControl { Dock = DockStyle.Fill };
        _orig = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            Font = UiTheme.MonoFont,
            ReadOnly = true,
            BackColor = UiTheme.Panel,
        };
        _ref = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            Font = UiTheme.MonoFont,
            ReadOnly = true,
            BackColor = UiTheme.Panel,
        };
        _diff = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Both,
            Font = UiTheme.MonoFont,
            ReadOnly = true,
            WordWrap = false,
            BackColor = UiTheme.Panel,
        };
        _tabControl.TabPages.Add(new TabPage("Original") { Controls = { _orig } });
        _tabControl.TabPages.Add(new TabPage("Refined") { Controls = { _ref } });
        _tabControl.TabPages.Add(new TabPage("Diff") { Controls = { _diff } });
        split.Panel2.Controls.Add(_tabControl);

        _list.SelectedIndexChanged += (_, __) => ShowSelected();
        return split;
    }

    private FlowLayoutPanel BuildButtons()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = DpiHelper.Scale(new Padding(0, 6, 0, 0)),
            Padding = DpiHelper.Scale(new Padding(0, 6, 0, 0)),
        };
        var copyR = new Button
        {
            Text = "Copy Refined",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var copyO = new Button
        {
            Text = "Copy Original",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var copyD = new Button
        {
            Text = "Copy Diff",
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

        UiTheme.StyleButton(copyR, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(copyO, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(copyD, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(_refreshButton, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(clear, UiTheme.ButtonKind.Danger);
        UiTheme.StyleButton(export, UiTheme.ButtonKind.Secondary);

        copyR.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(_ref.Text);
                NotificationService.ShowSuccess("Refined text copied to clipboard.");
            }
            catch
            {
                NotificationService.ShowError("Failed to copy refined text.");
            }
        };
        copyO.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(_orig.Text);
                NotificationService.ShowSuccess("Original text copied to clipboard.");
            }
            catch
            {
                NotificationService.ShowError("Failed to copy original text.");
            }
        };
        copyD.Click += (_, __) =>
        {
            try
            {
                Clipboard.SetText(_diff.Text);
                NotificationService.ShowSuccess("Diff text copied to clipboard.");
            }
            catch
            {
                NotificationService.ShowError("Failed to copy diff text.");
            }
        };
        _refreshButton.Click += (_, __) => RefreshHistory();
        clear.Click += (_, __) =>
        {
            try
            {
                if (
                    BrandedMessageBox.Show(
                        "Are you sure you want to delete all encrypted refinement history? This action is irreversible.",
                        "Confirm Delete",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    ) == DialogResult.Yes
                )
                {
                    _history.ClearRefinementHistory();
                    Populate();
                    _orig.Clear();
                    _ref.Clear();
                    _diff.Clear();
                    NotificationService.ShowSuccess("Encrypted refinement history cleared.");
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

        // Right-to-left flow: first added renders rightmost.
        panel.Controls.Add(copyR);
        panel.Controls.Add(copyO);
        panel.Controls.Add(copyD);
        panel.Controls.Add(export);
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(clear);
        return panel;
    }

    private TableLayoutPanel BuildStatus() => UiTheme.StatusRow(out _statusLamp, out _statusLabel);

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

    private void Populate()
    {
        try
        {
            _allItems = _history.ReadAll();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            SetStatus($"Status: Error - {ex.Message}", UiTheme.ErrorText);
            try
            {
                Logger.LogWarning($"Encrypted history populate failed: {ex.Message}");
            }
            catch { }
        }
    }

    private void ApplyFilter()
    {
        var query = _searchBox?.Text ?? "";
        _visibleItems = _allItems
            .Where(e => HistoryQuery.Matches(query, e.Original, e.Refined, e.Model, e.Error))
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        int corruptedCount = 0;
        int failedCount = 0;
        foreach (var (timestamp, model, original, refined, status, error) in _visibleItems)
        {
            bool isFailed = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
            bool isCorrupt =
                !isFailed && (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(refined));

            string preview;
            string stateText;
            Color back;
            Color stateColor;
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
                preview = Preview(original ?? refined ?? "");
            }
            else
            {
                stateText = "OK";
                stateColor = UiTheme.SuccessText;
                back = UiTheme.Panel;
                preview = Preview(original) + " → " + Preview(refined);
            }

            var item = new ListViewItem(timestamp.ToString("MM-dd HH:mm"))
            {
                BackColor = back,
                ForeColor = UiTheme.Ink,
                UseItemStyleForSubItems = false,
                Tag = timestamp,
                ToolTipText = $"{timestamp:yyyy-MM-dd HH:mm}  [{model}]  {status ?? "ok"}",
            };
            item.SubItems.Add(stateText);
            item.SubItems.Add(model);
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
        {
            _orig.Clear();
            _ref.Clear();
            _diff.Clear();
        }
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
                    "Export writes decrypted history as plaintext (not DPAPI-protected). Continue?",
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
                Title = "Export refinement history (plaintext)",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"tailslap-refinement-history-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            var content = HistoryQuery.FormatRefinementExport(DateTime.UtcNow, _visibleItems);
            File.WriteAllText(dlg.FileName, content, Encoding.UTF8);
            NotificationService.ShowSuccess("History exported.");
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"History export failed: {ex.GetType().Name}");
            }
            catch { }
            NotificationService.ShowError("Failed to export history.");
        }
    }

    private string Preview(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }

    private void ShowSelected()
    {
        try
        {
            var idx = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
            if (idx < 0 || idx >= _visibleItems.Count)
                return;

            var (timestamp, model, original, refined, status, error) = _visibleItems[idx];

            _orig.Text = original;
            _ref.Text = refined;

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Status: Failed entry — see error", UiTheme.ErrorText);
                _diff.Clear();
                _diff.SelectionColor = UiTheme.ErrorText;
                _diff.AppendText($"ERROR: {error ?? "unknown"}\n\n");
                if (!string.IsNullOrWhiteSpace(original))
                {
                    _diff.AppendText("ORIGINAL:\n" + original + "\n");
                }
                return;
            }
            else if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(refined))
            {
                SetStatus("Status: Encrypted entry - decryption may have failed", UiTheme.WarnText);
            }
            else
            {
                SetStatus("Status: Decrypted successfully", UiTheme.SuccessText);
            }

            RenderColoredDiff(original, refined);
        }
        catch (Exception ex)
        {
            SetStatus($"Status: Error showing entry - {ex.Message}", UiTheme.ErrorText);
            try
            {
                Logger.LogWarning($"Show selected encrypted history failed: {ex.Message}");
            }
            catch { }
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
            var currentCount = _history.ReadAll().Count;
            if (currentCount != _allItems.Count)
            {
                RefreshHistory();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Status: Error checking updates - {ex.Message}", UiTheme.ErrorText);
            try
            {
                Logger.LogWarning($"Encrypted checkForNewEntries failed: {ex.Message}");
            }
            catch { }
        }
    }

    private void RefreshHistory()
    {
        try
        {
            DateTime? selectedTs =
                _list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] < _visibleItems.Count
                    ? _visibleItems[_list.SelectedIndices[0]].Timestamp
                    : null;

            Populate();

            if (selectedTs.HasValue)
            {
                for (int i = 0; i < _visibleItems.Count; i++)
                {
                    if (_visibleItems[i].Timestamp == selectedTs.Value)
                    {
                        _list.SelectedIndices.Clear();
                        _list.SelectedIndices.Add(i);
                        break;
                    }
                }
            }

            _lastRefresh = DateTime.Now;

            _refreshButton.Text = $"Refresh (F5) - {_lastRefresh:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SetStatus($"Status: Error refreshing - {ex.Message}", UiTheme.ErrorText);
            try
            {
                Logger.LogWarning($"Encrypted refresh history failed: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError("Failed to refresh encrypted history.");
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
