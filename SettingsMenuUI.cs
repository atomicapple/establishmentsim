using Godot;
using System.Collections.Generic;

/// <summary>
/// Full player settings menu. Controls: Resolution, Window Mode,
/// Graphics Quality, Audio sliders, LLM connection mode.
/// Persists to user://settings.cfg via ConfigFile.
/// </summary>
public partial class SettingsMenuUI : Control
{
    [Signal] public delegate void OnSettingsAppliedEventHandler();
    [Signal] public delegate void OnSettingsCancelledEventHandler();

    private OptionButton _resolutionDropdown;
    private OptionButton _windowModeDropdown;
    private OptionButton _qualityDropdown;
    private HSlider _masterSlider, _musicSlider, _sfxSlider, _ambientSlider;
    private Label _masterLabel, _musicLabel, _sfxLabel, _ambientLabel;
    private OptionButton _llmModeDropdown;
    private LineEdit _apiKeyInput;
    private Button _applyButton, _cancelButton, _defaultButton;

    private ConfigFile _config;
    private const string ConfigPath = "user://settings.cfg";

    private readonly Vector2I[] _resolutions =
    {
        new(1920, 1080), new(2560, 1440), new(3840, 2160),
        new(1280, 720), new(1600, 900), new(1366, 768)
    };

