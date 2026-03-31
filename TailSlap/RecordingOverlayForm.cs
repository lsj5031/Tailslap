using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>
/// Floating capsule-shaped overlay shown during any active mode.
/// Uses a form Region so only the capsule itself is visible and interactive.
/// Displays real-time audio waveform bars driven by RMS levels, a pulsing indicator
/// for non-audio modes, and live transcription/refinement text.
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
    private const int IndicatorRadius = 6;

    // Wave shape: phase offsets create a flowing wave across bars
    private static readonly float[] BarPhaseOffsets = { 0f, 0.6f, 1.2f, 1.8f, 2.4f };
    private static readonly float[] BarWeights = { 0.5f, 0.8f, 1.0f, 0.75f, 0.55f };

    // Smoothing envelope (attack fast, release slow)
    private const float AttackCoeff = 0.4f;
    private const float ReleaseCoeff = 0.15f;

    // Animation durations (ms)
    private const int EntranceDuration = 350;
    private const int ExitDuration = 220;
    private const int WidthTransitionDuration = 250;
    private const int RenderInterval = 30; // ~33fps
    private const float WaveSpeed = 0.12f; // radians per tick for flowing wave

    // Colors
    private static readonly Color CapsuleBg = Color.FromArgb(35, 35, 40);
    private static readonly Color BarColor = Color.FromArgb(90, 210, 255);
    private static readonly Color TextColor = Color.FromArgb(230, 230, 240);
    private static readonly Color SubTextColor = Color.FromArgb(160, 160, 175);
    private static readonly Color IndicatorColor = Color.FromArgb(90, 210, 255);
    private static readonly Color BorderColor = Color.FromArgb(70, 70, 80);

    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_TOPMOST = 0x08;

    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly float[] _smoothedLevels = new float[BarCount];

    // Thread-safe RMS value: written from audio callback, read from UI timer
    private float _rmsValue;
    private float _smoothedRms;
    private string _transcriptionText = "";
    private string _statusText = "Recording...";
    private int _targetWidth;
    private int _currentAnimatedWidth;
    private float _alpha = 0f; // 0-255 alpha for layered window
    private float _wavePhase; // flowing wave phase offset
    private float _indicatorPulse; // for non-audio pulsing indicator
    private Size _regionSize = Size.Empty;

    /// <summary>
    /// Determines what visual indicator to show in the left area of the overlay.
    /// </summary>
    public enum OverlayMode
    {
        /// <summary>Audio-driven waveform bars (recording/streaming).</summary>
        Waveform,

        /// <summary>Pulsing circle indicator (refining/transcribing without audio).</summary>
        Pulse,
    }

    private OverlayMode _mode = OverlayMode.Waveform;

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

    public RecordingOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(MinWidth, CapsuleHeight);
        BackColor = CapsuleBg;
        Opacity = 0d;

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
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Update the current RMS audio level (called from audio callback).
    /// </summary>
    public void UpdateRms(float rms)
    {
        Interlocked.Exchange(ref _rmsValue, rms);
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
    /// Show the overlay with the given status text, mode, and entrance animation.
    /// </summary>
    public void ShowOverlay(string statusText, OverlayMode mode = OverlayMode.Waveform)
    {
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => ShowOverlay(statusText, mode)));
            }
            catch { }
            return;
        }

        _transcriptionText = "";
        _statusText = statusText;
        _mode = mode;
        _alpha = 0f;
        _smoothedRms = 0;
        Interlocked.Exchange(ref _rmsValue, 0f);
        _wavePhase = 0;
        _indicatorPulse = 0;
        Array.Clear(_smoothedLevels, 0, _smoothedLevels.Length);
        RecalculateTargetWidth();
        _currentAnimatedWidth = _targetWidth;
        Size = new Size(_currentAnimatedWidth, CapsuleHeight);
        UpdateWindowRegion(force: true);
        PositionAtBottom();
        _state = OverlayState.Entering;
        _animStartMs = Environment.TickCount;

        Opacity = 0d;

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
        _mode = OverlayMode.Pulse;
        Interlocked.Exchange(ref _rmsValue, 0f);
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
        _mode = OverlayMode.Pulse;
        Interlocked.Exchange(ref _rmsValue, 0f);
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

        int indicatorWidth =
            _mode == OverlayMode.Waveform ? WaveformBarAreaWidth : IndicatorRadius * 2 + 8;
        _targetWidth = indicatorWidth + 12 + textWidth + PaddingH * 2;
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
                float eased = SpringEaseOut(t, overshoot: 1.2f);
                _alpha = Math.Min(255f, eased * 255f);

                if (t >= 1f)
                {
                    _state = OverlayState.Visible;
                    _alpha = 255f;
                }
                break;
            }
            case OverlayState.Exiting:
            {
                int elapsed = now - _animStartMs;
                float t = Math.Min(1f, (float)elapsed / ExitDuration);
                float eased = 1f - (1f - t) * (1f - t);
                _alpha = 255f * (1f - eased);

                if (t >= 1f)
                {
                    _state = OverlayState.Hidden;
                    _renderTimer.Stop();
                    Opacity = 0d;
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
        float rms = Interlocked.CompareExchange(ref _rmsValue, 0f, 0f);
        if (rms > _smoothedRms)
            _smoothedRms += (rms - _smoothedRms) * AttackCoeff;
        else
            _smoothedRms += (rms - _smoothedRms) * ReleaseCoeff;

        // Advance wave phase for flowing animation
        _wavePhase += WaveSpeed;
        if (_wavePhase > MathF.PI * 2f)
            _wavePhase -= MathF.PI * 2f;

        // Update indicator pulse
        _indicatorPulse += 0.06f;
        if (_indicatorPulse > MathF.PI * 2f)
            _indicatorPulse -= MathF.PI * 2f;

        if (_mode == OverlayMode.Waveform)
        {
            float normalizedRms = Math.Min(1f, _smoothedRms / 2000f);
            // Breathing amplitude: strong when silent, subtle when speaking
            float breathAmp = normalizedRms < 0.1f ? 0.35f : 0.05f;

            for (int i = 0; i < BarCount; i++)
            {
                // Flowing wave: each bar has a phase offset creating a ripple
                float wave = MathF.Sin(_wavePhase + BarPhaseOffsets[i]);

                // Base level from audio RMS + wave motion
                float audioLevel = normalizedRms * BarWeights[i];
                float waveLevel = wave * breathAmp;
                float target = Math.Max(0.08f, Math.Min(1f, audioLevel + waveLevel));

                // Smooth transitions
                if (target > _smoothedLevels[i])
                    _smoothedLevels[i] += (target - _smoothedLevels[i]) * AttackCoeff;
                else
                    _smoothedLevels[i] += (target - _smoothedLevels[i]) * ReleaseCoeff;
            }
        }

        // Apply size, position, and alpha
        if (_state != OverlayState.Hidden)
        {
            Size = new Size(_currentAnimatedWidth, CapsuleHeight);
            UpdateWindowRegion();
            PositionAtBottom();
            Opacity = Math.Clamp(_alpha / 255f, 0d, 1d);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var rect = ClientRectangle;

        // Draw capsule background
        using (var path = CreateCapsulePath(rect.Width, rect.Height))
        {
            using var brush = new SolidBrush(CapsuleBg);
            g.FillPath(brush, path);

            // Inset border
            using var pen = new Pen(BorderColor, 1.5f);
            pen.Alignment = PenAlignment.Inset;
            g.DrawPath(pen, path);
        }

        // Draw indicator on left side
        float indicatorAreaX = PaddingH;

        if (_mode == OverlayMode.Waveform)
        {
            DrawWaveformBars(g, indicatorAreaX);
        }
        else
        {
            DrawPulseIndicator(g, indicatorAreaX);
        }

        // Draw text (right of indicator)
        string displayText = !string.IsNullOrEmpty(_transcriptionText)
            ? _transcriptionText
            : _statusText;
        bool isActive = !string.IsNullOrEmpty(_transcriptionText);

        int indicatorWidth =
            _mode == OverlayMode.Waveform ? WaveformBarAreaWidth : IndicatorRadius * 2 + 8;
        float textX = indicatorAreaX + indicatorWidth + 12;
        float textAreaWidth = rect.Width - textX - PaddingH;

        using var textFont = new Font("Segoe UI", 10f);
        using var textBrush = new SolidBrush(isActive ? TextColor : SubTextColor);

        var textRect = new RectangleF(textX, 0, textAreaWidth, CapsuleHeight);
        var stringFormat = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(displayText, textFont, textBrush, textRect, stringFormat);
    }

    private void DrawWaveformBars(Graphics g, float barAreaX)
    {
        float barAreaY = (CapsuleHeight - WaveformBarAreaHeight) / 2f;
        float totalBarsWidth = BarCount * BarWidth + (BarCount - 1) * BarGap;
        float barsStartX = barAreaX + (WaveformBarAreaWidth - totalBarsWidth) / 2f;

        for (int i = 0; i < BarCount; i++)
        {
            float barHeight = _smoothedLevels[i] * WaveformBarAreaHeight;
            barHeight = Math.Max(6, barHeight);
            float bx = barsStartX + i * (BarWidth + BarGap);
            float by = barAreaY + (WaveformBarAreaHeight - barHeight) / 2f;

            using var barBrush = new LinearGradientBrush(
                new PointF(bx, by),
                new PointF(bx, by + barHeight),
                BarColor,
                Color.FromArgb(60, 160, 220)
            );
            g.FillRectangle(barBrush, bx, by, BarWidth, barHeight);
        }
    }

    private void DrawPulseIndicator(Graphics g, float indicatorAreaX)
    {
        float cx = indicatorAreaX + IndicatorRadius + 4;
        float cy = CapsuleHeight / 2f;

        float pulseScale = 0.6f + 0.4f * (0.5f + 0.5f * MathF.Sin(_indicatorPulse));
        int glowRadius = (int)(IndicatorRadius * pulseScale * 1.8f);
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(cx - glowRadius, cy - glowRadius, glowRadius * 2, glowRadius * 2);
            using var glowBrush = new SolidBrush(Color.FromArgb(25, 90, 210, 255));
            g.FillPath(glowBrush, glowPath);
        }

        float coreRadius =
            IndicatorRadius * (0.7f + 0.3f * (0.5f + 0.5f * MathF.Sin(_indicatorPulse)));
        float r = Math.Max(3, coreRadius);
        using var coreBrush = new SolidBrush(IndicatorColor);
        g.FillEllipse(coreBrush, cx - r, cy - r, r * 2, r * 2);
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

    private static float SpringEaseOut(float t, float overshoot = 1.0f)
    {
        float s = overshoot;
        t -= 1f;
        return t * t * ((s + 1f) * t + s) + 1f;
    }

    private static float EaseOutCubic(float t)
    {
        return 1f - (1f - t) * (1f - t) * (1f - t);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion(bool force = false)
    {
        if (!force && ClientSize == _regionSize)
            return;

        _regionSize = ClientSize;

        if (_regionSize.Width <= 0 || _regionSize.Height <= 0)
            return;

        using var path = CreateCapsulePath(_regionSize.Width, _regionSize.Height);
        Region = new Region(path);
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
