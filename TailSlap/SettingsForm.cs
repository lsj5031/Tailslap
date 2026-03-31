using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ITextRefinerFactory _textRefinerFactory;
    private readonly IRemoteTranscriberFactory _remoteTranscriberFactory;

    private CheckBox _enabled;
    private CheckBox _autoPaste;
    private CheckBox _clipboardFallback;
    private TextBox _baseUrl;
    private TextBox _model;
    private TextBox _temperature;
    private TextBox _maxTokens;
    private TextBox _refinementPrompt;
    private TextBox _apiKey;
    private TextBox _referer;
    private TextBox _xTitle;
    private TextBox _llmHotkey;
    private Button _resetButton;
    private Button _testConnectionButton;
    private Button _captureLlmHotkeyButton;
    private Label _validationLabel;
    private Label _llmTestResultLabel;

    // Transcriber controls
    private CheckBox? _transcriberEnabled;
    private CheckBox? _transcriberAutoPaste;
    private CheckBox? _transcriberStreamResults;
    private TextBox? _transcriberBaseUrl;
    private TextBox? _transcriberModel;
    private TextBox? _transcriberTimeout;
    private TextBox? _transcriberApiKey;
    private TextBox? _transcriberHotkey;
    private ComboBox? _microphoneDropdown;
    private Button? _captureTranscriberHotkeyButton;
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

    // WebSocket timeout controls
    private TextBox? _wsConnectionTimeout;
    private TextBox? _wsReceiveTimeout;
    private TextBox? _wsSendTimeout;
    private TextBox? _wsHeartbeatInterval;
    private TextBox? _wsHeartbeatTimeout;

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
        Width = DpiHelper.Scale(680);
        Height = DpiHelper.Scale(560);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(DpiHelper.Scale(600), DpiHelper.Scale(500));
        SizeGripStyle = SizeGripStyle.Show;
        Icon = MainForm.LoadMainIcon();

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // General tab
        var general = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(16)),
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(110)));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        general.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        general.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _autoPaste = new CheckBox
        {
            Text = "Auto Paste after refine",
            Checked = _cfg.AutoPaste,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        general.Controls.Add(
            new Label
            {
                Text = "Auto Paste",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            0
        );
        general.Controls.Add(_autoPaste, 1, 0);
        _clipboardFallback = new CheckBox
        {
            Text = "Use clipboard when no selection is captured",
            Checked = _cfg.UseClipboardFallback,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        general.Controls.Add(
            new Label
            {
                Text = "Clipboard Fallback",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            1
        );
        general.Controls.Add(_clipboardFallback, 1, 1);

        // LLM tab
        var llm = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(16)),
            RowCount = 12,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(130)));
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 12; i++)
            llm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _enabled = new CheckBox
        {
            Text = "Enable LLM Processing",
            Checked = _cfg.Llm.Enabled,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        llm.Controls.Add(
            new Label
            {
                Text = "Enabled",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            0
        );
        llm.Controls.Add(_enabled, 1, 0);
        _baseUrl = new TextBox { Text = _cfg.Llm.BaseUrl, Dock = DockStyle.Fill };
        llm.Controls.Add(
            new Label
            {
                Text = "Base URL",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            1
        );
        llm.Controls.Add(_baseUrl, 1, 1);
        _model = new TextBox { Text = _cfg.Llm.Model, Dock = DockStyle.Fill };
        llm.Controls.Add(
            new Label
            {
                Text = "Model",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            2
        );
        llm.Controls.Add(_model, 1, 2);
        _temperature = new TextBox
        {
            Text = _cfg.Llm.Temperature.ToString("0.##"),
            Dock = DockStyle.Fill,
        };
        llm.Controls.Add(
            new Label
            {
                Text = "Temperature",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            3
        );
        llm.Controls.Add(_temperature, 1, 3);
        _maxTokens = new TextBox
        {
            Text = _cfg.Llm.MaxTokens?.ToString() ?? "",
            Dock = DockStyle.Fill,
        };
        llm.Controls.Add(
            new Label
            {
                Text = "Max Tokens",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            4
        );
        llm.Controls.Add(_maxTokens, 1, 4);
        _apiKey = new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = "Enter API key (leave blank to keep existing)",
            Dock = DockStyle.Fill,
        };
        llm.Controls.Add(
            new Label
            {
                Text = "API Key",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            5
        );
        llm.Controls.Add(_apiKey, 1, 5);
        _refinementPrompt = new TextBox
        {
            Text = _cfg.Llm.GetEffectiveRefinementPrompt(),
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Height = DpiHelper.Scale(150),
        };
        llm.Controls.Add(
            new Label
            {
                Text = "Prompt",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
            },
            0,
            6
        );
        llm.Controls.Add(_refinementPrompt, 1, 6);
        _referer = new TextBox
        {
            Text = _cfg.Llm.HttpReferer ?? string.Empty,
            Dock = DockStyle.Fill,
        };
        llm.Controls.Add(
            new Label
            {
                Text = "HTTP Referer",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            7
        );
        llm.Controls.Add(_referer, 1, 7);
        _xTitle = new TextBox { Text = _cfg.Llm.XTitle ?? string.Empty, Dock = DockStyle.Fill };
        llm.Controls.Add(
            new Label
            {
                Text = "X-Title",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            8
        );
        llm.Controls.Add(_xTitle, 1, 8);
        _llmHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.Hotkey),
            Dock = DockStyle.Fill,
        };
        llm.Controls.Add(
            new Label
            {
                Text = "Hotkey",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            9
        );
        llm.Controls.Add(_llmHotkey, 1, 9);
        _captureLlmHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureLlmHotkeyButton.Click += CaptureLlmHotkey;
        llm.Controls.Add(_captureLlmHotkeyButton, 1, 10);
        _testConnectionButton = new Button
        {
            Text = "Test LLM Connection",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _testConnectionButton.Click += TestConnection;
        _llmTestResultLabel = new Label
        {
            Text = "",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
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
        llm.Controls.Add(
            new Label
            {
                Text = "Test Connection",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            11
        );
        llm.Controls.Add(llmTestRow, 1, 11);

        // Add validation label and buttons
        _validationLabel = new Label
        {
            Text = "",
            ForeColor = Color.Red,
            AutoSize = true,
            Dock = DockStyle.Bottom,
            Padding = DpiHelper.Scale(new Padding(10)),
        };
        _resetButton = new Button
        {
            Text = "Reset to Defaults",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _resetButton.Click += ResetToDefaults;

        var generalPage = new TabPage("General") { AutoScroll = true };
        generalPage.Controls.Add(general);
        var llmPage = new TabPage("LLM") { AutoScroll = true };
        llmPage.Controls.Add(llm);
        var transcriber = CreateTranscriberTab();
        var transcriberPage = new TabPage("Transcriber") { AutoScroll = true };
        transcriberPage.Controls.Add(transcriber);
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(llmPage);
        tabs.TabPages.Add(transcriberPage);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Padding = DpiHelper.Scale(new Padding(10)),
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

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_resetButton);

        Controls.Add(tabs);
        Controls.Add(buttons);
        Controls.Add(_validationLabel);

        AcceptButton = ok;
        CancelButton = cancel;

        ok.Click += (_, __) => ApplyChanges();

        // Add real-time validation
        _baseUrl.TextChanged += ValidateInput;
        _temperature.TextChanged += ValidateInput;
        _maxTokens.TextChanged += ValidateInput;
        _model.TextChanged += ValidateInput;
        _apiKey.TextChanged += (_, _) => _llmTestResultLabel.Text = "";
        _baseUrl.TextChanged += (_, _) => _llmTestResultLabel.Text = "";
        _transcriberBaseUrl!.TextChanged += ValidateTranscriberInput;
        _transcriberModel!.TextChanged += ValidateTranscriberInput;
        _transcriberTimeout!.TextChanged += ValidateTranscriberInput;
        _transcriberAutoEnhanceThreshold!.TextChanged += ValidateTranscriberInput;
        _transcriberBaseUrl!.TextChanged += (_, _) => _transcriberTestResultLabel!.Text = "";
        _transcriberApiKey!.TextChanged += (_, _) => _transcriberTestResultLabel!.Text = "";
    }

    private TableLayoutPanel CreateTranscriberTab()
    {
        var transcriber = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = DpiHelper.Scale(new Padding(16)),
            RowCount = 24,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        transcriber.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiHelper.Scale(140)));
        transcriber.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 24; i++)
            transcriber.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _transcriberEnabled = new CheckBox
        {
            Text = "Enable Remote Transcription",
            Checked = _cfg.Transcriber.Enabled,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Enabled",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            0
        );
        transcriber.Controls.Add(_transcriberEnabled, 1, 0);

        _transcriberBaseUrl = new TextBox
        {
            Text = _cfg.Transcriber.BaseUrl,
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "API Endpoint (root, e.g. http://localhost:18000/v1)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            1
        );
        transcriber.Controls.Add(_transcriberBaseUrl, 1, 1);

        _transcriberModel = new TextBox { Text = _cfg.Transcriber.Model, Dock = DockStyle.Fill };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Model",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            2
        );
        transcriber.Controls.Add(_transcriberModel, 1, 2);

        _transcriberTimeout = new TextBox
        {
            Text = _cfg.Transcriber.TimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Timeout (sec)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            3
        );
        transcriber.Controls.Add(_transcriberTimeout, 1, 3);

        _transcriberApiKey = new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = "Enter API key (leave blank to keep existing)",
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "API Key",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            4
        );
        transcriber.Controls.Add(_transcriberApiKey, 1, 4);

        _transcriberAutoPaste = new CheckBox
        {
            Text = "Auto Paste after transcription",
            Checked = _cfg.Transcriber.AutoPaste,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Auto Paste",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            5
        );
        transcriber.Controls.Add(_transcriberAutoPaste, 1, 5);

        _transcriberStreamResults = new CheckBox
        {
            Text = "Type words as they arrive (after recording)",
            Checked = _cfg.Transcriber.StreamResults,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Stream Results",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            6
        );
        transcriber.Controls.Add(_transcriberStreamResults, 1, 6);

        _transcriberEnableVAD = new CheckBox
        {
            Text = "Auto-stop recording after silence",
            Checked = _cfg.Transcriber.EnableVAD,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Silence Detection",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            7
        );
        transcriber.Controls.Add(_transcriberEnableVAD, 1, 7);

        _transcriberSilenceThreshold = new TextBox
        {
            Text = _cfg.Transcriber.SilenceThresholdMs.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Silence Timeout (ms)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            8
        );
        transcriber.Controls.Add(_transcriberSilenceThreshold, 1, 8);

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
        // Map current settings to index: Low=900/550 (hard to trigger), Medium=500/300, High=200/100 (easy to trigger)
        // Detect current sensitivity based on activation threshold
        if (_cfg.Transcriber.VadActivationThreshold >= 800)
            _transcriberVadSensitivity.SelectedIndex = 0; // Low Sensitivity (Noisy)
        else if (_cfg.Transcriber.VadActivationThreshold >= 400)
            _transcriberVadSensitivity.SelectedIndex = 1; // Medium
        else
            _transcriberVadSensitivity.SelectedIndex = 2; // High Sensitivity (Quiet)

        transcriber.Controls.Add(
            new Label
            {
                Text = "VAD Sensitivity",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            9
        );
        transcriber.Controls.Add(_transcriberVadSensitivity, 1, 9);

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
        transcriber.Controls.Add(
            new Label
            {
                Text = "Microphone",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            10
        );
        transcriber.Controls.Add(_microphoneDropdown, 1, 10);

        _detectMicrophonesButton = new Button
        {
            Text = "Detect Microphones",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _detectMicrophonesButton!.Click += DetectMicrophones;
        transcriber.Controls.Add(_detectMicrophonesButton, 1, 11);

        _transcriberEnableAutoEnhance = new CheckBox
        {
            Text = "Auto-enhance long transcriptions with LLM",
            Checked = _cfg.Transcriber.EnableAutoEnhance,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Auto-Enhance",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            12
        );
        transcriber.Controls.Add(_transcriberEnableAutoEnhance, 1, 12);

        _transcriberAutoEnhanceThreshold = new TextBox
        {
            Text = _cfg.Transcriber.AutoEnhanceThresholdChars.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Enhance Threshold (chars)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            13
        );
        transcriber.Controls.Add(_transcriberAutoEnhanceThreshold, 1, 13);

        _transcriberHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.TranscriberHotkey),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Hotkey",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            14
        );
        transcriber.Controls.Add(_transcriberHotkey, 1, 14);

        _captureTranscriberHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureTranscriberHotkeyButton!.Click += CaptureTranscriberHotkey;
        transcriber.Controls.Add(_captureTranscriberHotkeyButton, 1, 15);

        _testTranscriberConnectionButton = new Button
        {
            Text = "Test Transcription API",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _testTranscriberConnectionButton!.Click += TestTranscriberConnection;
        _transcriberTestResultLabel = new Label
        {
            Text = "",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
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
        transcriber.Controls.Add(
            new Label
            {
                Text = "Test Connection",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            16
        );
        transcriber.Controls.Add(transcriberTestRow, 1, 16);

        // Push-to-Talk (Typeless) Hotkey
        _typelessHotkey = new TextBox
        {
            ReadOnly = true,
            Text = GetHotkeyDisplay(_cfg.TypelessHotkey),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Push-to-Talk Hotkey",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            17
        );
        transcriber.Controls.Add(_typelessHotkey, 1, 17);

        _captureTypelessHotkeyButton = new Button
        {
            Text = "Change Hotkey",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        _captureTypelessHotkeyButton!.Click += CaptureTypelessHotkey;
        transcriber.Controls.Add(_captureTypelessHotkeyButton, 1, 18);

        // WebSocket Timeout Settings Section
        transcriber.Controls.Add(
            new Label
            {
                Text = "WebSocket Timeout Settings",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(this.Font, FontStyle.Bold),
            },
            0,
            19
        );
        transcriber.Controls.Add(new Label(), 1, 19);

        _wsConnectionTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketConnectionTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Connection Timeout (sec)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            20
        );
        transcriber.Controls.Add(_wsConnectionTimeout, 1, 20);

        _wsReceiveTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketReceiveTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Receive Timeout (sec)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            21
        );
        transcriber.Controls.Add(_wsReceiveTimeout, 1, 21);

        _wsSendTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketSendTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Send Timeout (sec)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            22
        );
        transcriber.Controls.Add(_wsSendTimeout, 1, 22);

        _wsHeartbeatInterval = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketHeartbeatIntervalSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Heartbeat Interval (sec)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            23
        );
        transcriber.Controls.Add(_wsHeartbeatInterval, 1, 23);

        _wsHeartbeatTimeout = new TextBox
        {
            Text = _cfg.Transcriber.WebSocketHeartbeatTimeoutSeconds.ToString(),
            Dock = DockStyle.Fill,
        };
        transcriber.Controls.Add(
            new Label
            {
                Text = "Heartbeat Timeout (sec)",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            24
        );
        transcriber.Controls.Add(_wsHeartbeatTimeout, 1, 24);

        // Add validation handlers
        _wsConnectionTimeout.TextChanged += ValidateTranscriberInput;
        _wsReceiveTimeout.TextChanged += ValidateTranscriberInput;
        _wsSendTimeout.TextChanged += ValidateTranscriberInput;
        _wsHeartbeatInterval.TextChanged += ValidateTranscriberInput;
        _wsHeartbeatTimeout.TextChanged += ValidateTranscriberInput;

        _realtimeProviderDropdown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
        };
        _realtimeProviderDropdown.Items.AddRange(new object[] { "custom", "openai" });
        _realtimeProviderDropdown.SelectedItem = string.Equals(
            _cfg.Transcriber.RealtimeProvider,
            "openai",
            StringComparison.OrdinalIgnoreCase
        )
            ? "openai"
            : "custom";
        transcriber.Controls.Add(
            new Label
            {
                Text = "Realtime Provider",
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            },
            0,
            25
        );
        transcriber.Controls.Add(_realtimeProviderDropdown, 1, 25);

        return transcriber;
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
        // Only update LLM API key if user actually entered something (preserve existing if blank)

        var k = _apiKey.Text.Trim();
        _cfg.Llm.ApiKey = string.IsNullOrWhiteSpace(k) ? _cfg.Llm.ApiKey : k;
        _cfg.Llm.HttpReferer = string.IsNullOrWhiteSpace(_referer.Text)
            ? null
            : _referer.Text.Trim();
        _cfg.Llm.XTitle = string.IsNullOrWhiteSpace(_xTitle.Text) ? null : _xTitle.Text.Trim();
        _cfg.UseClipboardFallback = _clipboardFallback.Checked;

        // Apply transcriber changes
        _cfg.Transcriber.Enabled = _transcriberEnabled!.Checked;
        _cfg.Transcriber.BaseUrl = _transcriberBaseUrl!.Text.Trim();
        _cfg.Transcriber.Model = _transcriberModel!.Text.Trim();
        if (int.TryParse(_transcriberTimeout!.Text.Trim(), out var timeout))
            _cfg.Transcriber.TimeoutSeconds = timeout;
        // Only update transcriber API key if user actually entered something (preserve existing if blank)

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

        // Apply VAD Sensitivity
        if (_transcriberVadSensitivity != null && _transcriberVadSensitivity.SelectedIndex >= 0)
        {
            switch (_transcriberVadSensitivity.SelectedIndex)
            {
                case 0: // Low Sensitivity (Noisy Environment) - Hard to trigger, wide hysteresis
                    _cfg.Transcriber.VadActivationThreshold = 900;
                    _cfg.Transcriber.VadSustainThreshold = 250; // Much lower sustain to not stop too early
                    _cfg.Transcriber.VadSilenceThreshold = 120;
                    break;
                case 1: // Medium (Default) - Balanced
                    _cfg.Transcriber.VadActivationThreshold = 600;
                    _cfg.Transcriber.VadSustainThreshold = 180; // Lower sustain to catch softer speech
                    _cfg.Transcriber.VadSilenceThreshold = 80;
                    break;
                case 2: // High Sensitivity (Quiet Environment) - Easy to trigger
                    _cfg.Transcriber.VadActivationThreshold = 300;
                    _cfg.Transcriber.VadSustainThreshold = 100;
                    _cfg.Transcriber.VadSilenceThreshold = 40;
                    break;
            }
        }

        _cfg.Transcriber.PreferredMicrophoneIndex =
            _microphoneDropdown!.SelectedIndex >= 0 ? _microphoneDropdown.SelectedIndex : -1;

        // Apply auto-enhance settings
        _cfg.Transcriber.EnableAutoEnhance = _transcriberEnableAutoEnhance!.Checked;
        if (
            int.TryParse(_transcriberAutoEnhanceThreshold!.Text.Trim(), out var thresholdChars)
            && thresholdChars >= 10
            && thresholdChars <= 10000
        )
            _cfg.Transcriber.AutoEnhanceThresholdChars = thresholdChars;

        // Apply WebSocket timeout settings
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

        _cfg.Transcriber.RealtimeProvider = string.Equals(
            _realtimeProviderDropdown?.SelectedItem?.ToString(),
            "openai",
            StringComparison.OrdinalIgnoreCase
        )
            ? "openai"
            : "custom";
    }

    private void ValidateInput(object? sender, EventArgs e)
    {
        var errors = new System.Collections.Generic.List<string>();

        // Validate URL
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

        // Validate temperature
        if (!string.IsNullOrWhiteSpace(_temperature.Text))
        {
            if (!double.TryParse(_temperature.Text, out var temp) || temp < 0 || temp > 2)
            {
                errors.Add("Temperature must be between 0 and 2");
            }
        }

        // Validate max tokens
        if (!string.IsNullOrWhiteSpace(_maxTokens.Text))
        {
            if (!int.TryParse(_maxTokens.Text, out var tokens) || tokens <= 0 || tokens > 32768)
            {
                errors.Add("Max tokens must be between 1 and 32768");
            }
        }

        // Validate model name
        if (string.IsNullOrWhiteSpace(_model.Text))
        {
            errors.Add("Model name is required");
        }

        _validationLabel.Text = errors.Count > 0 ? string.Join("\n", errors) : "";
        _validationLabel.ForeColor = errors.Count > 0 ? Color.Red : Color.Green;
    }

    private void ValidateTranscriberInput(object? sender, EventArgs e)
    {
        var errors = new System.Collections.Generic.List<string>();

        // Validate URL
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

        // Validate timeout
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

        // Validate model name
        if (string.IsNullOrWhiteSpace(_transcriberModel!.Text))
        {
            errors.Add("Model name is required for transcriber");
        }

        // Validate auto-enhance threshold
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

        // Validate WebSocket timeout settings
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

        _validationLabel.Text = errors.Count > 0 ? string.Join("\n", errors) : "";
        _validationLabel.ForeColor = errors.Count > 0 ? Color.Red : Color.Green;
    }

    private bool ValidateAllInput()
    {
        ValidateInput(null, EventArgs.Empty!);
        ValidateTranscriberInput(null, EventArgs.Empty!);
        return string.IsNullOrEmpty(_validationLabel.Text)
            || _validationLabel.ForeColor == Color.Green;
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
            _llmTestResultLabel.ForeColor = Color.Green;
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
            _llmTestResultLabel.ForeColor = Color.Red;
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
            _transcriberTestResultLabel!.ForeColor = Color.Green;
        }
        catch (Exception ex)
        {
            var errorMessage = ex.Message;
            if (ex.InnerException != null)
            {
                errorMessage += ": " + ex.InnerException.Message;
            }
            _transcriberTestResultLabel!.Text = $"\u2717 {errorMessage}";
            _transcriberTestResultLabel!.ForeColor = Color.Red;
        }
        finally
        {
            _testTranscriberConnectionButton!.Enabled = true;
            _testTranscriberConnectionButton!.Text = "Test Transcription API";
        }
    }

    private void CaptureTranscriberHotkey(object? sender, EventArgs e)
    {
        using var cap = new HotkeyCaptureForm();
        if (cap.ShowDialog() != DialogResult.OK)
            return;
        _cfg.TranscriberHotkey.Modifiers = cap.Modifiers;
        _cfg.TranscriberHotkey.Key = cap.Key;
        _transcriberHotkey!.Text = GetHotkeyDisplay(_cfg.TranscriberHotkey);
        Logger.Log(
            $"Transcriber hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private void CaptureTypelessHotkey(object? sender, EventArgs e)
    {
        using var cap = new HotkeyCaptureForm();
        if (cap.ShowDialog() != DialogResult.OK)
            return;
        _cfg.TypelessHotkey.Modifiers = cap.Modifiers;
        _cfg.TypelessHotkey.Key = cap.Key;
        _typelessHotkey!.Text = GetHotkeyDisplay(_cfg.TypelessHotkey);
        Logger.Log(
            $"Typeless hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private void CaptureLlmHotkey(object? sender, EventArgs e)
    {
        using var cap = new HotkeyCaptureForm();
        if (cap.ShowDialog() != DialogResult.OK)
            return;
        _cfg.Hotkey.Modifiers = cap.Modifiers;
        _cfg.Hotkey.Key = cap.Key;
        _llmHotkey.Text = GetHotkeyDisplay(_cfg.Hotkey);
        Logger.Log(
            $"LLM hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}"
        );
    }

    private static string GetHotkeyDisplay(HotkeyConfig hotkey)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (hotkey.Modifiers == 0)
            hotkey.Modifiers = 0x0003;

        if ((hotkey.Modifiers & 0x0001) != 0)
            parts.Add("ALT");
        if ((hotkey.Modifiers & 0x0002) != 0)
            parts.Add("CTRL");
        if ((hotkey.Modifiers & 0x0004) != 0)
            parts.Add("SHIFT");
        if ((hotkey.Modifiers & 0x0008) != 0)
            parts.Add("WIN");

        // Modifier-only hotkey (Key == 0): display as "CTRL+WIN (hold)" etc.
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

            // Reset WebSocket timeout settings
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
            if (
                defaultCfg.Transcriber.PreferredMicrophoneIndex >= 0
                && defaultCfg.Transcriber.PreferredMicrophoneIndex
                    < _microphoneDropdown!.Items.Count
            )
                _microphoneDropdown.SelectedIndex = defaultCfg.Transcriber.PreferredMicrophoneIndex;

            // Reset Hotkeys in the config object as well, since they aren't read back/saved in ApplyChanges like other fields
            _cfg.Hotkey.Modifiers = defaultCfg.Hotkey.Modifiers;
            _cfg.Hotkey.Key = defaultCfg.Hotkey.Key;
            _llmHotkey.Text = GetHotkeyDisplay(defaultCfg.Hotkey);

            _cfg.TranscriberHotkey.Modifiers = defaultCfg.TranscriberHotkey.Modifiers;
            _cfg.TranscriberHotkey.Key = defaultCfg.TranscriberHotkey.Key;
            _transcriberHotkey!.Text = GetHotkeyDisplay(defaultCfg.TranscriberHotkey);

            _cfg.TypelessHotkey.Modifiers = defaultCfg.TypelessHotkey.Modifiers;
            _cfg.TypelessHotkey.Key = defaultCfg.TypelessHotkey.Key;
            _typelessHotkey!.Text = GetHotkeyDisplay(defaultCfg.TypelessHotkey);

            NotificationService.ShowInfo("Settings reset to defaults.");
        }
    }
}
