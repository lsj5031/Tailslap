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

    public HistoryForm()
    {
        Text = "Refinement History";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 950; Height = 620;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = MainForm.LoadMainIcon();

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

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        var copyR = new Button { Text = "Copy Refined", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var copyO = new Button { Text = "Copy Original", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var copyD = new Button { Text = "Copy Diff", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        copyR.Click += (_, __) => { try { Clipboard.SetText(_ref.Text); } catch { } };
        copyO.Click += (_, __) => { try { Clipboard.SetText(_orig.Text); } catch { } };
        copyD.Click += (_, __) => { try { Clipboard.SetText(_diff.Text); } catch { } };
        buttons.Controls.Add(copyR); buttons.Controls.Add(copyO); buttons.Controls.Add(copyD);

        Controls.Add(split);
        Controls.Add(buttons);

        Load += (_, __) => { Populate(); _tabControl.SelectedIndex = 2; };
        _list.SelectedIndexChanged += (_, __) => ShowSelected();
    }

    private void Populate()
    {
        var items = HistoryService.ReadAll();
        _list.Items.Clear();
        foreach (var e in items)
        {
            _list.Items.Add($"{e.Timestamp:MM-dd HH:mm} [{e.Model}] {Preview(e.Original)} -> {Preview(e.Refined)}");
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = _list.Items.Count - 1;
    }

    private string Preview(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }

    private void ShowSelected()
    {
        var idx = _list.SelectedIndex;
        var all = HistoryService.ReadAll();
        if (idx < 0 || idx >= all.Count) return;
        var e = all[idx];
        _orig.Text = e.Original;
        _ref.Text = e.Refined;
        RenderColoredDiff(e.Original, e.Refined);
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
}
