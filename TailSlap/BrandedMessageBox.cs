using System;
using System.Drawing;
using System.Windows.Forms;

public static class BrandedMessageBox
{
    private static Icon? _logoIcon;
    private static Bitmap? _logoBitmap;

    public static DialogResult Show(
        string text,
        string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None,
        IWin32Window? owner = null
    )
    {
        try
        {
            using var dialog = BuildDialog(text, caption, buttons, icon);
            return dialog.ShowDialog(owner);
        }
        catch
        {
            // As a last resort, fall back to the standard message box.
            return MessageBox.Show(text, caption, buttons, icon);
        }
    }

    private static Form BuildDialog(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon
    )
    {
        var dialog = new Form
        {
            Text = caption,
            Icon = GetLogoIcon(),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
        };

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        var picture = new PictureBox
        {
            Image = GetLogoBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Width = 48,
            Height = 48,
            Margin = new Padding(0, 0, 12, 0),
        };

        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            Margin = new Padding(0, 6, 0, 0),
        };

        layout.Controls.Add(picture, 0, 0);
        layout.Controls.Add(label, 1, 0);

        var buttonsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0),
        };

        AddButtons(dialog, buttonsPanel, buttons);

        layout.SetColumnSpan(buttonsPanel, 2);
        layout.Controls.Add(buttonsPanel, 0, 1);

        dialog.Controls.Add(layout);

        // Append glyph to the caption for quick severity recognition.
        if (icon == MessageBoxIcon.Warning)
        {
            dialog.Text = $"{caption} ⚠️";
        }
        else if (icon == MessageBoxIcon.Error)
        {
            dialog.Text = $"{caption} ❌";
        }
        else if (icon == MessageBoxIcon.Information)
        {
            dialog.Text = $"{caption} ℹ️";
        }

        return dialog;
    }

    private static void AddButtons(Form dialog, FlowLayoutPanel panel, MessageBoxButtons buttons)
    {
        Button Add(string text, DialogResult result, bool isAccept = false, bool isCancel = false)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                DialogResult = result,
                Margin = new Padding(6, 0, 0, 0),
            };
            if (isAccept)
                dialog.AcceptButton = btn;
            if (isCancel)
                dialog.CancelButton = btn;
            return btn;
        }

        switch (buttons)
        {
            case MessageBoxButtons.OK:
                panel.Controls.Add(Add("OK", DialogResult.OK, isAccept: true, isCancel: true));
                break;

            case MessageBoxButtons.OKCancel:
                panel.Controls.Add(Add("Cancel", DialogResult.Cancel, isCancel: true));
                panel.Controls.Add(Add("OK", DialogResult.OK, isAccept: true));
                break;

            case MessageBoxButtons.YesNo:
                panel.Controls.Add(Add("No", DialogResult.No, isCancel: true));
                panel.Controls.Add(Add("Yes", DialogResult.Yes, isAccept: true));
                break;

            case MessageBoxButtons.YesNoCancel:
                panel.Controls.Add(Add("Cancel", DialogResult.Cancel, isCancel: true));
                panel.Controls.Add(Add("No", DialogResult.No));
                panel.Controls.Add(Add("Yes", DialogResult.Yes, isAccept: true));
                break;

            default:
                panel.Controls.Add(Add("OK", DialogResult.OK, isAccept: true, isCancel: true));
                break;
        }
    }

    private static Icon GetLogoIcon()
    {
        if (_logoIcon != null)
            return _logoIcon;

        try
        {
            _logoIcon = MainForm.LoadMainIcon();
            return _logoIcon;
        }
        catch { }

        try
        {
            _logoIcon = Properties.Resources.IconIdle;
            return _logoIcon;
        }
        catch { }

        // Final fallback to embedded icon to avoid default system icon.
        _logoIcon = Properties.Resources.IconIdle;
        return _logoIcon;
    }

    private static Bitmap GetLogoBitmap()
    {
        if (_logoBitmap != null)
            return _logoBitmap;

        try
        {
            var icon = GetLogoIcon();
            _logoBitmap = icon.ToBitmap();
            return _logoBitmap;
        }
        catch { }

        try
        {
            _logoBitmap = Properties.Resources.IconIdle.ToBitmap();
            return _logoBitmap;
        }
        catch { }

        // Final fallback - create bitmap directly from embedded resource
        _logoBitmap = Properties.Resources.IconIdle.ToBitmap();
        return _logoBitmap;
    }
}
