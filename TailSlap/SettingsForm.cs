using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TailSlap;

public sealed class SettingsForm : Form
{
    private enum HotkeyTarget
    {
        Llm,
        Transcriber,
        Typeless,
        Streaming,
    }

    private const int HotkeyProbeId = 0x4A17;
    private const int SettingsLabelColumnWidth = 150;

    private readonly AppConfig _cfg;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;

    // Fields assigned from Build* helpers; null! is the definite-assignment contract.
    private CheckBox _enabled = null!;
    private CheckBox _autoPaste = null!;
    private CheckBox _excludeFromClipboardHistory = null!;
    private CheckBox _clipboardFallback = null!;
    private TextBox _baseUrl = null!;
    private TextBox _model = null!;
    private TextBox _temperature = null!;
    private TextBox _maxTokens = null!;
    private TextBox _refinementPrompt = null!;
    private ComboBox? _promptPresetDropdown;
    private TextBox _apiKey = null!;
    private TextBox _referer = null!;
    private TextBox _xTitle = null!;
    private TextBox _llmHotkey = null!;
    private Button _resetButton = null!;
    private Button _testConnectionButton = null!;
    private Button _captureLlmHotkeyButton = null!;
    private Label _validationLabel = null!;
    private Label _llmTestResultLabel = null!;
    private HazardStrip _hazardStrip = null!;

    // Transcriber controls
    private CheckBox? _transcriberEnabled;
    private CheckBox? _transcriberAutoPaste;
    private CheckBox? _transcriberStreamResults;
    private TextBox? _transcriberBaseUrl;
    private TextBox? _transcriberModel;
    private TextBox? _transcriberTimeout;
    private TextBox? _transcriberApiKey;
    private TextBox? _transcriberHotkey;
    private TextBox? _streamingTranscriberHotkey;
    private ComboBox? _microphoneDropdown;
    private Button? _captureTranscriberHotkeyButton;
    private Button? _captureStreamingTranscriberHotkeyButton;
    private Button? _testTranscriberConnectionButton;
    private Label? _transcriberTestResultLabel;
    private Button? _detectMicrophonesButton;
    private TextBox? _typelessHotkey;
    private Button? _captureTypelessHotkeyButton;
    private CheckBox? _transcriberEnableVAD;
    private TextBox? _transcriberSilenceThreshold;
    private ComboBox? _transcriberVadSensitivity;
    private CheckBox? _transcriberEnableAutoEnhance;
    private TextBox? _transcriberAutoEnhanceThreshold;
    private ComboBox? _realtimeProviderDropdown;
    private TextBox? _transcriberLanguage;
    private TextBox? _transcriberRealtimeSessionPrompt;

    // WebSocket timeout controls
    private TextBox? _wsConnectionTimeout;
    private TextBox? _wsReceiveTimeout;
    private TextBox? _wsSendTimeout;
    private TextBox? _wsHeartbeatInterval;
    private TextBox? _wsHeartbeatTimeout;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Compact in-grid builder for a two-column settings grid.</summary>
    private sealed class GridBuilder
    {
        private readonly TableLayoutPanel _grid;
        private int _row;

        public GridBuilder(TableLayoutPanel grid)
        {
            _grid = grid;
        }

