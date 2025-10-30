using System;
using System.Drawing;
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
        var llm = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(16), RowCount = 8, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        llm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) llm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

        var generalPage = new TabPage("General") { AutoScroll = true };
        generalPage.Controls.Add(general);
        var llmPage = new TabPage("LLM") { AutoScroll = true };
        llmPage.Controls.Add(llm);
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(llmPage);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        Controls.Add(tabs);
        Controls.Add(buttons);

        AcceptButton = ok; CancelButton = cancel;

        ok.Click += (_, __) => ApplyChanges();
    }

    private void ApplyChanges()
    {
        _cfg.AutoPaste = _autoPaste.Checked;
        _cfg.Llm.Enabled = _enabled.Checked;
        _cfg.Llm.BaseUrl = _baseUrl.Text.Trim();
        _cfg.Llm.Model = _model.Text.Trim();
        if (double.TryParse(_temperature.Text.Trim(), out var t)) _cfg.Llm.Temperature = t;
        var mt = _maxTokens.Text.Trim();
        _cfg.Llm.MaxTokens = string.IsNullOrEmpty(mt) ? null : (int?)int.Parse(mt);
        var k = _apiKey.Text;
        if (!string.IsNullOrWhiteSpace(k)) _cfg.Llm.ApiKey = k.Trim();
        _cfg.Llm.HttpReferer = string.IsNullOrWhiteSpace(_referer.Text) ? null : _referer.Text.Trim();
        _cfg.Llm.XTitle = string.IsNullOrWhiteSpace(_xTitle.Text) ? null : _xTitle.Text.Trim();
        _cfg.UseClipboardFallback = _clipboardFallback.Checked;
    }
}
