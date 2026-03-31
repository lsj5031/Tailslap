using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>
/// Floating capsule-shaped overlay shown during push-to-talk recording.
/// Displays real-time audio waveform bars driven by RMS levels and live transcription text.
/// </summary>
public sealed class RecordingOverlayForm : Form
{
    // Layout constants
    private const int CapsuleHeight = 56;
    private const int CapsuleCornerRadius = 28;
    private const int WaveformBarAreaWidth = 44;
    private const int WaveformBarAreaHeight = 32;
    private const int BarCount = 5;
    private const int BarWidth = 4;
    private const int BarGap = 4;
    private const int MinWidth = 200;
    private const int TextMinWidth = 120;
    private const int TextMaxWidth = 520;
    private const int PaddingH = 20;
    private const int BottomMargin = 48;

    // Waveform weights: center-high, natural falloff
    private static readonly float[] BarWeights = { 0.5f, 0.8f, 1.0f, 0.75f, 0.55f };

    // Smoothing envelope (attack fast, release slow)
    private const float AttackCoeff = 0.4f;
    private const float ReleaseCoeff = 0.15f;

    // Animation durations (ms)
    private const int EntranceDuration = 350;
    private const int ExitDuration = 220;
    private const int WidthTransitionDuration = 250;
    private const int RenderInterval = 30; // ~33fps

    // Colors
    private static readonly Color CapsuleBg = Color.FromArgb(35, 35, 40);
    private static readonly Color BarColor = Color.FromArgb(90, 210, 255);
    private static readonly Color BarInactiveColor = Color.FromArgb(60, 60, 70);
    private static readonly Color TextColor = Color.FromArgb(230, 230, 240);
    private static readonly Color SubTextColor = Color.FromArgb(160, 160, 175);

    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly Random _jitter = new();
    private readonly float[] _smoothedLevels = new float[BarCount];
    private readonly float[] _jitterOffsets = new float[BarCount];

    private float _currentRms;
    private float _smoothedRms;
    private string _transcriptionText = "";
    private string _statusText = "Recording...";
    private int _targetWidth;
    private int _currentAnimatedWidth;
    private float _opacity = 0f;
    private float _scale = 0.6f;

    private enum OverlayState
    {
        Hidden,
        Entering,
        Visible,
        Exiting,
    }

    private OverlayState _state = OverlayState.Hidden;
    private int _animStartMs;
    private int _widthAnimStartMs;
    private int _widthAnimStartValue;

