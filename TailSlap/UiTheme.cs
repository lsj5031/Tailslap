using System;
using System.Drawing;
using System.Windows.Forms;

// ============================================================================
// DIRECTION CONTRACT — TailSlap "Stockroom Tags" (impeccable, new-work §5)
//
// THESIS: The tool is a labeled stockroom, not a dashboard. Every surface is
// white cotton + black nylon with safety-orange zip-tie tags; the thing that
// is ON is tagged, the thing that is OFF is a plain plate, and a hazard
// diagonal means STOP. It refuses the generic light-settings SaaS look that
// this category ships by default.
//
// OWN-WORLD: Stockroom white ground (#FAF9F6), near-black ink (#1F1F1F),
// safety orange (#FF6A00) reserved for the live/primary tag, product cyan
// (#0E7C9E) for data, the green/amber/red severity lamp vocabulary, bold
// quoted caps ("RECORDING") on square label plates, Consolas for scannable
// data, 45° hazard diagonals for stop-states.
//
// STORY: The user reads the room the way a stock clerk reads a shelf: what
// is tagged orange is live, what is lit green/amber/red tells state at a
// glance, what is crossed by a hazard stripe cannot be touched. Nothing
// shouts; every control is a plate you can trust.
//
// FIRST VIEWPORT: A settings form whose tabs open under quoted caps tags
// ("GENERAL", "LLM REFINEMENT", "RECORDING", "ADVANCED"), the primary OK
// button is an orange plate, hotkeys are mono plates, and the validation
// strip turns into a hazard diagonal when something is wrong.
//
// FORM: challenger-stockroom (industrial streetwear quote grammar), dealt
// challenger chosen by the user on the decision page; seed key f64d3539.
// ----------------------------------------------------------------------------
// FINISH: unreviewed and undocumented is unfinished; this build ends with the
// finish review, the verdict, and DESIGN.md.
// ============================================================================

/// <summary>
/// TailSlap's shared "Stockroom Tags" design system: palette tokens, cached
/// fonts, and factories for the tagged-plate control language used across
/// every form. Light-only (user-confirmed). Fonts and colors are cached for
/// the app lifetime, matching the existing logo-bitmap caching pattern.
/// </summary>
public static class UiTheme
{
    // --- Ground & ink -------------------------------------------------------
    /// <summary>Warm stockroom-white form background.</summary>
    public static readonly Color Ground = Color.FromArgb(250, 249, 246);

    /// <summary>Pure white panels, lists, cards.</summary>
    public static readonly Color Panel = Color.White;

    /// <summary>Near-black nylon primary text.</summary>
    public static readonly Color Ink = Color.FromArgb(31, 31, 31);

    /// <summary>Secondary text.</summary>
    public static readonly Color Muted = Color.FromArgb(110, 110, 114);

    /// <summary>Faint placeholders / disabled text.</summary>
    public static readonly Color Faint = Color.FromArgb(158, 158, 162);

    /// <summary>Hairline dividers / plate borders.</summary>
    public static readonly Color Rule = Color.FromArgb(226, 224, 218);

    // --- Brand accents ------------------------------------------------------
    /// <summary>Safety orange — the live/primary zip-tie tag (decorative accent).</summary>
    public static readonly Color Orange = Color.FromArgb(255, 106, 0);

    /// <summary>Primary button plate — darkened for >=4.5:1 contrast with white bold caps.</summary>
    public static readonly Color PrimaryFill = Color.FromArgb(204, 74, 0);

    /// <summary>Hover state of the primary plate.</summary>
    public static readonly Color PrimaryHover = Color.FromArgb(176, 65, 0);

    /// <summary>Pressed state of the primary plate.</summary>
    public static readonly Color PrimaryPressed = Color.FromArgb(158, 57, 0);

    /// <summary>Soft orange tint for active-row backgrounds.</summary>
    public static readonly Color OrangeSoft = Color.FromArgb(255, 240, 227);

    /// <summary>Product cyan (darkened for text on white) — data accent.</summary>
    public static readonly Color Cyan = Color.FromArgb(14, 124, 158);

