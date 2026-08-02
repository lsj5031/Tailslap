using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>Severity of a single diagnostic check result.</summary>
public enum DiagnosticSeverity
{
    Success,
    Warning,
    Error,
    Info,
}

/// <summary>
/// One row of the diagnostics report. Rows sharing a <see cref="Section"/>
/// are grouped under a bold header in the report view.
/// </summary>
public sealed class DiagnosticRow
{
    public required string Section { get; init; }
    public required string Label { get; init; }
    public string? Value { get; init; }
    public string? Status { get; init; }
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Info;
    public bool Monospace { get; init; }
}

/// <summary>
/// Displays the TailSlap diagnostics report with severity-tinted rows and
/// grouped sections, styled to match the rest of the TailSlap UI: app icon,
/// DPI-aware layout, monospace values and an action button bar.
/// </summary>
public sealed class DiagnosticsForm : Form
{
    private readonly IReadOnlyList<DiagnosticRow> _rows;
    private readonly DateTime _runAt;
    private ListView _list = null!;
    private Label _headerSubtitle = null!;
    private Label _statusLamp = null!;
    private Label _statusLabel = null!;

    public DiagnosticsForm(IReadOnlyList<DiagnosticRow> rows, DateTime? runAt = null)
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _runAt = runAt ?? DateTime.Now;

        Text = "TailSlap Diagnostics";
        Icon = MainForm.LoadMainIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(DpiHelper.Scale(920), DpiHelper.Scale(600));
        MinimumSize = new Size(DpiHelper.Scale(680), DpiHelper.Scale(440));
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
            Text = "TailSlap Diagnostics",
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
            Font = UiTheme.BodyFont,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UiTheme.Panel,
        };
        _list.Columns.Add("Check", DpiHelper.Scale(230));
        _list.Columns.Add("Detail", DpiHelper.Scale(460));
        _list.Columns.Add("Result", -2);
        _list.Resize += (_, __) => UiTheme.FillLastListViewColumn(_list);
        UiTheme.FillLastListViewColumn(_list);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(0),
        };
        content.Controls.Add(_list, 0, 0);
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
            Text = "OK",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            DialogResult = DialogResult.OK,
        };
        var copyBtn = new Button
        {
            Text = "Copy to Clipboard",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        UiTheme.StyleButton(closeBtn, UiTheme.ButtonKind.Primary);
        UiTheme.StyleButton(copyBtn, UiTheme.ButtonKind.Secondary);
        copyBtn.Click += (_, __) => CopyToClipboard();
        CancelButton = closeBtn;

        // Right-to-left flow: first added renders rightmost.
        panel.Controls.Add(closeBtn);
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
        if (_rows.Count == 0)
        {
            _headerSubtitle.Text = "No diagnostics were collected.";
            SetStatus("Status: Nothing to report", UiTheme.Muted);
            return;
        }

        int errors = 0;
        int warnings = 0;
        int successes = 0;

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();

        var groups = new Dictionary<string, ListViewGroup>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            if (row.Severity == DiagnosticSeverity.Error)
                errors++;
            else if (row.Severity == DiagnosticSeverity.Warning)
                warnings++;
            else if (row.Severity == DiagnosticSeverity.Success)
                successes++;

            if (!groups.TryGetValue(row.Section, out var group))
            {
                group = new ListViewGroup(row.Section);
                _list.Groups.Add(group);
                groups[row.Section] = group;
            }

            var item = new ListViewItem(row.Label)
            {
                Group = group,
                UseItemStyleForSubItems = false,
            };

            var (back, text, statusText) = row.Severity switch
            {
                DiagnosticSeverity.Error => (
                    UiTheme.ErrorBack,
                    UiTheme.ErrorText,
                    "✗ " + (row.Status ?? "Failed")
                ),
                DiagnosticSeverity.Warning => (
                    UiTheme.WarnBack,
                    UiTheme.WarnText,
                    "⚠ " + (row.Status ?? "Warning")
                ),
                DiagnosticSeverity.Success => (
                    UiTheme.SuccessBack,
                    UiTheme.SuccessText,
                    "✓ " + (row.Status ?? "OK")
                ),
                _ => (UiTheme.Panel, UiTheme.Muted, row.Status ?? ""),
            };

            item.BackColor = back;
            item.ForeColor = UiTheme.Ink;

            var detail = new ListViewItem.ListViewSubItem(item, row.Value ?? "")
            {
                ForeColor = UiTheme.Ink,
                Font = row.Monospace ? UiTheme.MonoFont : UiTheme.BodyFont,
            };
            var result = new ListViewItem.ListViewSubItem(item, statusText)
            {
                ForeColor = text,
                Font = UiTheme.CapsFont,
            };

            item.SubItems.Add(detail);
            item.SubItems.Add(result);
            item.ToolTipText = BuildTooltip(row, statusText);

            _list.Items.Add(item);
        }
        _list.EndUpdate();

        var counts = new StringBuilder();
        counts.Append($"{_rows.Count} check{(_rows.Count == 1 ? "" : "s")}");
        if (successes > 0)
            counts.Append($" · {successes} OK");
        if (warnings > 0)
            counts.Append($" · {warnings} warning{(warnings == 1 ? "" : "s")}");
        if (errors > 0)
            counts.Append($" · {errors} error{(errors == 1 ? "" : "s")}");

        _headerSubtitle.Text = $"Ran {_runAt:yyyy-MM-dd HH:mm:ss} — {counts}";
        _headerSubtitle.ForeColor =
            errors > 0 ? UiTheme.ErrorText
            : warnings > 0 ? UiTheme.WarnText
            : UiTheme.SuccessText;

        SetStatus(
            errors > 0
                    ? $"Status: {errors} issue{(errors == 1 ? "" : "s")} found — check your endpoints"
                : warnings > 0
                    ? $"Status: {warnings} warning{(warnings == 1 ? "" : "s")} — mostly working"
                : "Status: All checks passed",
            errors > 0 ? UiTheme.ErrorText
                : warnings > 0 ? UiTheme.WarnText
                : UiTheme.SuccessText
        );
    }

    private static string BuildTooltip(DiagnosticRow row, string statusText)
    {
        var sb = new StringBuilder();
        sb.AppendLine(row.Section);
        sb.AppendLine($"{row.Label}: {row.Value ?? ""}");
        if (!string.IsNullOrWhiteSpace(statusText))
            sb.Append(statusText);
        return sb.ToString().TrimEnd();
    }

    private void CopyToClipboard()
    {
        try
        {
            if (_rows.Count == 0)
                return;

            var sb = new StringBuilder();
            string? lastSection = null;
            foreach (var row in _rows)
            {
                if (!string.Equals(row.Section, lastSection, StringComparison.Ordinal))
                {
                    sb.AppendLine();
                    sb.AppendLine(row.Section);
                    lastSection = row.Section;
                }
                var status = row.Severity switch
                {
                    DiagnosticSeverity.Error => "✗ " + (row.Status ?? "Failed"),
                    DiagnosticSeverity.Warning => "⚠ " + (row.Status ?? "Warning"),
                    DiagnosticSeverity.Success => "✓ " + (row.Status ?? "OK"),
                    _ => row.Status ?? "",
                };
                sb.AppendLine(
                    $"  {row.Label}: {row.Value ?? ""}{(string.IsNullOrWhiteSpace(status) ? "" : "  " + status)}"
                );
            }

            Clipboard.SetText(sb.ToString().TrimStart('\r', '\n'));
            NotificationService.ShowInfo("Diagnostics copied to clipboard.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                $"{nameof(DiagnosticsForm)} copy failed: {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
    }
}