        public void Header(string text)
        {
            var tag = new Panel
            {
                BackColor = UiTheme.Orange,
                Size = DpiHelper.Scale(new Size(8, 8)),
                Margin = DpiHelper.Scale(new Padding(0, 12, 8, 0)),
            };
            var label = new Label
            {
                Text = UiTheme.Caps(text),
                Font = UiTheme.CapsFont,
                ForeColor = UiTheme.Ink,
                AutoSize = true,
                Margin = DpiHelper.Scale(new Padding(0, 10, 0, 2)),
            };
            var row = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Margin = new Padding(0),
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(10)));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(tag, 0, 0);
            row.Controls.Add(label, 1, 0);
            _grid.Controls.Add(row, 0, _row);
            _grid.SetColumnSpan(row, 2);
            _row++;
        }

        public void Add(
            string caption,
            Control control,
            ContentAlignment align = ContentAlignment.MiddleLeft
        )
        {
            _grid.Controls.Add(
                new Label
                {
                    Text = caption,
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    TextAlign = align,
                },
                0,
                _row
            );
            _grid.Controls.Add(control, 1, _row);
            _row++;
        }
    }

    public SettingsForm(
        AppConfig cfg,
        ITextRefinerFactory textRefinerFactory,
        IRemoteTranscriberFactory remoteTranscriberFactory
    )
    {
        _cfg = cfg;
        _textRefinerFactory =
            textRefinerFactory ?? throw new ArgumentNullException(nameof(textRefinerFactory));
        _remoteTranscriberFactory =
            remoteTranscriberFactory
            ?? throw new ArgumentNullException(nameof(remoteTranscriberFactory));

        Text = "TailSlap Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        Width = DpiHelper.Scale(720);
        Height = DpiHelper.Scale(600);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(DpiHelper.Scale(620), DpiHelper.Scale(520));
        SizeGripStyle = SizeGripStyle.Show;
        Icon = MainForm.LoadMainIcon();
        BackColor = UiTheme.Ground;
        Font = UiTheme.BodyFont;

        var tabs = new TabControl { Dock = DockStyle.Fill };

        tabs.TabPages.Add(BuildGeneralPage());
        tabs.TabPages.Add(BuildLlmPage());
        tabs.TabPages.Add(BuildRecordingPage());
        tabs.TabPages.Add(BuildAdvancedPage());

        // Bottom bar: hazard strip + validation strip above the button plate row.
        var bottomBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
        };
        bottomBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottomBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _hazardStrip = new HazardStrip { Visible = false };
        bottomBar.Controls.Add(_hazardStrip, 0, 0);

        var validationRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = DpiHelper.Scale(new Padding(12, 4, 12, 0)),
        };
        validationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        validationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _validationLabel = new Label
        {
            Text = "",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Ink,
        };
        validationRowLamp = UiTheme.Lamp(UiTheme.SuccessText);
        validationRow.Controls.Add(validationRowLamp, 0, 0);
        validationRow.Controls.Add(_validationLabel, 1, 0);
        bottomBar.Controls.Add(validationRow, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Padding = DpiHelper.Scale(new Padding(12, 6, 12, 10)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _resetButton = new Button
        {
            Text = "Reset to Defaults",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _resetButton.Click += ResetToDefaults;
        UiTheme.StyleButton(ok, UiTheme.ButtonKind.Primary);
        UiTheme.StyleButton(cancel, UiTheme.ButtonKind.Secondary);
        UiTheme.StyleButton(_resetButton, UiTheme.ButtonKind.Danger);

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_resetButton);
        bottomBar.Controls.Add(buttons, 0, 2);

        Controls.Add(tabs);
        Controls.Add(bottomBar);

        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, __) => ApplyChanges();

        // Add real-time validation
        _baseUrl.TextChanged += RefreshValidationState;
        _temperature.TextChanged += RefreshValidationState;
        _maxTokens.TextChanged += RefreshValidationState;
        _model.TextChanged += RefreshValidationState;
        _apiKey.TextChanged += (_, _) => _llmTestResultLabel.Text = "";
        _baseUrl.TextChanged += (_, _) => _llmTestResultLabel.Text = "";
        _transcriberBaseUrl!.TextChanged += RefreshValidationState;
        _transcriberModel!.TextChanged += RefreshValidationState;
        _transcriberTimeout!.TextChanged += RefreshValidationState;
        _transcriberAutoEnhanceThreshold!.TextChanged += RefreshValidationState;
        _transcriberBaseUrl!.TextChanged += (_, _) => _transcriberTestResultLabel!.Text = "";
        _transcriberApiKey!.TextChanged += (_, _) => _transcriberTestResultLabel!.Text = "";
        RefreshValidationState(null, EventArgs.Empty);
    }

    private TabPage BuildGeneralPage()
    {
        var page = new TabPage("General") { AutoScroll = true };

        var general = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(0, 8, 0, 16)),
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        general.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(SettingsLabelColumnWidth))
        );
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            general.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var g = new GridBuilder(general);
        _autoPaste = new CheckBox
        {
            Text = "Auto-paste refined text",
            Checked = _cfg.AutoPaste,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        g.Add("Auto Paste", _autoPaste);

        _excludeFromClipboardHistory = new CheckBox
        {
            Text = "Exclude delivered text from clipboard history",
            Checked = _cfg.ExcludeFromClipboardHistory,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        g.Add("Clipboard Privacy", _excludeFromClipboardHistory);

        _clipboardFallback = new CheckBox
        {
            Text = "Use clipboard when nothing is selected",
            Checked = _cfg.UseClipboardFallback,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        g.Add("Clipboard Fallback", _clipboardFallback);

        // Tag strip added last so it docks to the top edge first.
        page.Controls.Add(general);
        page.Controls.Add(UiTheme.TagStrip("General", bottomMargin: 2));
        return page;
    }

    private TabPage BuildLlmPage()
    {
        var page = new TabPage("LLM Refinement") { AutoScroll = true };

        var llm = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(0, 8, 0, 16)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        llm.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(SettingsLabelColumnWidth))
        );
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var b = new GridBuilder(llm);

        b.Header("Core");
        _enabled = new CheckBox
        {
            Text = "Enable LLM refinement",
            Checked = _cfg.Llm.Enabled,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        b.Add("Enabled", _enabled);

        _baseUrl = new TextBox { Text = _cfg.Llm.BaseUrl, Dock = DockStyle.Fill };
        b.Add("Base URL", _baseUrl);

        _model = new TextBox { Text = _cfg.Llm.Model, Dock = DockStyle.Fill };
        b.Add("Model", _model);

        _temperature = new TextBox
        {
            Text = _cfg.Llm.Temperature.ToString("0.##"),
            Dock = DockStyle.Fill,
        };
        b.Add("Temperature", _temperature);

        _maxTokens = new TextBox
        {
            Text = _cfg.Llm.MaxTokens?.ToString() ?? "",
            Dock = DockStyle.Fill,
        };
        b.Add("Max Tokens", _maxTokens);

        _apiKey = new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = "Enter API key (leave blank to keep existing)",
            Dock = DockStyle.Fill,
        };
        b.Add("API Key", _apiKey);

        b.Header("Prompt");
        _refinementPrompt = new TextBox
        {
            Text = _cfg.Llm.GetEffectiveRefinementPrompt(),
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Height = DpiHelper.Scale(150),
        };
        b.Add("Prompt", _refinementPrompt, ContentAlignment.TopLeft);

        _promptPresetDropdown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
        };
        _promptPresetDropdown.Items.Add("(Choose a prompt preset…)");
        foreach (var preset in PromptPresets.All)
            _promptPresetDropdown.Items.Add(preset.Name);
        _promptPresetDropdown.SelectedIndex = 0;
        _promptPresetDropdown.SelectedIndexChanged += (_, __) =>
        {
            if (_promptPresetDropdown.SelectedIndex <= 0)
                return;
            var preset = PromptPresets.All[_promptPresetDropdown.SelectedIndex - 1];
            _refinementPrompt.Text = preset.Body;
        };
        b.Add("Prompt Preset", _promptPresetDropdown);

        b.Header("HTTP Headers");
        _referer = new TextBox
        {
            Text = _cfg.Llm.HttpReferer ?? string.Empty,
            Dock = DockStyle.Fill,
        };
        b.Add("HTTP Referer", _referer);

        _xTitle = new TextBox { Text = _cfg.Llm.XTitle ?? string.Empty, Dock = DockStyle.Fill };
        b.Add("X-Title", _xTitle);

        b.Header("Hotkey & Test");
        _llmHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.Hotkey),
            Dock = DockStyle.Fill,
            Font = UiTheme.MonoBoldFont,
            BackColor = UiTheme.Panel,
            BorderStyle = BorderStyle.FixedSingle,
        };
        b.Add("Refinement Hotkey", _llmHotkey);

        _captureLlmHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureLlmHotkeyButton.Click += CaptureLlmHotkey;
        UiTheme.StyleButton(_captureLlmHotkeyButton, UiTheme.ButtonKind.Secondary);
        b.Add("", _captureLlmHotkeyButton);

        _testConnectionButton = new Button
        {
            Text = "Test LLM Connection",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _testConnectionButton.Click += TestConnection;
        UiTheme.StyleButton(_testConnectionButton, UiTheme.ButtonKind.Secondary);
        _llmTestResultLabel = new Label
        {
            Text = "",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Ink,
            Margin = DpiHelper.Scale(new Padding(8, 0, 0, 0)),
        };
        var llmTestRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        llmTestRow.Controls.Add(_testConnectionButton);
        llmTestRow.Controls.Add(_llmTestResultLabel);
        b.Add("Test Connection", llmTestRow);

        page.Controls.Add(llm);
        page.Controls.Add(UiTheme.TagStrip("LLM Refinement", bottomMargin: 2));
        return page;
    }

    private TabPage BuildRecordingPage()
    {
        var page = new TabPage("Recording") { AutoScroll = true };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(0, 8, 0, 16)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        panel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(SettingsLabelColumnWidth))
        );
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var b = new GridBuilder(panel);

        b.Header("Endpoint");
        _transcriberEnabled = new CheckBox
        {
            Text = "Enable Remote Transcription",
            Checked = _cfg.Transcriber.Enabled,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        b.Add("Enabled", _transcriberEnabled);

        _transcriberBaseUrl = new TextBox
        {
            Text = _cfg.Transcriber.BaseUrl,
            Dock = DockStyle.Fill,
        };
        b.Add("ASR Server URL", _transcriberBaseUrl);

        _transcriberModel = new TextBox { Text = _cfg.Transcriber.Model, Dock = DockStyle.Fill };
        b.Add("Model", _transcriberModel);

        _transcriberTimeout = new TextBox
        {
            Text = _cfg.Transcriber.TimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Timeout (seconds)", _transcriberTimeout);

        _transcriberApiKey = new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = "Enter API key (leave blank to keep existing)",
            Dock = DockStyle.Fill,
        };
        b.Add("API Key", _transcriberApiKey);

        _transcriberAutoPaste = new CheckBox
        {
            Text = "Auto-paste transcribed text",
            Checked = _cfg.Transcriber.AutoPaste,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        b.Add("Auto Paste", _transcriberAutoPaste);

        _transcriberStreamResults = new CheckBox
        {
            Text = "Stream results as they arrive",
            Checked = _cfg.Transcriber.StreamResults,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        b.Add("Stream Results", _transcriberStreamResults);

        b.Header("Silence Detection");
        _transcriberEnableVAD = new CheckBox
        {
            Text = "Stop recording after silence",
            Checked = _cfg.Transcriber.EnableVAD,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        b.Add("Silence Detection", _transcriberEnableVAD);

        _transcriberSilenceThreshold = new TextBox
        {
            Text = _cfg.Transcriber.SilenceThresholdMs.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Silence Timeout (milliseconds)", _transcriberSilenceThreshold);

        _transcriberVadSensitivity = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
        };
        _transcriberVadSensitivity.Items.AddRange(
            new object[]
            {
                "Low (Noisy Environment)",
                "Medium (Default)",
                "High (Quiet Environment)",
            }
        );
        if (_cfg.Transcriber.VadActivationThreshold >= 800)
            _transcriberVadSensitivity.SelectedIndex = 0;
        else if (_cfg.Transcriber.VadActivationThreshold >= 400)
            _transcriberVadSensitivity.SelectedIndex = 1;
        else
            _transcriberVadSensitivity.SelectedIndex = 2;
        b.Add("VAD Sensitivity", _transcriberVadSensitivity);

        b.Header("Source");
        _microphoneDropdown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
        };
        RefreshMicrophoneList();
        if (
            _cfg.Transcriber.PreferredMicrophoneIndex >= 0
            && _cfg.Transcriber.PreferredMicrophoneIndex < _microphoneDropdown.Items.Count
        )
            _microphoneDropdown.SelectedIndex = _cfg.Transcriber.PreferredMicrophoneIndex;
        else if (_microphoneDropdown.Items.Count > 0)
            _microphoneDropdown.SelectedIndex = 0;
        b.Add("Microphone", _microphoneDropdown);

        _detectMicrophonesButton = new Button
        {
            Text = "Refresh Microphone List",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _detectMicrophonesButton.Click += DetectMicrophones;
        UiTheme.StyleButton(_detectMicrophonesButton, UiTheme.ButtonKind.Secondary);
        b.Add("", _detectMicrophonesButton);

        b.Header("Auto-Enhance");
        _transcriberEnableAutoEnhance = new CheckBox
        {
            Text = "Polish long transcriptions with LLM",
            Checked = _cfg.Transcriber.EnableAutoEnhance,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        b.Add("Auto-Enhance", _transcriberEnableAutoEnhance);

        _transcriberAutoEnhanceThreshold = new TextBox
        {
            Text = _cfg.Transcriber.AutoEnhanceThresholdChars.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Minimum text length for auto-enhance", _transcriberAutoEnhanceThreshold);

        b.Header("Hotkeys");
        _transcriberHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.TranscriberHotkey),
            Dock = DockStyle.Fill,
            Font = UiTheme.MonoBoldFont,
            BackColor = UiTheme.Panel,
            BorderStyle = BorderStyle.FixedSingle,
        };
        b.Add("Toggle Transcription Hotkey", _transcriberHotkey);

        _captureTranscriberHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureTranscriberHotkeyButton.Click += CaptureTranscriberHotkey;
        UiTheme.StyleButton(_captureTranscriberHotkeyButton, UiTheme.ButtonKind.Secondary);
        b.Add("", _captureTranscriberHotkeyButton);

        _typelessHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.TypelessHotkey),
            Dock = DockStyle.Fill,
            Font = UiTheme.MonoBoldFont,
            BackColor = UiTheme.Panel,
            BorderStyle = BorderStyle.FixedSingle,
        };
        b.Add("Push-to-Talk Hotkey", _typelessHotkey);

        _captureTypelessHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureTypelessHotkeyButton.Click += CaptureTypelessHotkey;
        UiTheme.StyleButton(_captureTypelessHotkeyButton, UiTheme.ButtonKind.Secondary);
        b.Add("", _captureTypelessHotkeyButton);

        _streamingTranscriberHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.StreamingTranscriberHotkey),
            Dock = DockStyle.Fill,
            Font = UiTheme.MonoBoldFont,
            BackColor = UiTheme.Panel,
            BorderStyle = BorderStyle.FixedSingle,
        };
        b.Add("Realtime Streaming Hotkey", _streamingTranscriberHotkey);

        _captureStreamingTranscriberHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureStreamingTranscriberHotkeyButton.Click += CaptureStreamingTranscriberHotkey;
        UiTheme.StyleButton(_captureStreamingTranscriberHotkeyButton, UiTheme.ButtonKind.Secondary);
        b.Add("", _captureStreamingTranscriberHotkeyButton);

        b.Header("Test");
        _testTranscriberConnectionButton = new Button
        {
            Text = "Test ASR Connection",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _testTranscriberConnectionButton.Click += TestTranscriberConnection;
        UiTheme.StyleButton(_testTranscriberConnectionButton, UiTheme.ButtonKind.Secondary);
        _transcriberTestResultLabel = new Label
        {
            Text = "",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Ink,
            Margin = DpiHelper.Scale(new Padding(8, 0, 0, 0)),
        };
        var transcriberTestRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        transcriberTestRow.Controls.Add(_testTranscriberConnectionButton);
        transcriberTestRow.Controls.Add(_transcriberTestResultLabel);
        b.Add("Test Connection", transcriberTestRow);

        page.Controls.Add(panel);
        page.Controls.Add(UiTheme.TagStrip("Recording", bottomMargin: 2));
        return page;
    }

    private TabPage BuildAdvancedPage()
    {
        var page = new TabPage("Advanced") { AutoScroll = true };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(0, 8, 0, 16)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        panel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(SettingsLabelColumnWidth))
        );
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var b = new GridBuilder(panel);

        b.Header("WebSocket");
        _wsConnectionTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketConnectionTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Connection Timeout (seconds)", _wsConnectionTimeout);

        _wsReceiveTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketReceiveTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Receive Timeout (seconds)", _wsReceiveTimeout);

        _wsSendTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketSendTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Send Timeout (seconds)", _wsSendTimeout);

        _wsHeartbeatInterval = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketHeartbeatIntervalSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Heartbeat Interval (seconds)", _wsHeartbeatInterval);

        _wsHeartbeatTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketHeartbeatTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        b.Add("Heartbeat Timeout (seconds)", _wsHeartbeatTimeout);

        _wsConnectionTimeout.TextChanged += RefreshValidationState;
        _wsReceiveTimeout.TextChanged += RefreshValidationState;
        _wsSendTimeout.TextChanged += RefreshValidationState;
        _wsHeartbeatInterval.TextChanged += RefreshValidationState;
        _wsHeartbeatTimeout.TextChanged += RefreshValidationState;

        b.Header("Realtime");
        _realtimeProviderDropdown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
        };
        _realtimeProviderDropdown.Items.AddRange(new object[] { "custom", "openai" });
        _realtimeProviderDropdown.SelectedItem = string.Equals(
            _cfg.Transcriber.RealtimeProvider,
            "custom",
            StringComparison.OrdinalIgnoreCase
        )
            ? "custom"
            : "openai";
        b.Add("Realtime Provider", _realtimeProviderDropdown);

        _transcriberLanguage = new TextBox
        {
            Text = _cfg.Transcriber.Language ?? "",
            Dock = DockStyle.Fill,
            PlaceholderText = "e.g. en, zh — blank = provider auto-detect",
        };
        b.Add("ASR Language", _transcriberLanguage);

        _transcriberRealtimeSessionPrompt = new TextBox
        {
            Text = _cfg.Transcriber.RealtimeSessionPrompt ?? "",
            Dock = DockStyle.Fill,
            PlaceholderText = "Domain-specific vocabulary hints for realtime",
        };
        b.Add("Session Prompt (optional vocab hint)", _transcriberRealtimeSessionPrompt);

        page.Controls.Add(panel);
        page.Controls.Add(UiTheme.TagStrip("Advanced", bottomMargin: 2));
        return page;
    }

    private void RefreshMicrophoneList()
    {
        _microphoneDropdown!.Items.Clear();
        var mics = AudioRecorder.GetAvailableMicrophones();
        foreach (var mic in mics)
        {
            _microphoneDropdown.Items.Add(mic);
        }
        if (_microphoneDropdown.Items.Count == 0)
        {
            _microphoneDropdown.Items.Add("(No microphones detected)");
            _microphoneDropdown.Enabled = false;
        }
    }

    private void DetectMicrophones(object? sender, EventArgs e)
    {
        RefreshMicrophoneList();
        NotificationService.ShowInfo($"Found {_microphoneDropdown!.Items.Count} microphone(s).");
    }

    private void ApplyChanges()
    {
        if (!ValidateAllInput())
        {
            NotificationService.ShowError("Please fix validation errors before saving.");
            DialogResult = DialogResult.None;
            return;
        }

        _cfg.AutoPaste = _autoPaste.Checked;
        _cfg.ExcludeFromClipboardHistory = _excludeFromClipboardHistory.Checked;
        _cfg.Llm.Enabled = _enabled.Checked;
        _cfg.Llm.BaseUrl = _baseUrl.Text.Trim();
        _cfg.Llm.Model = _model.Text.Trim();
        if (double.TryParse(_temperature.Text.Trim(), out var t))
            _cfg.Llm.Temperature = t;
        var mt = _maxTokens.Text.Trim();
        _cfg.Llm.MaxTokens = string.IsNullOrEmpty(mt) ? null : (int?)int.Parse(mt);
        _cfg.Llm.RefinementPrompt = string.IsNullOrWhiteSpace(_refinementPrompt.Text)
            ? LlmConfig.DefaultRefinementPrompt
            : _refinementPrompt.Text.Trim();

        var k = _apiKey.Text.Trim();
        _cfg.Llm.ApiKey = string.IsNullOrWhiteSpace(k) ? _cfg.Llm.ApiKey : k;
        _cfg.Llm.HttpReferer = string.IsNullOrWhiteSpace(_referer.Text)
            ? null
            : _referer.Text.Trim();
        _cfg.Llm.XTitle = string.IsNullOrWhiteSpace(_xTitle.Text) ? null : _xTitle.Text.Trim();
        _cfg.UseClipboardFallback = _clipboardFallback.Checked;

        _cfg.Transcriber.Enabled = _transcriberEnabled!.Checked;
        _cfg.Transcriber.BaseUrl = _transcriberBaseUrl!.Text.Trim();
        _cfg.Transcriber.Model = _transcriberModel!.Text.Trim();
        if (int.TryParse(_transcriberTimeout!.Text.Trim(), out var timeout))
            _cfg.Transcriber.TimeoutSeconds = timeout;
        var transcriberKey = _transcriberApiKey!.Text.Trim();
        _cfg.Transcriber.ApiKey = string.IsNullOrWhiteSpace(transcriberKey)
            ? _cfg.Transcriber.ApiKey
            : transcriberKey;
        _cfg.Transcriber.AutoPaste = _transcriberAutoPaste!.Checked;
        _cfg.Transcriber.StreamResults = _transcriberStreamResults!.Checked;
        _cfg.Transcriber.EnableVAD = _transcriberEnableVAD!.Checked;
        if (
            int.TryParse(_transcriberSilenceThreshold!.Text.Trim(), out var silenceMs)
            && silenceMs >= 500
            && silenceMs <= 10000
        )
            _cfg.Transcriber.SilenceThresholdMs = silenceMs;

        if (_transcriberVadSensitivity != null && _transcriberVadSensitivity.SelectedIndex >= 0)
        {
            switch (_transcriberVadSensitivity.SelectedIndex)
            {
                case 0:
                    _cfg.Transcriber.VadActivationThreshold = 900;
                    _cfg.Transcriber.VadSustainThreshold = 250;
                    _cfg.Transcriber.VadSilenceThreshold = 120;
                    break;
                case 1:
                    _cfg.Transcriber.VadActivationThreshold = 600;
                    _cfg.Transcriber.VadSustainThreshold = 180;
                    _cfg.Transcriber.VadSilenceThreshold = 80;
                    break;
                case 2:
                    _cfg.Transcriber.VadActivationThreshold = 300;
                    _cfg.Transcriber.VadSustainThreshold = 100;
                    _cfg.Transcriber.VadSilenceThreshold = 40;
                    break;
            }
        }

        _cfg.Transcriber.PreferredMicrophoneIndex =
            _microphoneDropdown!.SelectedIndex >= 0 ? _microphoneDropdown.SelectedIndex : -1;

        _cfg.Transcriber.EnableAutoEnhance = _transcriberEnableAutoEnhance!.Checked;
        if (
            int.TryParse(_transcriberAutoEnhanceThreshold!.Text.Trim(), out var thresholdChars)
            && thresholdChars >= 10
            && thresholdChars <= 10000
        )
            _cfg.Transcriber.AutoEnhanceThresholdChars = thresholdChars;

        if (
            int.TryParse(_wsConnectionTimeout!.Text.Trim(), out var wsConnTimeout)
            && wsConnTimeout >= 1
            && wsConnTimeout <= 120
        )
            _cfg.Transcriber.WebSocketConnectionTimeoutSeconds = wsConnTimeout;

        if (
            int.TryParse(_wsReceiveTimeout!.Text.Trim(), out var wsRecvTimeout)
            && wsRecvTimeout >= 1
            && wsRecvTimeout <= 120
        )
            _cfg.Transcriber.WebSocketReceiveTimeoutSeconds = wsRecvTimeout;

        if (
            int.TryParse(_wsSendTimeout!.Text.Trim(), out var wsSendTimeout)
            && wsSendTimeout >= 1
            && wsSendTimeout <= 120
        )
            _cfg.Transcriber.WebSocketSendTimeoutSeconds = wsSendTimeout;

        if (
            int.TryParse(_wsHeartbeatInterval!.Text.Trim(), out var wsHbInterval)
            && wsHbInterval >= 5
            && wsHbInterval <= 60
        )
            _cfg.Transcriber.WebSocketHeartbeatIntervalSeconds = wsHbInterval;

        if (
            int.TryParse(_wsHeartbeatTimeout!.Text.Trim(), out var wsHbTimeout)
            && wsHbTimeout >= 10
            && wsHbTimeout <= 120
        )
            _cfg.Transcriber.WebSocketHeartbeatTimeoutSeconds = wsHbTimeout;

        _cfg.Transcriber.Language = _transcriberLanguage?.Text?.Trim() ?? "";
        _cfg.Transcriber.RealtimeSessionPrompt =
            _transcriberRealtimeSessionPrompt?.Text?.Trim() ?? "";

        _cfg.Transcriber.RealtimeProvider = string.Equals(
            _realtimeProviderDropdown?.SelectedItem?.ToString(),
            "openai",
            StringComparison.OrdinalIgnoreCase
        )
            ? "openai"
            : "custom";
    }

    private void RefreshValidationState(object? sender, EventArgs e)
    {
        var errors = GetAllValidationErrors();
        bool hasErrors = errors.Count > 0;
        _validationLabel.Text = hasErrors
            ? "⚠ "
                + string.Join(" · ", errors.Take(2))
                + (errors.Count > 2 ? $" (+{errors.Count - 2} more)" : "")
            : "✓ All settings valid";
        _validationLabel.ForeColor = hasErrors ? UiTheme.ErrorText : UiTheme.SuccessText;
        _hazardStrip.Visible = hasErrors;
        // Keep the lamp in sync: green when valid, red when not.
        if (validationRowLamp != null)
            validationRowLamp.BackColor = hasErrors ? UiTheme.ErrorText : UiTheme.SuccessText;
    }

    private Label? validationRowLamp;

    private bool ValidateAllInput()
    {
        var errors = GetAllValidationErrors();
        bool hasErrors = errors.Count > 0;
        _validationLabel.Text = hasErrors
            ? "⚠ "
                + string.Join(" · ", errors.Take(2))
                + (errors.Count > 2 ? $" (+{errors.Count - 2} more)" : "")
            : "✓ All settings valid";
        _validationLabel.ForeColor = hasErrors ? UiTheme.ErrorText : UiTheme.SuccessText;
        _hazardStrip.Visible = hasErrors;
        if (validationRowLamp != null)
            validationRowLamp.BackColor = hasErrors ? UiTheme.ErrorText : UiTheme.SuccessText;
        return !hasErrors;
    }

    private System.Collections.Generic.List<string> GetAllValidationErrors()
    {
        var errors = new System.Collections.Generic.List<string>();
        errors.AddRange(GetLlmValidationErrors());
        errors.AddRange(GetTranscriberValidationErrors());
        errors.AddRange(GetHotkeyValidationErrors());
        return errors;
    }

    private System.Collections.Generic.List<string> GetLlmValidationErrors()
    {
        var errors = new System.Collections.Generic.List<string>();

        if (!string.IsNullOrWhiteSpace(_baseUrl.Text))
        {
            if (
                !Uri.TryCreate(_baseUrl.Text, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
            )
            {
                errors.Add("Base URL must be a valid HTTP/HTTPS URL");
            }
        }

        if (!string.IsNullOrWhiteSpace(_temperature.Text))
        {
            if (!double.TryParse(_temperature.Text, out var temp) || temp < 0 || temp > 2)
            {
                errors.Add("Temperature must be between 0 and 2");
            }
        }

        if (!string.IsNullOrWhiteSpace(_maxTokens.Text))
        {
            if (!int.TryParse(_maxTokens.Text, out var tokens) || tokens <= 0 || tokens > 32768)
            {
                errors.Add("Max tokens must be between 1 and 32768");
            }
        }

        if (string.IsNullOrWhiteSpace(_model.Text))
        {
            errors.Add("Model name is required");
        }

        return errors;
    }

    private System.Collections.Generic.List<string> GetTranscriberValidationErrors()
    {
        var errors = new System.Collections.Generic.List<string>();

        if (!string.IsNullOrWhiteSpace(_transcriberBaseUrl!.Text))
        {
            if (
                !Uri.TryCreate(_transcriberBaseUrl.Text, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
            )
            {
                errors.Add("Transcriber Base URL must be a valid HTTP/HTTPS URL");
            }
        }

        if (!string.IsNullOrWhiteSpace(_transcriberTimeout!.Text))
        {
            if (
                !int.TryParse(_transcriberTimeout.Text, out var timeout)
                || timeout <= 0
                || timeout > 300
            )
            {
                errors.Add("Timeout must be between 1 and 300 seconds");
            }
        }

        if (string.IsNullOrWhiteSpace(_transcriberModel!.Text))
        {
            errors.Add("Model name is required for transcriber");
        }

        if (!string.IsNullOrWhiteSpace(_transcriberAutoEnhanceThreshold!.Text))
        {
            if (
                !int.TryParse(_transcriberAutoEnhanceThreshold.Text, out var threshold)
                || threshold < 10
                || threshold > 10000
            )
            {
                errors.Add("Auto-enhance threshold must be between 10 and 10000 characters");
            }
        }

        if (!string.IsNullOrWhiteSpace(_wsConnectionTimeout!.Text))
        {
            if (
                !int.TryParse(_wsConnectionTimeout.Text, out var connTimeout)
                || connTimeout < 1
                || connTimeout > 120
            )
            {
                errors.Add("Connection timeout must be between 1 and 120 seconds");
            }
        }

        if (!string.IsNullOrWhiteSpace(_wsReceiveTimeout!.Text))
        {
            if (
                !int.TryParse(_wsReceiveTimeout.Text, out var recvTimeout)
                || recvTimeout < 1
                || recvTimeout > 120
            )
            {
                errors.Add("Receive timeout must be between 1 and 120 seconds");
            }
        }

        if (!string.IsNullOrWhiteSpace(_wsSendTimeout!.Text))
        {
            if (
                !int.TryParse(_wsSendTimeout.Text, out var sendTimeout)
                || sendTimeout < 1
                || sendTimeout > 120
            )
            {
                errors.Add("Send timeout must be between 1 and 120 seconds");
            }
        }

        if (!string.IsNullOrWhiteSpace(_wsHeartbeatInterval!.Text))
        {
            if (
                !int.TryParse(_wsHeartbeatInterval.Text, out var hbInterval)
                || hbInterval < 5
                || hbInterval > 60
            )
            {
                errors.Add("Heartbeat interval must be between 5 and 60 seconds");
            }
        }

        if (!string.IsNullOrWhiteSpace(_wsHeartbeatTimeout!.Text))
        {
            if (
                !int.TryParse(_wsHeartbeatTimeout.Text, out var hbTimeout)
                || hbTimeout < 10
                || hbTimeout > 120
            )
            {
                errors.Add("Heartbeat timeout must be between 10 and 120 seconds");
            }
        }

        return errors;
    }

    private System.Collections.Generic.List<string> GetHotkeyValidationErrors()
    {
        var errors = new System.Collections.Generic.List<string>();

        AddHotkeyValidationError(errors, HotkeyTarget.Llm, _cfg.Hotkey, "LLM hotkey");
        AddHotkeyValidationError(
            errors,
            HotkeyTarget.Transcriber,
            _cfg.TranscriberHotkey,
            "Transcriber hotkey"
        );
        AddHotkeyValidationError(
            errors,
            HotkeyTarget.Typeless,
            _cfg.TypelessHotkey,
            "Push-to-talk hotkey"
        );
        AddHotkeyValidationError(
            errors,
            HotkeyTarget.Streaming,
            _cfg.StreamingTranscriberHotkey,
            "Realtime streaming hotkey"
        );

        return errors;
    }

    private void AddHotkeyValidationError(
        System.Collections.Generic.List<string> errors,
        HotkeyTarget target,
        HotkeyConfig hotkey,
        string displayName
    )
    {
        if (hotkey.Modifiers == 0)
        {
            return;
        }

        if (hotkey.Key == 0 && target != HotkeyTarget.Typeless)
        {
            return;
        }

        var validationError = ValidateCapturedHotkey(target, hotkey.Modifiers, hotkey.Key);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            errors.Add($"{displayName}: {validationError}");
        }
    }

    private async void TestConnection(object? sender, EventArgs e)
    {
        try
        {
            _testConnectionButton.Enabled = false;
            _testConnectionButton.Text = "Testing...";
            _llmTestResultLabel.Text = "";

            var testConfig = new LlmConfig
            {
                Enabled = true,
                BaseUrl = _baseUrl.Text.Trim(),
                Model = _model.Text.Trim(),
                Temperature = double.TryParse(_temperature.Text.Trim(), out var t) ? t : 0.2,
                MaxTokens = string.IsNullOrWhiteSpace(_maxTokens.Text)
                    ? null
                    : (int?)int.Parse(_maxTokens.Text),
                RefinementPrompt = string.IsNullOrWhiteSpace(_refinementPrompt.Text)
                    ? LlmConfig.DefaultRefinementPrompt
                    : _refinementPrompt.Text.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(_apiKey.Text.Trim())
                    ? _cfg.Llm.ApiKey
                    : _apiKey.Text.Trim(),
                HttpReferer = string.IsNullOrWhiteSpace(_referer.Text)
                    ? null
                    : _referer.Text.Trim(),
                XTitle = string.IsNullOrWhiteSpace(_xTitle.Text) ? null : _xTitle.Text.Trim(),
            };

            var testRefiner = _textRefinerFactory.Create(testConfig);
            await testRefiner.RefineAsync(
                "Test connection",
                System.Threading.CancellationToken.None
            );

            _llmTestResultLabel.Text = "\u2713 OK";
            _llmTestResultLabel.ForeColor = UiTheme.SuccessText;
        }
        catch (Exception ex)
        {
            var apiKeyToUse = string.IsNullOrWhiteSpace(_apiKey.Text.Trim())
                ? _cfg.Llm.ApiKey
                : _apiKey.Text.Trim();
            var errorMsg = string.IsNullOrWhiteSpace(apiKeyToUse)
                ? "API key is required"
                : ex.Message;
            _llmTestResultLabel.Text = $"\u2717 {errorMsg}";
            _llmTestResultLabel.ForeColor = UiTheme.ErrorText;
        }
        finally
        {
            _testConnectionButton.Enabled = true;
            _testConnectionButton.Text = "Test LLM Connection";
        }
    }

    private async void TestTranscriberConnection(object? sender, EventArgs e)
    {
        try
        {
            _testTranscriberConnectionButton!.Enabled = false;
            _testTranscriberConnectionButton!.Text = "Testing...";
            _transcriberTestResultLabel!.Text = "";

            var testConfig = new TranscriberConfig
            {
                Enabled = true,
                BaseUrl = _transcriberBaseUrl!.Text.Trim(),
                Model = _transcriberModel!.Text.Trim(),
                TimeoutSeconds = int.TryParse(_transcriberTimeout!.Text.Trim(), out var t) ? t : 30,
                ApiKey = string.IsNullOrWhiteSpace(_transcriberApiKey!.Text.Trim())
                    ? _cfg.Transcriber.ApiKey
                    : _transcriberApiKey!.Text.Trim(),
            };

            var testTranscriber = _remoteTranscriberFactory.Create(testConfig);
            await testTranscriber.TestConnectionAsync();

            _transcriberTestResultLabel!.Text = "\u2713 OK";
            _transcriberTestResultLabel!.ForeColor = UiTheme.SuccessText;
        }
        catch (Exception ex)
        {
            var errorMessage = ex.Message;
            if (ex.InnerException != null)
            {
                errorMessage += ": " + ex.InnerException.Message;
            }
            _transcriberTestResultLabel!.Text = $"\u2717 {errorMessage}";
            _transcriberTestResultLabel!.ForeColor = UiTheme.ErrorText;
        }
        finally
        {
            _testTranscriberConnectionButton!.Enabled = true;
            _testTranscriberConnectionButton!.Text = "Test ASR Connection";
        }
    }

    private void CaptureTranscriberHotkey(object? sender, EventArgs e)
    {
        using var cap = CreateHotkeyCaptureDialog(HotkeyTarget.Transcriber);
        if (cap.ShowDialog(this) != DialogResult.OK)
            return;
        _cfg.TranscriberHotkey.Modifiers = cap.Modifiers;
        _cfg.TranscriberHotkey.Key = cap.Key;
        _transcriberHotkey!.Text = GetHotkeyDisplay(_cfg.TranscriberHotkey);
        RefreshValidationState(null, EventArgs.Empty);
        Logger.Log(
            $"Transcriber hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private void CaptureTypelessHotkey(object? sender, EventArgs e)
    {
        using var cap = CreateHotkeyCaptureDialog(HotkeyTarget.Typeless);
        if (cap.ShowDialog(this) != DialogResult.OK)
            return;
        _cfg.TypelessHotkey.Modifiers = cap.Modifiers;
        _cfg.TypelessHotkey.Key = cap.Key;
        _cfg.TypelessHotkey.RightAltOnly = cap.RightAltOnly;
        _typelessHotkey!.Text = GetHotkeyDisplay(_cfg.TypelessHotkey);
        RefreshValidationState(null, EventArgs.Empty);
        Logger.Log(
            $"Typeless hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private void CaptureStreamingTranscriberHotkey(object? sender, EventArgs e)
    {
        using var cap = CreateHotkeyCaptureDialog(HotkeyTarget.Streaming);
        if (cap.ShowDialog(this) != DialogResult.OK)
            return;
        _cfg.StreamingTranscriberHotkey.Modifiers = cap.Modifiers;
        _cfg.StreamingTranscriberHotkey.Key = cap.Key;
        _streamingTranscriberHotkey!.Text = GetHotkeyDisplay(_cfg.StreamingTranscriberHotkey);
        RefreshValidationState(null, EventArgs.Empty);
        Logger.Log(
            $"Streaming hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private void CaptureLlmHotkey(object? sender, EventArgs e)
    {
        using var cap = CreateHotkeyCaptureDialog(HotkeyTarget.Llm);
        if (cap.ShowDialog(this) != DialogResult.OK)
            return;
        _cfg.Hotkey.Modifiers = cap.Modifiers;
        _cfg.Hotkey.Key = cap.Key;
        _llmHotkey.Text = GetHotkeyDisplay(_cfg.Hotkey);
        RefreshValidationState(null, EventArgs.Empty);
        Logger.Log(
            $"LLM hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private HotkeyCaptureForm CreateHotkeyCaptureDialog(HotkeyTarget target)
    {
        bool allowModifierOnly = target == HotkeyTarget.Typeless;
        string? promptText = allowModifierOnly
            ? "Press a hold-to-talk hotkey.\r\nYou can use modifiers only (for example Ctrl+Win) or include a normal key."
            : null;

        return new HotkeyCaptureForm(
            validator: (mods, key) => ValidateCapturedHotkey(target, mods, key),
            promptText: promptText,
            allowModifierOnly: allowModifierOnly
        );
    }

    private string? ValidateCapturedHotkey(HotkeyTarget target, uint mods, uint key)
    {
        if (mods == 0)
        {
            return "Hotkey must include at least one modifier.";
        }

        if (key == 0 && target != HotkeyTarget.Typeless)
        {
            return "Hotkey must include at least one modifier and one non-modifier key.";
        }

        var duplicateConflict = FindDuplicateHotkeyConflict(target, mods, key);
        if (!string.IsNullOrWhiteSpace(duplicateConflict))
        {
            return duplicateConflict;
        }

        if (target == HotkeyTarget.Typeless)
        {
            return null;
        }

        return CanRegisterHotkey(mods, key, out var registrationError) ? null : registrationError;
    }

    private string? FindDuplicateHotkeyConflict(HotkeyTarget target, uint mods, uint key)
    {
        if (target != HotkeyTarget.Llm && HotkeyMatches(_cfg.Hotkey, mods, key))
        {
            return "Already used by the LLM hotkey.";
        }

        if (target != HotkeyTarget.Transcriber && HotkeyMatches(_cfg.TranscriberHotkey, mods, key))
        {
            return "Already used by the transcription hotkey.";
        }

        if (target != HotkeyTarget.Typeless && HotkeyMatches(_cfg.TypelessHotkey, mods, key))
        {
            return "Already used by the push-to-talk hotkey.";
        }

        if (
            target != HotkeyTarget.Streaming
            && HotkeyMatches(_cfg.StreamingTranscriberHotkey, mods, key)
        )
        {
            return "Already used by the realtime streaming hotkey.";
        }

        return null;
    }

    private static bool HotkeyMatches(HotkeyConfig hotkey, uint mods, uint key)
    {
        return hotkey.Modifiers == mods && hotkey.Key == key;
    }

    private bool CanRegisterHotkey(uint mods, uint key, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (!IsHandleCreated)
        {
            return true;
        }

        if (RegisterHotKey(Handle, HotkeyProbeId, mods, key))
        {
            UnregisterHotKey(Handle, HotkeyProbeId);
            return true;
        }

        var lastError = Marshal.GetLastWin32Error();
        errorMessage =
            lastError == 1409
                ? "Already registered by another application."
                : $"Windows rejected this hotkey (error {lastError}).";
        return false;
    }

    private static string GetHotkeyDisplay(HotkeyConfig hotkey)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (hotkey.Modifiers == 0)
            hotkey.Modifiers = 0x0003;

        if ((hotkey.Modifiers & 0x0001) != 0)
            parts.Add(hotkey.RightAltOnly ? "RALT" : "ALT");
        if ((hotkey.Modifiers & 0x0002) != 0)
            parts.Add("CTRL");
        if ((hotkey.Modifiers & 0x0004) != 0)
            parts.Add("SHIFT");
        if ((hotkey.Modifiers & 0x0008) != 0)
            parts.Add("WIN");

        if (hotkey.Key == 0)
        {
            parts.Add("(hold)");
            return string.Join("+", parts);
        }

        var keyName = ((Keys)hotkey.Key).ToString();
        if (keyName.StartsWith("D") && keyName.Length == 2 && char.IsDigit(keyName[1]))
        {
            keyName = keyName.Substring(1);
        }
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

    private void ResetToDefaults(object? sender, EventArgs e)
    {
        var result = BrandedMessageBox.Show(
            "This will reset all settings to their default values. Are you sure?",
            "Reset to Defaults",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning
        );

        if (result == DialogResult.Yes)
        {
            var defaultCfg = new AppConfig();

            _autoPaste.Checked = defaultCfg.AutoPaste;
            _excludeFromClipboardHistory.Checked = defaultCfg.ExcludeFromClipboardHistory;
            _enabled.Checked = defaultCfg.Llm.Enabled;
            _clipboardFallback.Checked = defaultCfg.UseClipboardFallback;
            _baseUrl.Text = defaultCfg.Llm.BaseUrl;
            _model.Text = defaultCfg.Llm.Model;
            _temperature.Text = defaultCfg.Llm.Temperature.ToString("0.##");
            _maxTokens.Text = defaultCfg.Llm.MaxTokens?.ToString() ?? "";
            _refinementPrompt.Text = defaultCfg.Llm.GetEffectiveRefinementPrompt();
            _apiKey.Text = "";
            _referer.Text = defaultCfg.Llm.HttpReferer ?? "";
            _xTitle.Text = defaultCfg.Llm.XTitle ?? "";

            _transcriberEnabled!.Checked = defaultCfg.Transcriber.Enabled;
            _transcriberBaseUrl!.Text = defaultCfg.Transcriber.BaseUrl;
            _transcriberModel!.Text = defaultCfg.Transcriber.Model;
            _transcriberTimeout!.Text = defaultCfg.Transcriber.TimeoutSeconds.ToString();
            _transcriberApiKey!.Text = "";
            _transcriberAutoPaste!.Checked = defaultCfg.Transcriber.AutoPaste;
            _transcriberStreamResults!.Checked = defaultCfg.Transcriber.StreamResults;
            _transcriberEnableAutoEnhance!.Checked = defaultCfg.Transcriber.EnableAutoEnhance;
            _transcriberAutoEnhanceThreshold!.Text =
                defaultCfg.Transcriber.AutoEnhanceThresholdChars.ToString();

            _wsConnectionTimeout!.Text =
                defaultCfg.Transcriber.WebSocketConnectionTimeoutSeconds.ToString();
            _wsReceiveTimeout!.Text =
                defaultCfg.Transcriber.WebSocketReceiveTimeoutSeconds.ToString();
            _wsSendTimeout!.Text = defaultCfg.Transcriber.WebSocketSendTimeoutSeconds.ToString();
            _wsHeartbeatInterval!.Text =
                defaultCfg.Transcriber.WebSocketHeartbeatIntervalSeconds.ToString();
            _wsHeartbeatTimeout!.Text =
                defaultCfg.Transcriber.WebSocketHeartbeatTimeoutSeconds.ToString();
            _realtimeProviderDropdown!.SelectedItem = defaultCfg.Transcriber.RealtimeProvider;
            if (_transcriberLanguage != null)
                _transcriberLanguage.Text = defaultCfg.Transcriber.Language ?? "";
            if (_transcriberRealtimeSessionPrompt != null)
                _transcriberRealtimeSessionPrompt.Text =
                    defaultCfg.Transcriber.RealtimeSessionPrompt ?? "";
            if (
                defaultCfg.Transcriber.PreferredMicrophoneIndex >= 0
                && defaultCfg.Transcriber.PreferredMicrophoneIndex
                    < _microphoneDropdown!.Items.Count
            )
                _microphoneDropdown.SelectedIndex = defaultCfg.Transcriber.PreferredMicrophoneIndex;

            _cfg.Hotkey.Modifiers = defaultCfg.Hotkey.Modifiers;
            _cfg.Hotkey.Key = defaultCfg.Hotkey.Key;
            _llmHotkey.Text = GetHotkeyDisplay(defaultCfg.Hotkey);

            _cfg.TranscriberHotkey.Modifiers = defaultCfg.TranscriberHotkey.Modifiers;
            _cfg.TranscriberHotkey.Key = defaultCfg.TranscriberHotkey.Key;
            _transcriberHotkey!.Text = GetHotkeyDisplay(defaultCfg.TranscriberHotkey);

            _cfg.TypelessHotkey.Modifiers = defaultCfg.TypelessHotkey.Modifiers;
            _cfg.TypelessHotkey.Key = defaultCfg.TypelessHotkey.Key;
            _cfg.TypelessHotkey.RightAltOnly = defaultCfg.TypelessHotkey.RightAltOnly;
            _typelessHotkey!.Text = GetHotkeyDisplay(defaultCfg.TypelessHotkey);

            _cfg.StreamingTranscriberHotkey.Modifiers = defaultCfg
                .StreamingTranscriberHotkey
                .Modifiers;
            _cfg.StreamingTranscriberHotkey.Key = defaultCfg.StreamingTranscriberHotkey.Key;
            _streamingTranscriberHotkey!.Text = GetHotkeyDisplay(
                defaultCfg.StreamingTranscriberHotkey
            );

            RefreshValidationState(null, EventArgs.Empty);

            NotificationService.ShowInfo("Settings reset to defaults.");
        }
    }
}
