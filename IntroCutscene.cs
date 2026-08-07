using Godot;
using System;
using System.Collections.Generic;

/// <summary>A single slide in the intro cutscene.</summary>
public class IntroSlide
{
    public string ImagePath { get; set; }          // background art
    public string Title { get; set; }
    public string Body { get; set; }
    public float DurationSeconds { get; set; } = 6f;  // auto-advance time
    public bool AllowSkip { get; set; } = true;
}

/// <summary>
/// 3-slide graphic novel intro cutscene.
/// Sets the stakes: failing venue, municipal crackdown, mounting debt,
/// and Leo handing over the keys to the last property.
/// </summary>
public partial class IntroCutscene : Control
{
    [Signal] public delegate void OnCutsceneCompleteEventHandler();

    private List<IntroSlide> _slides;
    private int _currentSlide;
    private float _slideTimer;
    private bool _textRevealing;
    private int _revealIndex;
    private float _revealTimer;

    private TextureRect _imageDisplay;
    private Label _titleLabel, _bodyLabel;
    private Button _skipBtn, _nextBtn;
    private ColorRect _vignette;
    private ColorRect _fadeOverlay;
    private bool _fading;

    private static readonly Color Gold = new(0.85f, 0.7f, 0.25f);

    public override void _Ready()
    {
        BuildSlides();
        BuildUI();
        ShowSlide(0);
        GD.Print("[IntroCutscene] Ready.");
    }

    private void BuildSlides()
    {
        _slides = new List<IntroSlide>
        {
            new IntroSlide
            {
                Title = "THE ESTABLISHMENT",
                Body = "The city is changing. A new municipal crackdown has driven Vice Heat to unprecedented levels. " +
                       "Venues are being raided. Owners are being arrested. The old ways are dying.\n\n" +
                       "In the chaos, opportunity hides for those bold enough to seize it.",
                DurationSeconds = 8f
            },
            new IntroSlide
            {
                Title = "THE DEBT",
                Body = "You've inherited a failing venue in the Iron Row district. The previous owner fled, leaving behind " +
                       "crumbling rooms, disgruntled staff, and a $50,000 debt to the Velvet Cartel.\n\n" +
                       "They've given you 90 days to turn it around. Or else.",
                DurationSeconds = 8f
            },
            new IntroSlide
            {
                Title = "THE HANDOVER",
                Body = "Leo Vance, your fixer and manager, slides a dossier across the table.\n\n" +
                       "\"Listen carefully. This isn't just about running a club. It's about managing heat, " +
                       "keeping staff loyal, and knowing when to pay off the right people.\n\n" +
                       "The Cartel wants their money. The cops want their cut. And the clients? " +
                       "They want discretion. Balance those three, and you might survive.\"\n\n" +
                       "Leo hands you the keys. \"Last property in the district. Don't screw this up.\"",
                DurationSeconds = 12f
            }
        };
    }

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Dark background
        var bg = new ColorRect();
        bg.Color = new Color(0.03f, 0.02f, 0.05f);
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        // Image placeholder
        _imageDisplay = new TextureRect();
        _imageDisplay.SetAnchorsPreset(Control.LayoutPreset.Center);
        _imageDisplay.SetSize(new Vector2(800, 300));
        _imageDisplay.Position = new Vector2(-400, -250);
        _imageDisplay.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        AddChild(_imageDisplay);

        // Vignette
        _vignette = new ColorRect();
        _vignette.Color = new Color(0, 0, 0, 0.4f);
        _vignette.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _vignette.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_vignette);

        // Gold decorative line
        var line = new ColorRect();
        line.Color = Gold;
        line.SetAnchorsPreset(Control.LayoutPreset.Center);
        line.SetSize(new Vector2(500, 1));
        line.Position = new Vector2(-250, 80);
        AddChild(line);

        // Title
        _titleLabel = new Label();
        _titleLabel.AddThemeFontSizeOverride("font_size", 24);
        _titleLabel.AddThemeColorOverride("font_color", Gold);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _titleLabel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _titleLabel.SetSize(new Vector2(600, 40));
        _titleLabel.Position = new Vector2(-300, 90);
        AddChild(_titleLabel);

        // Body text
        _bodyLabel = new Label();
        _bodyLabel.AddThemeFontSizeOverride("font_size", 14);
        _bodyLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.85f));
        _bodyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _bodyLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _bodyLabel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _bodyLabel.SetSize(new Vector2(550, 200));
        _bodyLabel.Position = new Vector2(-275, 140);
        AddChild(_bodyLabel);

        // Skip button
        _skipBtn = new Button();
        _skipBtn.Text = "SKIP ▸▸";
        _skipBtn.AddThemeFontSizeOverride("font_size", 11);
        _skipBtn.Flat = true;
        _skipBtn.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.5f));
        _skipBtn.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _skipBtn.Position = new Vector2(-80, -40);
        _skipBtn.Pressed += SkipCutscene;
        AddChild(_skipBtn);

        // Fade overlay
        _fadeOverlay = new ColorRect();
        _fadeOverlay.Color = new Color(0, 0, 0, 0);
        _fadeOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fadeOverlay.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_fadeOverlay);
    }

    private void ShowSlide(int index)
    {
        if (index >= _slides.Count)
        {
            CompleteCutscene();
            return;
        }

        _currentSlide = index;
        var slide = _slides[index];
        _slideTimer = 0f;
        _textRevealing = true;
        _revealIndex = 0;
        _revealTimer = 0f;

        _titleLabel.Text = "";
        _bodyLabel.Text = "";

        GD.Print($"[IntroCutscene] Slide {index + 1}/{_slides.Count}: {slide.Title}");
    }

    public override void _Process(double delta)
    {
        var slide = _currentSlide < _slides.Count ? _slides[_currentSlide] : null;
        if (slide == null) return;

        _slideTimer += (float)delta;

        // Text reveal effect
        if (_textRevealing)
        {
            _revealTimer += (float)delta * 60f; // chars per second
            int targetIndex = Math.Min((int)_revealTimer, slide.Body.Length);
            if (targetIndex > _revealIndex)
            {
                _revealIndex = targetIndex;
                _titleLabel.Text = slide.Title;
                _bodyLabel.Text = slide.Body[.._revealIndex];
            }
            if (_revealIndex >= slide.Body.Length)
                _textRevealing = false;
        }

        // Auto-advance
        if (_slideTimer >= slide.DurationSeconds)
            ShowSlide(_currentSlide + 1);

        // Click anywhere to advance (after text is revealed)
        if (!_textRevealing && Input.IsMouseButtonPressed(MouseButton.Left))
            ShowSlide(_currentSlide + 1);
    }

    private void SkipCutscene()
    {
        CompleteCutscene();
    }

    private void CompleteCutscene()
    {
        GD.Print("[IntroCutscene] Complete.");
        EmitSignal(SignalName.OnCutsceneComplete);

        // Fade out and remove
        var tween = CreateTween();
        tween.TweenProperty(_fadeOverlay, "color", new Color(0, 0, 0, 1), 0.8f);
        tween.TweenCallback(Callable.From(() =>
        {
            var loop = GetTree()?.Root?.FindChild("MasterGameLoop", true, false) as MasterGameLoop;
            loop?.StartNewGame();
            QueueFree();
        }));
    }

    public override string ToString() => $"[IntroCutscene] Slide {_currentSlide + 1}/{_slides.Count}";
}