    public override void _Ready()
    {
        _config = new ConfigFile();
        LoadSettings();
        BuildUI();
        ApplyLoadedValues();
        GD.Print("[SettingsMenu] Initialized.");
    }

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var bg = new ColorRect();
        bg.Color = new Color(0, 0, 0, 0.6f);
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.SetSize(new Vector2(500, 480));
        var panelStyle = new StyleBoxFlat { BgColor = new Color(0.08f, 0.08f, 0.13f, 1f) };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);
        margin.AddChild(vbox);

        // Title
        var title = MakeLabel("SETTINGS", 20, new Color(1f, 0.8f, 0.3f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        vbox.AddChild(title);
        vbox.AddChild(new HSeparator());

        // ── Video ──
        vbox.AddChild(MakeSection("VIDEO"));
        _resolutionDropdown = MakeDropdown(new[] { "1920×1080", "2560×1440", "3840×2160", "1280×720", "1600×900", "1366×768" });
        vbox.AddChild(MakeRow("Resolution:", _resolutionDropdown));
        _windowModeDropdown = MakeDropdown(new[] { "Fullscreen", "Windowed", "Borderless" });
        vbox.AddChild(MakeRow("Window Mode:", _windowModeDropdown));
        _qualityDropdown = MakeDropdown(new[] { "Low", "Medium", "High", "Ultra" });
        vbox.AddChild(MakeRow("Quality:", _qualityDropdown));

        vbox.AddChild(new HSeparator());

        // ── Audio ──
        vbox.AddChild(MakeSection("AUDIO"));
        (_masterSlider, _masterLabel) = MakeSliderRow("Master", 80);
        vbox.AddChild(_masterSlider.GetParent() as HBoxContainer);
        (_musicSlider, _musicLabel) = MakeSliderRow("Music", 75);
        vbox.AddChild(_musicSlider.GetParent() as HBoxContainer);
        (_sfxSlider, _sfxLabel) = MakeSliderRow("SFX", 85);
        vbox.AddChild(_sfxSlider.GetParent() as HBoxContainer);
        (_ambientSlider, _ambientLabel) = MakeSliderRow("Ambient", 60);
        vbox.AddChild(_ambientSlider.GetParent() as HBoxContainer);

        vbox.AddChild(new HSeparator());

        // ── LLM ──
        vbox.AddChild(MakeSection("AI / LLM"));
        _llmModeDropdown = MakeDropdown(new[] { "Cloud API (DeepSeek)", "Local (Ollama)" });
        vbox.AddChild(MakeRow("Connection:", _llmModeDropdown));
        _apiKeyInput = new LineEdit();
        _apiKeyInput.PlaceholderText = "sk-... (API Key)";
        _apiKeyInput.Secret = true;
        vbox.AddChild(MakeRow("API Key:", _apiKeyInput));

        vbox.AddChild(new HSeparator());

        // ── Buttons ──
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 8);
        btnRow.Alignment = BoxContainer.AlignmentMode.End;

        _defaultButton = MakeButton("Defaults", new Color(0.3f, 0.3f, 0.4f));
        _defaultButton.Pressed += ResetDefaults;
        btnRow.AddChild(_defaultButton);

        _cancelButton = MakeButton("Cancel", new Color(0.4f, 0.3f, 0.3f));
        _cancelButton.Pressed += () => { Visible = false; EmitSignal(SignalName.OnSettingsCancelled); };
        btnRow.AddChild(_cancelButton);

        _applyButton = MakeButton("Apply", new Color(0.2f, 0.6f, 0.3f));
        _applyButton.Pressed += ApplySettings;
        btnRow.AddChild(_applyButton);

        vbox.AddChild(btnRow);
    }

    private void LoadSettings()
    {
        if (_config.Load(ConfigPath) != Error.Ok) return;
    }

    private void ApplyLoadedValues()
    {
        _resolutionDropdown.Selected = (int)_config.GetValue("video", "resolution", 0);
        _windowModeDropdown.Selected = (int)_config.GetValue("video", "window_mode", 0);
        _qualityDropdown.Selected = (int)_config.GetValue("video", "quality", 2);
        _masterSlider.Value = (double)_config.GetValue("audio", "master", 80);
        _musicSlider.Value = (double)_config.GetValue("audio", "music", 75);
        _sfxSlider.Value = (double)_config.GetValue("audio", "sfx", 85);
        _ambientSlider.Value = (double)_config.GetValue("audio", "ambient", 60);
        _llmModeDropdown.Selected = (int)_config.GetValue("llm", "mode", 0);
        _apiKeyInput.Text = (string)_config.GetValue("llm", "api_key", "");
        UpdateAllSliderLabels();
    }

    private void SaveSettings()
    {
        _config.SetValue("video", "resolution", _resolutionDropdown.Selected);
        _config.SetValue("video", "window_mode", _windowModeDropdown.Selected);
        _config.SetValue("video", "quality", _qualityDropdown.Selected);
        _config.SetValue("audio", "master", (float)_masterSlider.Value);
        _config.SetValue("audio", "music", (float)_musicSlider.Value);
        _config.SetValue("audio", "sfx", (float)_sfxSlider.Value);
        _config.SetValue("audio", "ambient", (float)_ambientSlider.Value);
        _config.SetValue("llm", "mode", _llmModeDropdown.Selected);
        _config.SetValue("llm", "api_key", _apiKeyInput.Text);
        _config.Save(ConfigPath);
    }

    private void ApplySettings()
    {
        SaveSettings();

        // Apply video
        var res = _resolutions[_resolutionDropdown.Selected];
        DisplayServer.WindowSetSize(res);
        DisplayServer.WindowSetMode(_windowModeDropdown.Selected switch
        {
            1 => DisplayServer.WindowMode.Windowed,
            2 => DisplayServer.WindowMode.Maximized,
            _ => DisplayServer.WindowMode.Fullscreen
        });

        // Apply audio
        var audioMgr = GetTree()?.Root?.FindChild("AudioManager", true, false) as AudioManager;
        if (audioMgr != null)
        {
            audioMgr.MasterVolume = (float)_masterSlider.Value;
            audioMgr.MusicVolume = (float)_musicSlider.Value;
            audioMgr.SfxVolume = (float)_sfxSlider.Value;
            audioMgr.AmbientVolume = (float)_ambientSlider.Value;
        }

        // Apply quality
        var vfx = GetTree()?.Root?.FindChild("VenueAtmosphereVFX", true, false) as VenueAtmosphereVFX;
        if (vfx != null)
            vfx.Quality = (GraphicsQuality)_qualityDropdown.Selected;

        GD.Print("[SettingsMenu] Settings applied.");
        EmitSignal(SignalName.OnSettingsApplied);
        Visible = false;
    }

    private void ResetDefaults()
    {
        _resolutionDropdown.Selected = 0;
        _windowModeDropdown.Selected = 0;
        _qualityDropdown.Selected = 2;
        _masterSlider.Value = 80;
        _musicSlider.Value = 75;
        _sfxSlider.Value = 85;
        _ambientSlider.Value = 60;
        _llmModeDropdown.Selected = 0;
        _apiKeyInput.Text = "";
        UpdateAllSliderLabels();
    }

    private void UpdateAllSliderLabels()
    {
        _masterLabel.Text = $"{(int)_masterSlider.Value}%";
        _musicLabel.Text = $"{(int)_musicSlider.Value}%";
        _sfxLabel.Text = $"{(int)_sfxSlider.Value}%";
        _ambientLabel.Text = $"{(int)_ambientSlider.Value}%";
    }

    private (HSlider, Label) MakeSliderRow(string name, float defaultValue)
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        var label = MakeLabel(name, 12, new Color(0.7f, 0.7f, 0.8f));
        label.CustomMinimumSize = new Vector2(60, 0);
        hbox.AddChild(label);
        var slider = new HSlider();
        slider.CustomMinimumSize = new Vector2(180, 0);
        slider.MinValue = 0; slider.MaxValue = 100; slider.Value = defaultValue;
        hbox.AddChild(slider);
        var valLabel = MakeLabel($"{defaultValue:F0}%", 12, Colors.White);
        valLabel.CustomMinimumSize = new Vector2(40, 0);
        hbox.AddChild(valLabel);
        slider.ValueChanged += (v) => valLabel.Text = $"{(int)v}%";
        return (slider, valLabel);
    }

    private static HBoxContainer MakeRow(string labelText, Control control)
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 8);
        hbox.AddChild(MakeLabel(labelText, 12, new Color(0.7f, 0.7f, 0.8f)));
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hbox.AddChild(control);
        return hbox;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label(); l.Text = text;
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        l.VerticalAlignment = VerticalAlignment.Center;
        return l;
    }

    private static Label MakeSection(string text) => MakeLabel(text, 15, new Color(1f, 0.8f, 0.3f));

    private static OptionButton MakeDropdown(string[] items)
    {
        var dd = new OptionButton();
        foreach (var item in items) dd.AddItem(item);
        return dd;
    }

    private static Button MakeButton(string text, Color color)
    {
        var btn = new Button(); btn.Text = text;
        var style = new StyleBoxFlat { BgColor = color };
        btn.AddThemeStyleboxOverride("normal", style);
        return btn;
    }
}