    /// <summary>The overlay waveform cyan.</summary>
    public static readonly Color CyanBar = Color.FromArgb(90, 210, 255);

    // --- Severity lamp vocabulary (unified across all forms) ----------------
    public static readonly Color SuccessBack = Color.FromArgb(237, 247, 237);
    public static readonly Color SuccessText = Color.FromArgb(46, 130, 50);
    public static readonly Color WarnBack = Color.FromArgb(255, 249, 224);
    public static readonly Color WarnText = Color.FromArgb(150, 105, 8);
    public static readonly Color ErrorBack = Color.FromArgb(255, 241, 241);
    public static readonly Color ErrorText = Color.FromArgb(190, 45, 45);

    // --- Fonts (cached for app lifetime; never dispose these) ---------------
    /// <summary>Standard body face — use instead of per-dialog `new Font(...)` to avoid GDI leaks.</summary>
    public static readonly Font BodyFont = CreateFont("Segoe UI", 9f, FontStyle.Regular);

    /// <summary>Bold caps for quoted tag headers and primary plates.</summary>
    public static readonly Font CapsFont = CreateFont("Segoe UI", 9f, FontStyle.Bold);

    /// <summary>Dialog title face.</summary>
    public static readonly Font TitleFont = CreateFont("Segoe UI", 12f, FontStyle.Bold);

    /// <summary>Scannable data (timestamps, hotkeys, URLs, values).</summary>
    public static readonly Font MonoFont = CreateFont("Consolas", 9f, FontStyle.Regular);

    /// <summary>Bold mono for values that must pop (hotkeys, timecode).</summary>
    public static readonly Font MonoBoldFont = CreateFont("Consolas", 9f, FontStyle.Bold);

    /// <summary>Robust font factory: falls back gracefully if a face is missing.</summary>
    private static Font CreateFont(string family, float size, FontStyle style)
    {
        try
        {
            return new Font(family, size, style);
        }
        catch
        {
            try
            {
                return new Font(
                    family == "Consolas"
                        ? FontFamily.GenericMonospace
                        : FontFamily.GenericSansSerif,
                    size,
                    style
                );
            }
            catch
            {
                return SystemFonts.DefaultFont;
            }
        }
    }

    /// <summary>Wrap text in straight quotes, the stockroom's "name the obvious" register.</summary>
    public static string Caps(string text) => "\"" + text.ToUpperInvariant() + "\"";

