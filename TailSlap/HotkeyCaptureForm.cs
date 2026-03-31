using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class HotkeyCaptureForm : Form
{
    private readonly Func<uint, uint, string?>? _validator;
    private readonly Label _prompt;
    private readonly TextBox _display;
    private readonly Label _hint;
    private readonly Button _ok;
    private readonly Button _cancel;
    private readonly Button _clear;

    public uint Modifiers { get; private set; }
    public uint Key { get; private set; }
    public string Display { get; private set; } = string.Empty;

    public HotkeyCaptureForm(Func<uint, uint, string?>? validator = null, string? promptText = null)
    {
        _validator = validator;
        Text = "Set Global Hotkey";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        Width = DpiHelper.Scale(560);
        Height = DpiHelper.Scale(340);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        AutoSize = false;
        MinimumSize = new Size(DpiHelper.Scale(560), DpiHelper.Scale(360));
        SizeGripStyle = SizeGripStyle.Show;
        TopMost = true;
        Icon = MainForm.LoadMainIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = DpiHelper.Scale(new Padding(16)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // prompt
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // display
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // hint
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // spacer/fill
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

        _prompt = new Label
        {
            Text =
                promptText
                ?? "Press a keyboard shortcut to use as your global hotkey.\r\n"
                    + "Must include Ctrl, Alt, Shift, or Win plus a non-modifier key (e.g., Ctrl+Alt+R, Win+T)",
            AutoSize = true,
            MaximumSize = new Size(DpiHelper.Scale(720), 0),
            Dock = DockStyle.Fill,
            UseCompatibleTextRendering = true,
            Margin = DpiHelper.Scale(new Padding(0, 0, 0, 6)),
        };
        layout.Controls.Add(_prompt, 0, 0);

        _display = new TextBox
        {
            ReadOnly = true,
            Dock = DockStyle.Fill,
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", DpiHelper.ScaleFont(14), FontStyle.Bold),
            BackColor = SystemColors.Window,
            Text = "Press a key combination...",
            Multiline = false,
        };
        layout.Controls.Add(_display, 0, 1);

        _hint = new Label
        {
            Text = "Waiting for input...",
            AutoSize = true,
            MaximumSize = new Size(DpiHelper.Scale(720), 0),
            Dock = DockStyle.Fill,
            UseCompatibleTextRendering = true,
            ForeColor = SystemColors.GrayText,
            Margin = DpiHelper.Scale(new Padding(0, 4, 0, 6)),
        };
        layout.Controls.Add(_hint, 0, 2);

        // spacer to push buttons to bottom when resized taller
        layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = DpiHelper.Scale(new Padding(10)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        _ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Enabled = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _clear = new Button
        {
            Text = "Clear",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _clear.Click += (_, __) => ClearCapture();
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(_clear);

        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = _cancel;

        KeyDown += OnKeyDownCapture;
        Shown += (_, __) => AdjustSizeToContent(layout);
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
        if (
            e.KeyCode == Keys.ControlKey
            || e.KeyCode == Keys.ShiftKey
            || e.KeyCode == Keys.Menu
            || e.KeyCode == Keys.LWin
            || e.KeyCode == Keys.RWin
        )
        {
            e.SuppressKeyPress = true;
            return;
        }

        uint mods = 0;
        if (e.Control)
            mods |= 0x0002; // MOD_CONTROL
        if (e.Alt)
            mods |= 0x0001; // MOD_ALT
        if (e.Shift)
            mods |= 0x0004; // MOD_SHIFT
        bool winDown = IsWinDown();
        if (winDown)
            mods |= 0x0008; // MOD_WIN

        Modifiers = mods;
        Key = (uint)e.KeyCode;
        Display = BuildDisplay(e.Control, e.Alt, e.Shift, winDown, e.KeyCode);
        _display.Text = Display;

        if (mods == 0 || Key == 0)
        {
            SetValidationState(
                isValid: false,
                hint: "Must include Ctrl, Alt, Shift, or Win and a non-modifier key."
            );
        }
        else
        {
            var validationError = _validator?.Invoke(mods, Key);
            if (string.IsNullOrWhiteSpace(validationError))
            {
                SetValidationState(isValid: true, hint: "Available hotkey. Click OK to save.");
            }
            else
            {
                SetValidationState(isValid: false, hint: validationError);
            }
        }

        e.SuppressKeyPress = true;
    }

    private void SetValidationState(bool isValid, string hint)
    {
        _display.BackColor = isValid ? Color.LightGreen : Color.LightCoral;
        _hint.Text = isValid ? $"✓ {hint}" : $"⚠ {hint}";
        _hint.ForeColor = isValid ? Color.Green : Color.Red;
        _ok.Enabled = isValid;
    }

    private static string BuildDisplay(bool ctrl, bool alt, bool shift, bool win, Keys key)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (ctrl)
            parts.Add("Ctrl");
        if (alt)
            parts.Add("Alt");
        if (shift)
            parts.Add("Shift");
        if (win)
            parts.Add("Win");

        string keyName = key.ToString();
        if (keyName.StartsWith("D") && keyName.Length == 2 && char.IsDigit(keyName[1]))
            keyName = keyName.Substring(1);
        else if (keyName == "OemSemicolon" || keyName == "Oem1")
            keyName = ";";
        else if (keyName == "OemQuestion" || keyName == "Oem2")
            keyName = "?";
        else if (keyName == "OemTilde" || keyName == "Oem3")
            keyName = "~";
        else if (keyName == "OemOpenBrackets" || keyName == "Oem4")
            keyName = "[";
        else if (keyName == "OemPipe" || keyName == "Oem5")
            keyName = "|";
        else if (keyName == "OemCloseBrackets" || keyName == "Oem6")
            keyName = "]";
        else if (keyName == "OemQuotes" || keyName == "Oem7")
            keyName = "'";

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private static bool IsWinDown()
    {
        try
        {
            return ((GetKeyState(VK_LWIN) & 0x8000) != 0) || ((GetKeyState(VK_RWIN) & 0x8000) != 0);
        }
        catch
        {
            return false;
        }
    }

    private void AdjustSizeToContent(Control layout)
    {
        try
        {
            // Constrain labels to current width so they wrap correctly
            int wrapW = Math.Max(DpiHelper.Scale(300), ClientSize.Width - DpiHelper.Scale(40));
            _prompt.MaximumSize = new Size(wrapW, 0);
            _hint.MaximumSize = new Size(wrapW, 0);
            layout.PerformLayout();
            var pref = layout.PreferredSize;
            int pad = DpiHelper.Scale(24);
            var wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(
                wa.Width - DpiHelper.Scale(80),
                Math.Max(MinimumSize.Width, pref.Width + pad)
            );
            int h = Math.Min(
                wa.Height - DpiHelper.Scale(80),
                Math.Max(MinimumSize.Height, pref.Height + pad)
            );
            ClientSize = new Size(w, h);
        }
        catch { }
    }
}
