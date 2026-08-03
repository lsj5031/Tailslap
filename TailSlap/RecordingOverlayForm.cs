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
    private const int CapsuleHeight = 60;
    private const int CapsuleCornerRadius = 28;
    private const int WaveformBarAreaWidth = 48;
    private const int WaveformBarAreaHeight = 36;
    private const int BarCount = 7;
    private const int BarWidth = 3;
    private const int BarGap = 3;
    private const int MinWidth = 200;
    private const int TextMinWidth = 120;
    private const int TextMaxWidth = 520;
    private const int PaddingH = 20;
    private const int BottomMargin = 48;
    private const int IndicatorRadius = 6;

    // Wave shape: phase offsets create a flowing wave across bars
    private static readonly float[] BarPhaseOffsets =
    {
        0f,
        0.48f,
        0.96f,
        1.44f,
        1.92f,
        2.4f,
        2.88f,
    };
    private static readonly float[] BarWeights = { 0.42f, 0.68f, 0.88f, 1f, 0.88f, 0.68f, 0.42f };

    // Smoothing envelope (attack fast, release slow)
    private const float AttackCoeff = 0.4f;
    private const float ReleaseCoeff = 0.15f;

    // Animation durations (ms)
    private const int EntranceDuration = 300;
    private const int ExitDuration = 220;
    private const int WidthTransitionDuration = 250;
    private const int RenderInterval = 30; // ~33fps
    private const float WaveSpeed = 4.2f; // radians per second for the flowing wave
    private const float IndicatorPulseSpeed = 4.8f; // radians per second
    private const float AudioLevelScale = 9000f;

    // Cached fonts (never dispose — shared for app lifetime)
    private static readonly Font TextFont = new Font("Segoe UI", 10f);

    // Colors
    private static readonly Color CapsuleBg = Color.FromArgb(35, 35, 40);
    private static readonly Color BarColor = Color.FromArgb(90, 210, 255);
    private static readonly Color BarHighlight = Color.FromArgb(178, 238, 255);
    private static readonly Color ProcessingColor = UiTheme.Orange;
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
    private float _verticalOffset;
    private int _lastRenderMs;
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
        _verticalOffset = 10;
        Array.Clear(_smoothedLevels, 0, _smoothedLevels.Length);
        RecalculateTargetWidth();
        _currentAnimatedWidth = _targetWidth;
        _widthAnimStartValue = _targetWidth;
        _widthAnimStartMs = Environment.TickCount;
        Size = new Size(_currentAnimatedWidth, CapsuleHeight);
        UpdateWindowRegion(force: true);
        PositionAtBottom((int)_verticalOffset);
        _state = OverlayState.Entering;
        _animStartMs = Environment.TickCount;
        _lastRenderMs = _animStartMs;

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
        SetStatus("Transcribing...");
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
        SetStatus("Refining...");
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
        _lastRenderMs = _animStartMs;
    }

    private void SetStatus(string statusText)
    {
        _statusText = statusText;
        _transcriptionText = "";
        _mode = OverlayMode.Pulse;
        Interlocked.Exchange(ref _rmsValue, 0f);
        RecalculateTargetWidth();
    }

    private void RecalculateTargetWidth()
    {
        using var g = Graphics.FromHwnd(Handle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var font = TextFont;
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

    private void PositionAtBottom(int verticalOffset = 0)
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        int x = screen.X + (screen.Width - Width) / 2;
        int y = screen.Y + screen.Height - CapsuleHeight - BottomMargin + verticalOffset;
        Location = new Point(x, y);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        int now = Environment.TickCount;
        float deltaSeconds =
            _lastRenderMs == 0
                ? RenderInterval / 1000f
                : Math.Clamp((now - _lastRenderMs) / 1000f, 0.008f, 0.1f);
        _lastRenderMs = now;

        switch (_state)
        {
            case OverlayState.Entering:
            {
                int elapsed = now - _animStartMs;
                float t = Math.Min(1f, (float)elapsed / EntranceDuration);
                float eased = EaseOutQuint(t);
                _alpha = eased * 255f;
                _verticalOffset = (1f - eased) * 10f;

                if (t >= 1f)
                {
                    _state = OverlayState.Visible;
                    _alpha = 255f;
                    _verticalOffset = 0f;
                }
                break;
            }
            case OverlayState.Exiting:
            {
                int elapsed = now - _animStartMs;
                float t = Math.Min(1f, (float)elapsed / ExitDuration);
                float eased = EaseOutCubic(t);
                _alpha = 255f * (1f - eased);
                _verticalOffset = eased * 6f;

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

        // Smooth RMS with an envelope that is stable even if the WinForms timer jitters.
        float rms = Interlocked.CompareExchange(ref _rmsValue, 0f, 0f);
        float frameFactor = deltaSeconds / (RenderInterval / 1000f);
        float attack = 1f - MathF.Pow(1f - AttackCoeff, frameFactor);
        float release = 1f - MathF.Pow(1f - ReleaseCoeff, frameFactor);
        if (rms > _smoothedRms)
            _smoothedRms += (rms - _smoothedRms) * attack;
        else
            _smoothedRms += (rms - _smoothedRms) * release;

        // Advance phase by elapsed time so the motion stays consistent under load.
        _wavePhase += WaveSpeed * deltaSeconds;
        if (_wavePhase > MathF.PI * 2f)
            _wavePhase -= MathF.PI * 2f;

        _indicatorPulse += IndicatorPulseSpeed * deltaSeconds;
        if (_indicatorPulse > MathF.PI * 2f)
            _indicatorPulse -= MathF.PI * 2f;

        if (_mode == OverlayMode.Waveform)
        {
            float normalizedRms = Math.Clamp(
                MathF.Pow(Math.Min(1f, _smoothedRms / AudioLevelScale), 0.7f),
                0f,
                1f
            );
            float idleLevel = 0.08f + (1f - normalizedRms) * 0.025f;
            float waveAmplitude = normalizedRms < 0.1f ? 0.12f : 0.045f;

            for (int i = 0; i < BarCount; i++)
            {
                // Flowing wave: each bar has a phase offset creating a ripple
                float wave = MathF.Sin(_wavePhase + BarPhaseOffsets[i]);

                // Base level from audio RMS + wave motion
                float audioLevel = normalizedRms * BarWeights[i] * 0.9f;
                float waveLevel = wave * waveAmplitude;
                float target = Math.Clamp(idleLevel + audioLevel + waveLevel, 0.07f, 1f);

                // Smooth transitions
                if (target > _smoothedLevels[i])
                    _smoothedLevels[i] += (target - _smoothedLevels[i]) * attack;
                else
                    _smoothedLevels[i] += (target - _smoothedLevels[i]) * release;
            }
        }

        // Apply size, position, and alpha
        if (_state != OverlayState.Hidden)
        {
            Size = new Size(_currentAnimatedWidth, CapsuleHeight);
            UpdateWindowRegion();
            PositionAtBottom((int)MathF.Round(_verticalOffset));
            Opacity = Math.Clamp(_alpha / 255f, 0d, 1d);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.GammaCorrected;
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

        var textFont = TextFont;
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
            float barHeight = Math.Max(6f, _smoothedLevels[i] * WaveformBarAreaHeight);
            float bx = barsStartX + i * (BarWidth + BarGap);
            float by = barAreaY + (WaveformBarAreaHeight - barHeight) / 2f;

            using var barBrush = new LinearGradientBrush(
                new PointF(bx, by),
                new PointF(bx, by + barHeight),
                BarHighlight,
                Color.FromArgb(46, 142, 190)
            );
            using var barPath = CreateRoundedRectanglePath(
                new RectangleF(bx, by, BarWidth, barHeight),
                BarWidth / 2f
            );
            g.FillPath(barBrush, barPath);
        }
    }

    private void DrawPulseIndicator(Graphics g, float indicatorAreaX)
    {
        float cx = indicatorAreaX + IndicatorRadius + 4;
        float cy = CapsuleHeight / 2f;

        var indicatorColor = _mode == OverlayMode.Pulse ? ProcessingColor : IndicatorColor;
        float pulse = 0.5f + 0.5f * MathF.Sin(_indicatorPulse);
        float pulseScale = 0.7f + 0.3f * pulse;
        float glowRadius = IndicatorRadius * (1.7f + pulse * 1.1f);
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(cx - glowRadius, cy - glowRadius, glowRadius * 2, glowRadius * 2);
            using var glowBrush = new SolidBrush(
                Color.FromArgb(24, indicatorColor.R, indicatorColor.G, indicatorColor.B)
            );
            g.FillPath(glowBrush, glowPath);
        }

        float ringRadius = IndicatorRadius + 3f + pulse * 2f;
        using var ringPen = new Pen(
            Color.FromArgb(90, indicatorColor.R, indicatorColor.G, indicatorColor.B),
            1f
        );
        g.DrawEllipse(ringPen, cx - ringRadius, cy - ringRadius, ringRadius * 2, ringRadius * 2);

        float coreRadius = IndicatorRadius * (0.75f + 0.25f * pulseScale);
        float r = Math.Max(3, coreRadius);
        using var coreBrush = new SolidBrush(indicatorColor);
        g.FillEllipse(coreBrush, cx - r, cy - r, r * 2, r * 2);

        using var glintBrush = new SolidBrush(Color.FromArgb(175, 255, 255, 255));
        g.FillEllipse(glintBrush, cx - r * 0.4f, cy - r * 0.55f, r * 0.55f, r * 0.4f);
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2f;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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

    private static float EaseOutQuint(float t)
    {
        return 1f - MathF.Pow(1f - t, 5f);
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
