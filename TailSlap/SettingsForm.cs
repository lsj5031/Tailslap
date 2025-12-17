using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private CheckBox _enabled;
    private CheckBox _autoPaste;
    private CheckBox _clipboardFallback;
    private TextBox _baseUrl;
    private TextBox _model;
    private TextBox _temperature;
    private TextBox _maxTokens;
    private TextBox _apiKey;
    private TextBox _referer;
    private TextBox _xTitle;
    private TextBox _llmHotkey;
    private Button _resetButton;
    private Button _testConnectionButton;
    private Button _captureLlmHotkeyButton;
    private Label _validationLabel;

    // Transcriber controls
    private CheckBox? _transcriberEnabled;
    private CheckBox? _transcriberAutoPaste;
    private TextBox? _transcriberBaseUrl;
    private TextBox? _transcriberModel;
    private TextBox? _transcriberTimeout;
    private TextBox? _transcriberApiKey;
    private TextBox? _transcriberHotkey;
    private ComboBox? _microphoneDropdown;
    private Button? _captureTranscriberHotkeyButton;
    private Button? _testTranscriberConnectionButton;
    private Button? _detectMicrophonesButton;

    public SettingsForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "TailSlap Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        Width = 680; Height = 560;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(600, 500);
        SizeGripStyle = SizeGripStyle.Show;
        Icon = MainForm.LoadMainIcon();

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // General tab
        var general = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(16), RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        general.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        general.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _autoPaste = new CheckBox { Text = "Auto Paste after refine", Checked = _cfg.AutoPaste, AutoSize = true, Dock = DockStyle.Fill };
        general.Controls.Add(new Label { Text = "Auto Paste", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        general.Controls.Add(_autoPaste, 1, 0);
        _clipboardFallback = new CheckBox { Text = "Use clipboard when no selection is captured", Checked = _cfg.UseClipboardFallback, AutoSize = true, Dock = DockStyle.Fill };
        general.Controls.Add(new Label { Text = "Clipboard Fallback", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        general.Controls.Add(_clipboardFallback, 1, 1);

        // LLM tab
        var llm = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(16), RowCount = 11, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 11; i++) llm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _enabled = new CheckBox { Text = "Enable LLM Processing", Checked = _cfg.Llm.Enabled, AutoSize = true, Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "Enabled", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        llm.Controls.Add(_enabled, 1, 0);
        _baseUrl = new TextBox { Text = _cfg.Llm.BaseUrl, Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "Base URL", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        llm.Controls.Add(_baseUrl, 1, 1);
        _model = new TextBox { Text = _cfg.Llm.Model, Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "Model", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        llm.Controls.Add(_model, 1, 2);
        _temperature = new TextBox { Text = _cfg.Llm.Temperature.ToString("0.##"), Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "Temperature", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        llm.Controls.Add(_temperature, 1, 3);
        _maxTokens = new TextBox { Text = _cfg.Llm.MaxTokens?.ToString() ?? "", Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "Max Tokens", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        llm.Controls.Add(_maxTokens, 1, 4);
        _apiKey = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "Enter API key (leave blank to keep)", Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "API Key", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
        llm.Controls.Add(_apiKey, 1, 5);
        _referer = new TextBox { Text = _cfg.Llm.HttpReferer ?? string.Empty, Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "HTTP Referer", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
        llm.Controls.Add(_referer, 1, 6);
        _xTitle = new TextBox { Text = _cfg.Llm.XTitle ?? string.Empty, Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "X-Title", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 7);
        llm.Controls.Add(_xTitle, 1, 7);
        _llmHotkey = new TextBox { ReadOnly = true, Text = GetHotkeyDisplay(_cfg.Hotkey), Dock = DockStyle.Fill };
        llm.Controls.Add(new Label { Text = "Hotkey", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 8);
        llm.Controls.Add(_llmHotkey, 1, 8);
        _captureLlmHotkeyButton = new Button { Text = "Change Hotkey", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _captureLlmHotkeyButton.Click += CaptureLlmHotkey;
        llm.Controls.Add(_captureLlmHotkeyButton, 1, 9);
        _testConnectionButton = new Button { Text = "Test LLM Connection", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _testConnectionButton.Click += TestConnection;
        llm.Controls.Add(new Label { Text = "Test Connection", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 10);
        llm.Controls.Add(_testConnectionButton, 1, 10);

        // Add validation label and buttons
        _validationLabel = new Label { Text = "", ForeColor = Color.Red, AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(10) };
        _resetButton = new Button { Text = "Reset to Defaults", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
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

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_resetButton);

        Controls.Add(tabs);
        Controls.Add(buttons);
        Controls.Add(_validationLabel);

        AcceptButton = ok; CancelButton = cancel;

        ok.Click += (_, __) => ApplyChanges();
        
        // Add real-time validation
        _baseUrl.TextChanged += ValidateInput;
        _temperature.TextChanged += ValidateInput;
        _maxTokens.TextChanged += ValidateInput;
        _model.TextChanged += ValidateInput;
        _transcriberBaseUrl!.TextChanged += ValidateTranscriberInput;
        _transcriberModel!.TextChanged += ValidateTranscriberInput;
        _transcriberTimeout!.TextChanged += ValidateTranscriberInput;
    }

    private TableLayoutPanel CreateTranscriberTab()
    {
        var transcriber = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(16), RowCount = 11, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        transcriber.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        transcriber.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 11; i++) transcriber.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _transcriberEnabled = new CheckBox { Text = "Enable Remote Transcription", Checked = _cfg.Transcriber.Enabled, AutoSize = true, Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "Enabled", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        transcriber.Controls.Add(_transcriberEnabled, 1, 0);

        _transcriberBaseUrl = new TextBox { Text = _cfg.Transcriber.BaseUrl, Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "Base URL", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        transcriber.Controls.Add(_transcriberBaseUrl, 1, 1);

        _transcriberModel = new TextBox { Text = _cfg.Transcriber.Model, Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "Model", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        transcriber.Controls.Add(_transcriberModel, 1, 2);

        _transcriberTimeout = new TextBox { Text = _cfg.Transcriber.TimeoutSeconds.ToString(), Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "Timeout (sec)", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        transcriber.Controls.Add(_transcriberTimeout, 1, 3);

        _transcriberApiKey = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "Enter API key (leave blank to keep)", Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "API Key", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        transcriber.Controls.Add(_transcriberApiKey, 1, 4);

        _transcriberAutoPaste = new CheckBox { Text = "Auto Paste after transcription", Checked = _cfg.Transcriber.AutoPaste, AutoSize = true, Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "Auto Paste", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
        transcriber.Controls.Add(_transcriberAutoPaste, 1, 5);

        _microphoneDropdown = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        RefreshMicrophoneList();
        if (_cfg.Transcriber.PreferredMicrophoneIndex >= 0 && _cfg.Transcriber.PreferredMicrophoneIndex < _microphoneDropdown.Items.Count)
            _microphoneDropdown.SelectedIndex = _cfg.Transcriber.PreferredMicrophoneIndex;
        else if (_microphoneDropdown.Items.Count > 0)
            _microphoneDropdown.SelectedIndex = 0;
        transcriber.Controls.Add(new Label { Text = "Microphone", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 6);
        transcriber.Controls.Add(_microphoneDropdown, 1, 6);

        _detectMicrophonesButton = new Button { Text = "Detect Microphones", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _detectMicrophonesButton!.Click += DetectMicrophones;
        transcriber.Controls.Add(_detectMicrophonesButton, 1, 7);

        _transcriberHotkey = new TextBox { ReadOnly = true, Text = GetHotkeyDisplay(_cfg.TranscriberHotkey), Dock = DockStyle.Fill };
        transcriber.Controls.Add(new Label { Text = "Hotkey", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 8);
        transcriber.Controls.Add(_transcriberHotkey, 1, 8);

        _captureTranscriberHotkeyButton = new Button { Text = "Change Hotkey", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _captureTranscriberHotkeyButton!.Click += CaptureTranscriberHotkey;
        transcriber.Controls.Add(_captureTranscriberHotkeyButton, 1, 9);

        _testTranscriberConnectionButton = new Button { Text = "Test Transcription API", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        _testTranscriberConnectionButton!.Click += TestTranscriberConnection;
        transcriber.Controls.Add(new Label { Text = "Test Connection", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 10);
        transcriber.Controls.Add(_testTranscriberConnectionButton, 1, 10);

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
        if (double.TryParse(_temperature.Text.Trim(), out var t)) _cfg.Llm.Temperature = t;
        var mt = _maxTokens.Text.Trim();
        _cfg.Llm.MaxTokens = string.IsNullOrEmpty(mt) ? null : (int?)int.Parse(mt);
        // Allow clearing LLM API key if blank
        var k = _apiKey.Text.Trim();
        _cfg.Llm.ApiKey = string.IsNullOrWhiteSpace(k) ? null : k;
        _cfg.Llm.HttpReferer = string.IsNullOrWhiteSpace(_referer.Text) ? null : _referer.Text.Trim();
        _cfg.Llm.XTitle = string.IsNullOrWhiteSpace(_xTitle.Text) ? null : _xTitle.Text.Trim();
        _cfg.UseClipboardFallback = _clipboardFallback.Checked;

        // Apply transcriber changes
        _cfg.Transcriber.Enabled = _transcriberEnabled!.Checked;
        _cfg.Transcriber.BaseUrl = _transcriberBaseUrl!.Text.Trim();
        _cfg.Transcriber.Model = _transcriberModel!.Text.Trim();
        if (int.TryParse(_transcriberTimeout!.Text.Trim(), out var timeout)) _cfg.Transcriber.TimeoutSeconds = timeout;
        // Allow clearing transcriber API key if blank
        var transcriberKey = _transcriberApiKey!.Text.Trim();
        _cfg.Transcriber.ApiKey = string.IsNullOrWhiteSpace(transcriberKey) ? null : transcriberKey;
        _cfg.Transcriber.AutoPaste = _transcriberAutoPaste!.Checked;
        _cfg.Transcriber.PreferredMicrophoneIndex = _microphoneDropdown!.SelectedIndex >= 0 ? _microphoneDropdown.SelectedIndex : -1;
    }

    private void ValidateInput(object? sender, EventArgs e)
    {
        var errors = new System.Collections.Generic.List<string>();
        
        // Validate URL
        if (!string.IsNullOrWhiteSpace(_baseUrl.Text))
        {
            if (!Uri.TryCreate(_baseUrl.Text, UriKind.Absolute, out var uri) || 
                (uri.Scheme != "http" && uri.Scheme != "https"))
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
            if (!Uri.TryCreate(_transcriberBaseUrl.Text, UriKind.Absolute, out var uri) || 
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                errors.Add("Transcriber Base URL must be a valid HTTP/HTTPS URL");
            }
        }
        
        // Validate timeout
        if (!string.IsNullOrWhiteSpace(_transcriberTimeout!.Text))
        {
            if (!int.TryParse(_transcriberTimeout.Text, out var timeout) || timeout <= 0 || timeout > 300)
            {
                errors.Add("Timeout must be between 1 and 300 seconds");
            }
        }
        
        // Validate model name
        if (string.IsNullOrWhiteSpace(_transcriberModel!.Text))
        {
            errors.Add("Model name is required for transcriber");
        }
        
        _validationLabel.Text = errors.Count > 0 ? string.Join("\n", errors) : "";
        _validationLabel.ForeColor = errors.Count > 0 ? Color.Red : Color.Green;
    }

    private bool ValidateAllInput()
    {
        ValidateInput(null, EventArgs.Empty!);
        ValidateTranscriberInput(null, EventArgs.Empty!);
        return string.IsNullOrEmpty(_validationLabel.Text) || _validationLabel.ForeColor == Color.Green;
    }

    private async void TestConnection(object? sender, EventArgs e)
    {
        try
        {
            _testConnectionButton.Enabled = false;
            _testConnectionButton.Text = "Testing...";
            
            var testConfig = new LlmConfig
            {
                Enabled = true,
                BaseUrl = _baseUrl.Text.Trim(),
                Model = _model.Text.Trim(),
                Temperature = double.TryParse(_temperature.Text.Trim(), out var t) ? t : 0.2,
                MaxTokens = string.IsNullOrWhiteSpace(_maxTokens.Text) ? null : (int?)int.Parse(_maxTokens.Text),
                ApiKey = _apiKey.Text.Trim(),
                HttpReferer = string.IsNullOrWhiteSpace(_referer.Text) ? null : _referer.Text.Trim(),
                XTitle = string.IsNullOrWhiteSpace(_xTitle.Text) ? null : _xTitle.Text.Trim()
            };
            
            var testRefiner = new TextRefiner(testConfig);
            await testRefiner.RefineAsync("Test connection", System.Threading.CancellationToken.None);
            
            NotificationService.ShowSuccess("Connection test successful!");
        }
        catch (Exception ex)
        {
            NotificationService.ShowError($"Connection test failed: {ex.Message}");
        }
        finally
        {
            _testConnectionButton.Enabled = true;
            _testConnectionButton.Text = "Test Connection";
        }
    }

    private async void TestTranscriberConnection(object? sender, EventArgs e)
    {
        try
        {
            _testTranscriberConnectionButton!.Enabled = false;
            _testTranscriberConnectionButton!.Text = "Testing...";
            
            var testConfig = new TranscriberConfig
            {
                Enabled = true,
                BaseUrl = _transcriberBaseUrl!.Text.Trim(),
                Model = _transcriberModel!.Text.Trim(),
                TimeoutSeconds = int.TryParse(_transcriberTimeout!.Text.Trim(), out var t) ? t : 30,
                ApiKey = _transcriberApiKey!.Text.Trim()
            };
            
            using var testTranscriber = new RemoteTranscriber(testConfig);
            await testTranscriber.TestConnectionAsync();
            
            NotificationService.ShowSuccess("Transcription API connection test successful!");
        }
        catch (Exception ex)
        {
            var errorMessage = ex.Message;
            if (ex.InnerException != null)
            {
                errorMessage += ": " + ex.InnerException.Message;
            }
            NotificationService.ShowError($"Transcription API connection test failed: {errorMessage}");
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
        if (cap.ShowDialog() != DialogResult.OK) return;
        _cfg.TranscriberHotkey.Modifiers = cap.Modifiers;
        _cfg.TranscriberHotkey.Key = cap.Key;
        _transcriberHotkey!.Text = GetHotkeyDisplay(_cfg.TranscriberHotkey);
        Logger.Log($"Transcriber hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}");
    }

    private void CaptureLlmHotkey(object? sender, EventArgs e)
    {
        using var cap = new HotkeyCaptureForm();
        if (cap.ShowDialog() != DialogResult.OK) return;
        _cfg.Hotkey.Modifiers = cap.Modifiers;
        _cfg.Hotkey.Key = cap.Key;
        _llmHotkey.Text = GetHotkeyDisplay(_cfg.Hotkey);
        Logger.Log($"LLM hotkey captured: mods={cap.Modifiers}, key={cap.Key}, display={cap.Display}");
    }

    private static string GetHotkeyDisplay(HotkeyConfig hotkey)
    {
        var parts = new System.Collections.Generic.List<string>();
        
        if (hotkey.Modifiers == 0) hotkey.Modifiers = 0x0003;
        
        if ((hotkey.Modifiers & 0x0001) != 0) parts.Add("ALT");
        if ((hotkey.Modifiers & 0x0002) != 0) parts.Add("CTRL");
        if ((hotkey.Modifiers & 0x0004) != 0) parts.Add("SHIFT");
        if ((hotkey.Modifiers & 0x0008) != 0) parts.Add("WIN");
        
        var keyName = ((Keys)hotkey.Key).ToString();
        if (keyName.StartsWith("D") && keyName.Length == 2 && char.IsDigit(keyName[1]))
        {
            keyName = keyName.Substring(1);
        }
        else if (keyName == "OemSemicolon" || keyName == "Oem1") keyName = ";";
        else if (keyName == "OemQuestion" || keyName == "Oem2") keyName = "?";
        else if (keyName == "OemTilde" || keyName == "Oem3") keyName = "~";
        else if (keyName == "OemOpenBrackets" || keyName == "Oem4") keyName = "[";
        else if (keyName == "OemPipe" || keyName == "Oem5") keyName = "|";
        else if (keyName == "OemCloseBrackets" || keyName == "Oem6") keyName = "]";
        else if (keyName == "OemQuotes" || keyName == "Oem7") keyName = "'";
        
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private void ResetToDefaults(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "This will reset all settings to their default values. Are you sure?",
            "Reset to Defaults",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
            
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
            _apiKey.Text = "";
            _referer.Text = defaultCfg.Llm.HttpReferer ?? "";
            _xTitle.Text = defaultCfg.Llm.XTitle ?? "";
            
            _transcriberEnabled!.Checked = defaultCfg.Transcriber.Enabled;
            _transcriberBaseUrl!.Text = defaultCfg.Transcriber.BaseUrl;
            _transcriberModel!.Text = defaultCfg.Transcriber.Model;
            _transcriberTimeout!.Text = defaultCfg.Transcriber.TimeoutSeconds.ToString();
            _transcriberApiKey!.Text = "";
            _transcriberAutoPaste!.Checked = defaultCfg.Transcriber.AutoPaste;
            if (defaultCfg.Transcriber.PreferredMicrophoneIndex >= 0 && defaultCfg.Transcriber.PreferredMicrophoneIndex < _microphoneDropdown!.Items.Count)
                _microphoneDropdown.SelectedIndex = defaultCfg.Transcriber.PreferredMicrophoneIndex;
            
            // Reset Hotkeys in the config object as well, since they aren't read back/saved in ApplyChanges like other fields
            _cfg.Hotkey.Modifiers = defaultCfg.Hotkey.Modifiers;
            _cfg.Hotkey.Key = defaultCfg.Hotkey.Key;
            _llmHotkey.Text = GetHotkeyDisplay(defaultCfg.Hotkey);
            
            _cfg.TranscriberHotkey.Modifiers = defaultCfg.TranscriberHotkey.Modifiers;
            _cfg.TranscriberHotkey.Key = defaultCfg.TranscriberHotkey.Key;
            _transcriberHotkey!.Text = GetHotkeyDisplay(defaultCfg.TranscriberHotkey);
            
            NotificationService.ShowInfo("Settings reset to defaults.");
        }
    }
}
