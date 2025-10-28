using System;
using System.Drawing;
using System.Windows.Forms;

public sealed class HotkeyCaptureForm : Form
{
    private readonly Label _prompt;
    private readonly TextBox _display;
    private readonly Label _hint;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly Button _clear;

    public uint Modifiers { get; private set; }
    public uint Key { get; private set; }
    public string Display { get; private set; } = string.Empty;

    public HotkeyCaptureForm()
    {
        Text = "Set Global Hotkey";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true; MinimizeBox = true;
        Width = 520; Height = 280;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(520, 300);
        SizeGripStyle = SizeGripStyle.Show;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // prompt
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // display
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // hint
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // spacer/fill
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // buttons

        _prompt = new Label 
        { 
            Text = "Press a keyboard shortcut to use as your global hotkey.\r\n" +
                   "Must include Ctrl, Alt, or Shift (e.g., Ctrl+Alt+R)",
            AutoSize = true, 
            Dock = DockStyle.Fill
        };
        layout.Controls.Add(_prompt, 0, 0);

        _display = new TextBox 
        { 
            ReadOnly = true, 
            Dock = DockStyle.Fill,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            BackColor = SystemColors.Window,
            Text = "Press a key combination...",
            Multiline = false
        };
        layout.Controls.Add(_display, 0, 1);

        _hint = new Label 
        { 
            Text = "Waiting for input...",
            AutoSize = true, 
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0)
        };
        layout.Controls.Add(_hint, 0, 2);

        // spacer to push buttons to bottom when resized taller
        layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        _ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Enabled = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _clear = new Button { Text = "Clear", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _clear.Click += (_, __) => ClearCapture();
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(_clear);

        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = _ok; CancelButton = _cancel;

        KeyDown += OnKeyDownCapture;
    }

    private void ClearCapture()
    {
        Modifiers = 0;
        Key = 0;
        Display = string.Empty;
        _display.Text = "Press a key combination...";
        _display.BackColor = SystemColors.Window;
        _hint.Text = "Waiting for input...";
        _hint.ForeColor = SystemColors.GrayText;
        _ok.Enabled = false;
    }

    private void OnKeyDownCapture(object? sender, KeyEventArgs e)
    {
        // Ignore pure modifier presses
        if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu)
        {
            e.SuppressKeyPress = true; return;
        }

        uint mods = 0;
        if (e.Control) mods |= 0x0002; // MOD_CONTROL
        if (e.Alt) mods |= 0x0001;     // MOD_ALT
        if (e.Shift) mods |= 0x0004;   // MOD_SHIFT

        Modifiers = mods;
        Key = (uint)e.KeyCode;
        Display = BuildDisplay(e.Control, e.Alt, e.Shift, e.KeyCode);
        _display.Text = Display;

        if (mods != 0 && Key != 0)
        {
            _display.BackColor = Color.LightGreen;
            _hint.Text = "✓ Valid hotkey! Click OK to save.";
            _hint.ForeColor = Color.Green;
            _ok.Enabled = true;
        }
        else
        {
            _display.BackColor = Color.LightCoral;
            _hint.Text = "⚠ Must include Ctrl, Alt, or Shift modifier key.";
            _hint.ForeColor = Color.Red;
            _ok.Enabled = false;
        }

        e.SuppressKeyPress = true;
    }

    private static string BuildDisplay(bool ctrl, bool alt, bool shift, Keys key)
    {
        System.Collections.Generic.List<string> parts = new();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}
