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
            Padding = DpiHelper.Scale(new Padding(14)),
            BackColor = UiTheme.Ground,
            Font = UiTheme.BodyFont,
            AutoScaleMode = AutoScaleMode.Dpi,
        };

        // Hazard diagonal across the top for stop-signal severities.
        if (icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error)
        {
            dialog.Controls.Add(new HazardStrip());
        }

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Header row: orange tag square + quoted caps caption + severity lamp.
        var header = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = DpiHelper.Scale(new Padding(0, 0, 0, 10)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(10)));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var tag = new Panel
        {
            BackColor = UiTheme.Orange,
            Size = DpiHelper.Scale(new Size(8, 8)),
            Margin = DpiHelper.Scale(new Padding(0, 4, 8, 0)),
        };
        var captionLabel = new Label
        {
            Text = UiTheme.Caps(caption),
            Font = UiTheme.CapsFont,
            ForeColor = UiTheme.Ink,
            AutoSize = true,
        };
        header.Controls.Add(tag, 0, 0);
        header.Controls.Add(captionLabel, 1, 0);

        // Severity lamp beside the caption (square lamp = label plate grammar).
        Color lampColor = icon switch
        {
            MessageBoxIcon.Warning => UiTheme.WarnText,
            MessageBoxIcon.Error => UiTheme.ErrorText,
            MessageBoxIcon.Information => UiTheme.Cyan,
            _ => UiTheme.Faint,
        };
        if (icon != MessageBoxIcon.None)
        {
            var lamp = UiTheme.Lamp(lampColor);
            lamp.Margin = DpiHelper.Scale(new Padding(10, 4, 0, 0));
            header.Controls.Add(lamp, 2, 0);
        }

        var picture = new PictureBox
        {
            Image = GetLogoBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Size = DpiHelper.Scale(new Size(48, 48)),
            Margin = DpiHelper.Scale(new Padding(0, 0, 12, 0)),
        };

        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(DpiHelper.Scale(480), 0),
            Margin = new Padding(0),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Ink,
        };

        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);
        layout.Controls.Add(picture, 0, 1);
        layout.Controls.Add(label, 1, 1);

        var buttonsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = DpiHelper.Scale(new Padding(0, 12, 0, 0)),
        };

        AddButtons(dialog, buttonsPanel, buttons);

        layout.SetColumnSpan(buttonsPanel, 2);
        layout.Controls.Add(buttonsPanel, 0, 2);

        dialog.Controls.Add(layout);
        return dialog;
    }

    private static void AddButtons(Form dialog, FlowLayoutPanel panel, MessageBoxButtons buttons)
    {
        Button Add(
            string text,
            DialogResult result,
            bool primary,
            bool isAccept = false,
            bool isCancel = false
        )
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                DialogResult = result,
                Margin = DpiHelper.Scale(new Padding(6, 0, 0, 0)),
            };
            UiTheme.StyleButton(
                btn,
                primary ? UiTheme.ButtonKind.Primary : UiTheme.ButtonKind.Secondary
            );
            if (isAccept)
                dialog.AcceptButton = btn;
            if (isCancel)
                dialog.CancelButton = btn;
            return btn;
        }

        switch (buttons)
        {
            case MessageBoxButtons.OK:
                panel.Controls.Add(
                    Add("OK", DialogResult.OK, primary: true, isAccept: true, isCancel: true)
                );
                break;

            case MessageBoxButtons.OKCancel:
                panel.Controls.Add(
                    Add("Cancel", DialogResult.Cancel, primary: false, isCancel: true)
                );
                panel.Controls.Add(Add("OK", DialogResult.OK, primary: true, isAccept: true));
                break;

            case MessageBoxButtons.YesNo:
                panel.Controls.Add(Add("No", DialogResult.No, primary: false, isCancel: true));
                panel.Controls.Add(Add("Yes", DialogResult.Yes, primary: true, isAccept: true));
                break;

            case MessageBoxButtons.YesNoCancel:
                panel.Controls.Add(
                    Add("Cancel", DialogResult.Cancel, primary: false, isCancel: true)
                );
                panel.Controls.Add(Add("No", DialogResult.No, primary: false));
                panel.Controls.Add(Add("Yes", DialogResult.Yes, primary: true, isAccept: true));
                break;

            default:
                panel.Controls.Add(
                    Add("OK", DialogResult.OK, primary: true, isAccept: true, isCancel: true)
                );
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

    /// <summary>
    /// Returns a cached logo bitmap shared across all TailSlap dialogs.
    /// Reuse this (never call LoadMainIcon().ToBitmap() ad hoc) so repeated
    /// dialog opens do not leak GDI+ bitmap handles.
    /// </summary>
    internal static Bitmap GetLogoBitmap()
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