    //public RecordingOverlayForm()
    public RecordingOverlayForm()
    {
        // Form setup: borderless, topmost, no taskbar, no activation
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0;
        Size = new Size(MinWidth, CapsuleHeight);

        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        // Prevent the overlay from stealing focus
        CreateParams cps = new CreateParams();
        // Will be applied in OnHandleCreated

        SetStyle(
            ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw,
            true
        );
        DoubleBuffered = true;

        _renderTimer = new System.Windows.Forms.Timer { Interval = RenderInterval };
        _renderTimer.Tick += OnRenderTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_NOACTIVATE (0x08000000) | WS_EX_TOOLWINDOW (0x80) | WS_EX_TOPMOST (0x08)
            // WS_EX_LAYERED (0x80000) for per-pixel alpha
            cp.ExStyle |= 0x08000000 | 0x80 | 0x08 | 0x80000;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Update the current RMS audio level (called from audio callback).
    /// </summary>
    public void UpdateRms(float rms)
    {
        _currentRms = rms;
    }

    /// <summary>
    /// Update the live transcription text displayed in the capsule.
    /// </summary>
    public void UpdateTranscriptionText(string text)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => UpdateTranscriptionText(text)));
            }
            catch { }
            return;
        }
        _transcriptionText = text ?? "";
        RecalculateTargetWidth();
    }

    /// <summary>
    /// Show the overlay with a "Recording..." status and entrance animation.
    /// </summary>
    public void ShowOverlay()
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(ShowOverlay));
            }
            catch { }
            return;
        }

        _transcriptionText = "";
        _statusText = "Recording...";
        _opacity = 0f;
        _scale = 0.6f;
        _smoothedRms = 0;
        Array.Clear(_smoothedLevels, 0, _smoothedLevels.Length);
        RecalculateTargetWidth();
        _currentAnimatedWidth = _targetWidth;
        PositionAtBottom();
        _state = OverlayState.Entering;
        _animStartMs = Environment.TickCount;

        Show();
        _renderTimer.Start();
    }

    /// <summary>
    /// Transition to "Transcribing..." state.
    /// </summary>
    public void ShowTranscribing()
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(ShowTranscribing));
            }
            catch { }
            return;
        }
        _statusText = "Transcribing...";
        _currentRms = 0;
        Invalidate();
    }

    /// <summary>
    /// Transition to "Refining..." state.
    /// </summary>
    public void ShowRefining()
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(ShowRefining));
            }
            catch { }
            return;
        }
        _statusText = "Refining...";
        _currentRms = 0;
        Invalidate();
    }

    /// <summary>
    /// Hide the overlay with an exit animation.
    /// </summary>
    public void HideOverlay()
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(HideOverlay));
            }
            catch { }
            return;
        }

        if (_state == OverlayState.Hidden || _state == OverlayState.Exiting)
            return;

        _state = OverlayState.Exiting;
        _animStartMs = Environment.TickCount;
    }

    private void RecalculateTargetWidth()
    {
        using var g = Graphics.FromHwnd(Handle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var font = new Font("Segoe UI", 10f);
        string displayText = !string.IsNullOrEmpty(_transcriptionText)
            ? _transcriptionText
            : _statusText;
        int textWidth = (int)g.MeasureString(displayText, font, TextMaxWidth).Width;
        textWidth = Math.Max(TextMinWidth, Math.Min(TextMaxWidth, textWidth));

        _targetWidth = WaveformBarAreaWidth + 12 + textWidth + PaddingH * 2;
        _targetWidth = Math.Max(MinWidth, _targetWidth);

        if (_state == OverlayState.Visible || _state == OverlayState.Entering)
        {
            _widthAnimStartMs = Environment.TickCount;
            _widthAnimStartValue = _currentAnimatedWidth;
        }
    }

    private void PositionAtBottom()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        int x = screen.X + (screen.Width - Width) / 2;
        int y = screen.Y + screen.Height - CapsuleHeight - BottomMargin;
        Location = new Point(x, y);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        int now = Environment.TickCount;

        switch (_state)
        {
            case OverlayState.Entering:
            {
                int elapsed = now - _animStartMs;
                float t = Math.Min(1f, (float)elapsed / EntranceDuration);
                // Spring-like easing: overshoot
                float eased = SpringEaseOut(t, overshoot: 1.2f);
                _opacity = Math.Min(1f, eased);
                _scale = 0.6f + 0.4f * eased;

                if (t >= 1f)
                {
                    _state = OverlayState.Visible;
                    _opacity = 1f;
                    _scale = 1f;
                }
                break;
            }
            case OverlayState.Exiting:
            {
                int elapsed = now - _animStartMs;
                float t = Math.Min(1f, (float)elapsed / ExitDuration);
                // Ease-out for exit
                float eased = 1f - (1f - t) * (1f - t);
                _opacity = 1f - eased;
                _scale = 1f - 0.2f * eased;

                if (t >= 1f)
                {
                    _state = OverlayState.Hidden;
                    _renderTimer.Stop();
                    Hide();
                    return;
                }
                break;
            }
        }

        // Animate width transitions
        if (_state == OverlayState.Visible || _state == OverlayState.Entering)
        {
            int widthElapsed = now - _widthAnimStartMs;
            float wt = Math.Min(1f, (float)widthElapsed / WidthTransitionDuration);
            float wEased = EaseOutCubic(wt);
            _currentAnimatedWidth =
                _widthAnimStartValue + (int)((_targetWidth - _widthAnimStartValue) * wEased);
        }

        // Smooth RMS with attack/release envelope
        if (_currentRms > _smoothedRms)
            _smoothedRms += (_currentRms - _smoothedRms) * AttackCoeff;
        else
            _smoothedRms += (_currentRms - _smoothedRms) * ReleaseCoeff;

        // Update individual bar levels with jitter
        float normalizedRms = Math.Min(1f, _smoothedRms / 2000f); // Normalize: 2000 RMS ≈ loud
        for (int i = 0; i < BarCount; i++)
        {
            // Add ±4% random jitter for organic feel
            _jitterOffsets[i] =
                (_jitterOffsets[i] + ((float)_jitter.NextDouble() - 0.5f) * 0.08f) * 0.6f;
            float jitter = _jitterOffsets[i];
            float target = normalizedRms * BarWeights[i] + jitter;
            target = Math.Max(0.05f, Math.Min(1f, target));

            if (target > _smoothedLevels[i])
                _smoothedLevels[i] += (target - _smoothedLevels[i]) * AttackCoeff;
            else
                _smoothedLevels[i] += (target - _smoothedLevels[i]) * ReleaseCoeff;
        }

        // Apply size and position
        if (_state != OverlayState.Hidden)
        {
            int w = (int)(_currentAnimatedWidth * _scale);
            int h = (int)(CapsuleHeight * _scale);
            Size = new Size(w, h);
            PositionAtBottom();
            Opacity = _opacity;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = ClientRectangle;

        // Draw capsule background
        using (var path = CreateCapsulePath(rect.Width, rect.Height))
        {
            using var brush = new SolidBrush(CapsuleBg);
            g.FillPath(brush, path);

            // Subtle border
            using var pen = new Pen(Color.FromArgb(60, 60, 70), 1f);
            g.DrawPath(pen, path);
        }

        // Scale content for entrance/exit animation
        float contentScale = _scale;
        float cx = rect.Width / 2f;
        float cy = rect.Height / 2f;

        // Draw waveform bars (left side)
        float barAreaX = PaddingH;
        float barAreaY = (CapsuleHeight - WaveformBarAreaHeight) / 2f;
        float totalBarsWidth = BarCount * BarWidth + (BarCount - 1) * BarGap;
        float barsStartX = barAreaX + (WaveformBarAreaWidth - totalBarsWidth) / 2f;

        for (int i = 0; i < BarCount; i++)
        {
            float barHeight = _smoothedLevels[i] * WaveformBarAreaHeight;
            barHeight = Math.Max(3, barHeight); // Minimum visible height
            float bx = barsStartX + i * (BarWidth + BarGap);
            float by = barAreaY + (WaveformBarAreaHeight - barHeight) / 2f;

            // Active bar with gradient
            using var barBrush = new LinearGradientBrush(
                new PointF(bx, by),
                new PointF(bx, by + barHeight),
                BarColor,
                Color.FromArgb(60, 160, 220)
            );
            g.FillRectangle(barBrush, bx, by, BarWidth, barHeight);
        }

        // Draw text (right of waveform)
        string displayText = !string.IsNullOrEmpty(_transcriptionText)
            ? _transcriptionText
            : _statusText;
        bool isActive = !string.IsNullOrEmpty(_transcriptionText);
        float textX = barAreaX + WaveformBarAreaWidth + 12;
        float textAreaWidth = rect.Width - textX - PaddingH;

        using var textFont = new Font("Segoe UI", 10f);
        using var textBrush = new SolidBrush(isActive ? TextColor : SubTextColor);

        // Clip text to available area
        var textRect = new RectangleF(textX, 0, textAreaWidth, CapsuleHeight);
        var stringFormat = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(displayText, textFont, textBrush, textRect, stringFormat);
    }

    private static GraphicsPath CreateCapsulePath(int width, int height)
    {
        var path = new GraphicsPath();
        int radius = Math.Min(CapsuleCornerRadius, height / 2);
        radius = Math.Min(radius, width / 2);
        var rect = new Rectangle(0, 0, width, height);
        path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.X + rect.Width - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(
            rect.X + rect.Width - radius * 2,
            rect.Y + rect.Height - radius * 2,
            radius * 2,
            radius * 2,
            0,
            90
        );
        path.AddArc(rect.X, rect.Y + rect.Height - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Spring ease-out with optional overshoot (mimics CASpringAnimation).
    /// </summary>
    private static float SpringEaseOut(float t, float overshoot = 1.0f)
    {
        // Damped spring approximation
        float s = overshoot;
        t -= 1f;
        return t * t * ((s + 1f) * t + s) + 1f;
    }

    private static float EaseOutCubic(float t)
    {
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