    /// <summary>
    /// A full-width section header: orange square tag + quoted bold caps,
    /// over a hairline rule. Dock it Top inside a form or tab page.
    /// </summary>
    public static TableLayoutPanel TagStrip(string text, int bottomMargin = 8)
    {
        var strip = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, DpiHelper.Scale(bottomMargin)),
        };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(10)));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var tag = new Panel
        {
            BackColor = Orange,
            Size = DpiHelper.Scale(new Size(8, 8)),
            Margin = DpiHelper.Scale(new Padding(0, 4, 8, 0)),
        };
        var label = new Label
        {
            Text = Caps(text),
            Font = CapsFont,
            ForeColor = Ink,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
        };
        var rule = new Panel
        {
            BackColor = Rule,
            Height = 1,
            Dock = DockStyle.Fill,
            Margin = DpiHelper.Scale(new Padding(8, 9, 0, 0)),
        };

        strip.Controls.Add(tag, 0, 0);
        strip.Controls.Add(label, 1, 0);
        strip.Controls.Add(rule, 2, 0);
        return strip;
    }

    /// <summary>Which plate a button is made of.</summary>
    public enum ButtonKind
    {
        /// <summary>Safety-orange plate — the single primary action.</summary>
        Primary,

        /// <summary>White plate with hairline border — standard action.</summary>
        Secondary,

        /// <summary>White plate with red border/text — destructive action.</summary>
        Danger,
    }

    /// <summary>
    /// Style an existing AutoSize button as a stockroom label plate. Keeps the
    /// button's Text, AutoSize and event wiring; only the plate changes.
    /// </summary>
    public static void StyleButton(Button btn, ButtonKind kind = ButtonKind.Secondary)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.Cursor = Cursors.Hand;
        btn.Margin = DpiHelper.Scale(new Padding(4, 2, 0, 2));
        btn.Font = kind == ButtonKind.Primary ? CapsFont : btn.Font;

        switch (kind)
        {
            case ButtonKind.Primary:
                btn.BackColor = PrimaryFill;
                btn.ForeColor = Color.White;
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = PrimaryHover;
                btn.FlatAppearance.MouseDownBackColor = PrimaryPressed;
                break;
            case ButtonKind.Danger:
                btn.BackColor = Panel;
                btn.ForeColor = ErrorText;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = ErrorText;
                btn.FlatAppearance.MouseOverBackColor = ErrorBack;
                btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(253, 232, 230);
                break;
            default:
                btn.BackColor = Panel;
                btn.ForeColor = Ink;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Rule;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(246, 244, 239);
                btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(238, 235, 228);
                break;
        }

        // Grey out the plate when disabled (Flat buttons do not do this alone).
        void ApplyEnabledState()
        {
            if (!btn.Enabled)
            {
                btn.BackColor = Color.FromArgb(238, 236, 232);
                btn.ForeColor = Faint;
            }
            else if (kind == ButtonKind.Primary)
            {
                btn.BackColor = PrimaryFill;
                btn.ForeColor = Color.White;
            }
            else if (kind == ButtonKind.Danger)
            {
                btn.BackColor = Panel;
                btn.ForeColor = ErrorText;
            }
            else
            {
                btn.BackColor = Panel;
                btn.ForeColor = Ink;
            }
        }

        btn.EnabledChanged += (_, __) => ApplyEnabledState();
        ApplyEnabledState();
    }

    /// <summary>
    /// A 10x10 square state lamp. Fill with SuccessText / WarnText / ErrorText
    /// / Orange / Cyan; render inside a small cell via Dock/Anchor.
    /// </summary>
    public static Label Lamp(Color color)
    {
        return new Label
        {
            AutoSize = false,
            Size = DpiHelper.Scale(new Size(10, 10)),
            BackColor = color,
            Margin = DpiHelper.Scale(new Padding(0, 4, 6, 0)),
        };
    }

    /// <summary>Builds the shared bottom status row used by themed dialogs.</summary>
    public static TableLayoutPanel StatusRow(
        out Label statusLamp,
        out Label statusLabel,
        int topMargin = 4
    )
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = DpiHelper.Scale(new Padding(0, topMargin, 0, 0)),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        statusLamp = Lamp(Muted);
        statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Muted,
            Margin = new Padding(0),
        };
        row.Controls.Add(statusLamp, 0, 0);
        row.Controls.Add(statusLabel, 1, 0);
        return row;
    }

    /// <summary>Makes the final ListView column consume the available width.</summary>
    public static void FillLastListViewColumn(ListView list, int minimumWidth = 80)
    {
        if (list.Columns.Count == 0)
            return;

        int available = list.ClientSize.Width - DpiHelper.Scale(4);
        for (int i = 0; i < list.Columns.Count - 1; i++)
            available -= list.Columns[i].Width;

        int width = Math.Max(DpiHelper.Scale(minimumWidth), available);
        if (list.Columns[^1].Width != width)
            list.Columns[^1].Width = width;
    }
}

/// <summary>
/// A 45° black-on-white hazard diagonal band, the stockroom's STOP signal.
/// Used for destructive/error banners and validation failure strips.
/// </summary>
public sealed class HazardStrip : Panel
{
    public HazardStrip()
    {
        Height = DpiHelper.Scale(6);
        Dock = DockStyle.Top;
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer,
            true
        );
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int band = DpiHelper.Scale(12);
        int w = Width + Height * 2;

        using (var white = new SolidBrush(Color.White))
            g.FillRectangle(white, 0, 0, Width, Height);

        g.TranslateTransform(0, Height);
        g.RotateTransform(-45);
        using (var black = new SolidBrush(Color.FromArgb(30, 30, 30)))
        {
            for (int x = -Height * 2; x < w; x += band * 2)
                g.FillRectangle(black, x, -Height * 2, band, Height * 4);
        }
        g.ResetTransform();
    }
}
