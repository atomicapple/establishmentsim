using Godot;
using System;

/// <summary>
/// Art Deco main menu screen. Dark theme with gold accents,
/// slow-panning 3D isometric room background, and save-state
/// detection that lights up "Load Dossier" when saves exist.
/// </summary>
public partial class MainMenuUI : Control
{
    [Signal] public delegate void OnNewGamePressedEventHandler();
    [Signal] public delegate void OnLoadGamePressedEventHandler();
    [Signal] public delegate void OnSettingsPressedEventHandler();
    [Signal] public delegate void OnExitPressedEventHandler();

    private Button _newGameBtn, _loadGameBtn, _settingsBtn, _exitBtn;
    private Label _titleLabel, _subtitleLabel, _versionLabel;
    private ColorRect _vignette;
    private TextureRect _bgRenderer;
    private float _panOffset;

    // Art Deco palette
    private static readonly Color Gold     = new(0.85f, 0.7f, 0.25f);
    private static readonly Color GoldDim  = new(0.5f, 0.4f, 0.15f);
    private static readonly Color DarkBg   = new(0.05f, 0.04f, 0.07f);
    private static readonly Color Slate    = new(0.15f, 0.14f, 0.2f);

    public override void _Ready()
    {
        BuildUI();
        CheckSaveState();

        // Connect to MasterGameLoop for phase management
        var loop = GetTree()?.Root?.FindChild("MasterGameLoop", true, false) as MasterGameLoop;
        loop?.TransitionTo(GamePhase.MainMenu);

        GD.Print("[MainMenu] Ready.");
    }

    public override void _Process(double delta)
    {
        // Slow background pan
        _panOffset += (float)delta * 3f;
    }

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Dark background
        var bg = new ColorRect();
        bg.Color = DarkBg;
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        // Center content
        var center = new VBoxContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.Center);
        center.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddThemeConstantOverride("separation", 8);
        AddChild(center);

        // Decorative gold line
        var topLine = new ColorRect();
        topLine.Color = Gold;
        topLine.CustomMinimumSize = new Vector2(300, 2);
        center.AddChild(topLine);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });

        // Title
        _titleLabel = new Label();
        _titleLabel.Text = "ESTABLISHMENT SIMULATOR";
        _titleLabel.AddThemeFontSizeOverride("font_size", 28);
        _titleLabel.AddThemeColorOverride("font_color", Gold);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(_titleLabel);

        // Subtitle
        _subtitleLabel = new Label();
        _subtitleLabel.Text = "MANAGEMENT IS A DIRTY BUSINESS";
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 12);
        _subtitleLabel.AddThemeColorOverride("font_color", GoldDim);
        _subtitleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(_subtitleLabel);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        // Bottom gold line
        var bottomLine = new ColorRect();
        bottomLine.Color = Gold;
        bottomLine.CustomMinimumSize = new Vector2(300, 1);
        center.AddChild(bottomLine);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 32) });

        // Buttons
        _newGameBtn    = CreateMenuButton("NEW OPERATION",     Gold);
        _loadGameBtn   = CreateMenuButton("LOAD DOSSIER",      GoldDim);
        _settingsBtn   = CreateMenuButton("DIRECTIVES & SETTINGS", Slate);
        _exitBtn       = CreateMenuButton("EXIT TO REALITY",   new Color(0.4f, 0.3f, 0.25f));

        _newGameBtn.Pressed  += () => EmitSignal(SignalName.OnNewGamePressed);
        _loadGameBtn.Pressed += () => EmitSignal(SignalName.OnLoadGamePressed);
        _settingsBtn.Pressed += () => EmitSignal(SignalName.OnSettingsPressed);
        _exitBtn.Pressed     += () => EmitSignal(SignalName.OnExitPressed);

        center.AddChild(_newGameBtn);
        center.AddChild(_loadGameBtn);
        center.AddChild(_settingsBtn);
        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 20) });
        center.AddChild(_exitBtn);

        // Version
        _versionLabel = new Label();
        _versionLabel.Text = "v0.1.0 — Godot 4.7.1";
        _versionLabel.AddThemeFontSizeOverride("font_size", 10);
        _versionLabel.AddThemeColorOverride("font_color", new Color(0.3f, 0.3f, 0.35f));
        _versionLabel.HorizontalAlignment = HorizontalAlignment.Center;
        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 40) });
        center.AddChild(_versionLabel);
    }

    private Button CreateMenuButton(string text, Color color)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 16);
        btn.CustomMinimumSize = new Vector2(280, 44);

        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = color
        };
        btn.AddThemeStyleboxOverride("normal", normal);

        var hover = new StyleBoxFlat
        {
            BgColor = new Color(color.R, color.G, color.B, 0.15f),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(color.R, color.G, color.B, 0.8f)
        };
        btn.AddThemeStyleboxOverride("hover", hover);

        btn.AddThemeColorOverride("font_color", color);
        return btn;
    }

    private void CheckSaveState()
    {
        string savePath = "user://saves/autosave.sav";
        bool hasSaves = Godot.FileAccess.FileExists(savePath);

        // Light up "Load Dossier" if saves exist
        if (hasSaves)
        {
            _loadGameBtn.AddThemeColorOverride("font_color", Gold);
            var style = _loadGameBtn.GetThemeStylebox("normal") as StyleBoxFlat;
            if (style != null) style.BorderColor = Gold;
            _loadGameBtn.AddThemeStyleboxOverride("normal",
                new StyleBoxFlat { BgColor = new Color(0,0,0,0), BorderWidthBottom=1,BorderWidthTop=1,BorderWidthLeft=1,BorderWidthRight=1, BorderColor=Gold });
        }
        else
        {
            _loadGameBtn.Disabled = true;
        }
    }

    public override string ToString() => "[MainMenu] Ready for player input.";
}
