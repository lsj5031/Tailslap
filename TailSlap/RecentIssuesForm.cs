using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>
/// Displays the most recent error/warning entries read from the application log,
/// newest first, with copy and open-log-folder actions. Styled to match the rest
/// of the TailSlap UI: app icon, DPI-aware layout, monospace log text and
/// severity-tinted rows.
/// </summary>
public sealed class RecentIssuesForm : Form
{
    private readonly IReadOnlyList<LogEntry> _entries;
    private ListView _list = null!;
    private Label _headerSubtitle = null!;
    private Label _statusLamp = null!;
    private Label _statusLabel = null!;
    private Panel _emptyPanel = null!;

    public RecentIssuesForm(IReadOnlyList<LogEntry> issues)
    {
        _entries = issues ?? throw new ArgumentNullException(nameof(issues));

        Text = "TailSlap — Recent Errors & Warnings";
        Icon = MainForm.LoadMainIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(DpiHelper.Scale(960), DpiHelper.Scale(620));
        MinimumSize = new Size(DpiHelper.Scale(700), DpiHelper.Scale(440));
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.BodyFont;
        BackColor = UiTheme.Ground;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = DpiHelper.Scale(new Padding(14)),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        root.Controls.Add(BuildButtons(), 0, 2);
        root.Controls.Add(BuildStatus(), 0, 3);
        Controls.Add(root);

        Populate();
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
            Text = "Recent Errors & Warnings",
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

    private TableLayoutPanel BuildContent()
    {
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
        _list.Columns.Add("Time", DpiHelper.Scale(150));
        _list.Columns.Add("Level", DpiHelper.Scale(70));
        _list.Columns.Add("Source", DpiHelper.Scale(190));
        _list.Columns.Add("Message", -2);
        _list.Resize += (_, __) => UiTheme.FillLastListViewColumn(_list);
        UiTheme.FillLastListViewColumn(_list);

        _emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Panel };

        var emptyBlock = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Panel,
            Anchor = AnchorStyles.None,
        };
        var emptyLogo = new PictureBox
        {
            Image = BrandedMessageBox.GetLogoBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Size = new Size(DpiHelper.Scale(56), DpiHelper.Scale(56)),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, DpiHelper.Scale(8)),
        };
        var emptyTitle = new Label
        {
            Text = "No errors or warnings found",
            Font = UiTheme.TitleFont,
            ForeColor = UiTheme.Ink,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var emptySub = new Label
        {
            Text = "Your log is clean — nothing to worry about.",
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        emptyBlock.Controls.Add(emptyLogo, 0, 0);
        emptyBlock.Controls.Add(emptyTitle, 0, 1);
        emptyBlock.Controls.Add(emptySub, 0, 2);
        _emptyPanel.Controls.Add(emptyBlock);
        _emptyPanel.Resize += (_, __) => CenterChild(_emptyPanel, emptyBlock);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(0),
        };
        content.Controls.Add(_list, 0, 0);
        content.Controls.Add(_emptyPanel, 0, 0);
        return content;
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
            Padding = new Padding(0, DpiHelper.Scale(8), 0, 0),
        };
        var closeBtn = new Button
        {
            Text = "Close",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            DialogResult = DialogResult.Cancel,
        };
        var openBtn = new Button
        {
            Text = "Open Log Folder",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var copyBtn = new Button
        {
            Text = "Copy to Clipboard",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        UiTheme.StyleButton(closeBtn, UiTheme.ButtonKind.Primary);
        UiTheme.StyleButton(openBtn, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(copyBtn, UiTheme.ButtonKind.Secondary);
        openBtn.Click += (_, __) => OpenLogFolder();
        copyBtn.Click += (_, __) => CopyToClipboard();
        CancelButton = closeBtn;

        // Right-to-left flow: first added renders rightmost.
        panel.Controls.Add(closeBtn);
        panel.Controls.Add(openBtn);
        panel.Controls.Add(copyBtn);
        return panel;
    }

    private TableLayoutPanel BuildStatus()
    {
        return UiTheme.StatusRow(out _statusLamp, out _statusLabel);
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

    private void Populate()
    {
        if (_entries.Count == 0)
        {
            _list.Visible = false;
            _emptyPanel.Visible = true;
            _headerSubtitle.Text = "All clear — no errors or warnings logged.";
            _headerSubtitle.ForeColor = UiTheme.SuccessText;
            SetStatus("Status: No errors or warnings found", UiTheme.SuccessText);
            return;
        }

        _list.Visible = true;
        _emptyPanel.Visible = false;
        int errors = 0;
        int warns = 0;
        foreach (var entry in _entries)
        {
            bool isError = string.Equals(entry.Level, "error", StringComparison.OrdinalIgnoreCase);
            if (isError)
                errors++;
            else
                warns++;
        }
        _headerSubtitle.Text =
            $"{errors} error{(errors == 1 ? "" : "s")} · {warns} warning{(warns == 1 ? "" : "s")}"
            + $" — newest first (oldest shown {FormatTimestamp(_entries[^1].Ts)})";
        _headerSubtitle.ForeColor = UiTheme.Muted;
        SetStatus(
            $"Status: {_entries.Count} error/warning entr{(_entries.Count == 1 ? "y" : "ies")}"
                + " (newest first)",
            errors > 0 ? UiTheme.ErrorText : UiTheme.WarnText
        );

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _entries)
        {
            bool isError = string.Equals(entry.Level, "error", StringComparison.OrdinalIgnoreCase);
            var item = new ListViewItem(FormatTimestamp(entry.Ts))
            {
                BackColor = isError ? UiTheme.ErrorBack : UiTheme.WarnBack,
                ForeColor = UiTheme.Ink,
                UseItemStyleForSubItems = false,
                ToolTipText = BuildTooltip(entry),
            };
            item.SubItems.Add(isError ? "ERROR" : "WARN");
            item.SubItems.Add(entry.Source);
            item.SubItems.Add(OneLine(entry.Msg));

            // Colored severity badge in the Level column uses the cached mono font.
            item.SubItems[1].ForeColor = isError ? UiTheme.ErrorText : UiTheme.WarnText;
            item.SubItems[1].Font = UiTheme.MonoBoldFont;
            item.SubItems[2].ForeColor = UiTheme.Ink;
            item.SubItems[3].ForeColor = UiTheme.Ink;

            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private static void CenterChild(Control parent, Control child)
    {
        child.Location = new Point(
            Math.Max(0, (parent.ClientSize.Width - child.Width) / 2),
            Math.Max(0, (parent.ClientSize.Height - child.Height) / 2)
        );
    }

    private static string OneLine(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string BuildTooltip(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{entry.Ts}  [{entry.Level.ToUpperInvariant()}]  {entry.Source}");
        sb.AppendLine(entry.Msg);
        if (!string.IsNullOrWhiteSpace(entry.Err))
            sb.AppendLine($"detail: {entry.Err}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatTimestamp(string ts)
    {
        if (DateTime.TryParse(ts, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return ts;
    }

    private void CopyToClipboard()
    {
        try
        {
            if (_entries.Count == 0)
                return;

            var sb = new StringBuilder();
            foreach (var entry in _entries)
            {
                sb.AppendLine(
                    $"{FormatTimestamp(entry.Ts)}  [{entry.Level.ToUpperInvariant()}]  {entry.Source}"
                );
                sb.AppendLine($"    {entry.Msg}");
                if (!string.IsNullOrWhiteSpace(entry.Err))
                    sb.AppendLine($"    detail: {entry.Err}");
                sb.AppendLine();
            }

            Clipboard.SetText(sb.ToString().TrimEnd());
            NotificationService.ShowInfo("Errors & warnings copied to clipboard.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Copy issues failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Process.Start(
                new ProcessStartInfo(Logger.GetLogDirectory()) { UseShellExecute = true }
            );
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"Open log folder failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
    }
}
