using Godot;
using System;
using System.Collections.Generic;

/// <summary>A single narrative slide in the intro sequence.</summary>
public class NarrativeSlide
{
    public string ImagePath { get; set; }          // background texture
    public string Title { get; set; }
    public string Body { get; set; }
    public float AutoAdvanceSeconds { get; set; }  // 0 = manual advance only
}

/// <summary>
/// Handles the opening story sequence with typewriter-style text
/// pacing, punctuation-triggered pauses, Next/Skip controls,
/// and an IntroSequenceCompleted signal to hand off to the Tutorial.
/// </summary>
public partial class IntroNarrativeManager : Control
{
    [Signal] public delegate void IntroSequenceCompletedEventHandler();

    // ── Configuration ──────────────────────────────────────────────────
    /// <summary>Characters revealed per second during typewriter effect.</summary>
    public float CharsPerSecond { get; set; } = 45f;

    /// <summary>Extra pause (seconds) when hitting punctuation.</summary>
    public float PunctuationPauseSeconds { get; set; } = 0.3f;

    /// <summary>Set of punctuation characters that trigger a pause.</summary>
    private static readonly HashSet<char> PauseChars = new()
    { '.', '!', '?', ';', ':', ',', '—', '\n' };

    // ── Node References ────────────────────────────────────────────────
    private TextureRect _imageDisplay;
    private Label _titleLabel;
    private RichTextLabel _bodyLabel;
    private Button _nextBtn, _skipBtn;
    private ColorRect _fadeOverlay;
    private Label _pageIndicator;

    // ── State ───────────────────────────────────────────────────────────
    private List<NarrativeSlide> _slides;
    private int _currentSlideIndex;
    private int _revealedCharIndex;
    private float _charTimer;
    private float _pauseTimer;
    private bool _isPaused;
    private bool _textFullyRevealed;
    private bool _isFading;

    private static readonly Color Gold    = new(0.85f, 0.7f, 0.25f);
    private static readonly Color GoldDim = new(0.5f, 0.4f, 0.15f);
    private static readonly Color BodyText = new(0.8f, 0.78f, 0.85f);

    public override void _Ready()
    {
        BuildSlides();
        BuildUI();
        ShowSlide(0);
        GD.Print("[IntroNarrativeManager] Ready.");
    }

    // ── Slide Data ─────────────────────────────────────────────────────
    private void BuildSlides()
    {
        _slides = new List<NarrativeSlide>
        {
            new NarrativeSlide
            {
                Title = "THE ESTABLISHMENT",
                Body = "The city of Ashwick is changing. A new municipal crackdown — the so-called \"Morality Initiative\" — has driven Vice Heat to unprecedented levels across every district.\n\nVenues are being raided nightly. Owners are being arrested without warrants. The old ways of doing business are crumbling under the weight of political ambition.\n\nBut in every crisis, there is opportunity. And for those bold enough to operate in the grey spaces between law and profit, the rewards have never been greater.",
                AutoAdvanceSeconds = 0
            },
            new NarrativeSlide
            {
                Title = "THE DEBT",
                Body = "Six months ago, Dominic Voss — the previous owner of the Iron Row establishment — fled the city. He left behind crumbling rooms, a frightened skeleton crew, and a fifty-thousand-dollar debt to the Velvet Cartel.\n\nThe Cartel doesn't forget debts. They've given you ninety days to make good on Voss's obligations — with a vig that compounds weekly.\n\nNinety days to turn a failing venue into a profit machine. Ninety days before the Cartel collects in blood what they can't collect in cash.",
                AutoAdvanceSeconds = 0
            },
            new NarrativeSlide
            {
                Title = "THE HANDOVER",
                Body = "Leo Vance slides a worn leather dossier across the table. Forty-two years old, former fixer for three different administrations, and the only person in Ashwick who knows where every body is buried — literally.\n\n\"Listen carefully.\" He taps the dossier. \"This isn't just about running a club. It's about managing heat, keeping staff loyal, and knowing exactly when to pay off the right people. The Cartel wants their money. The cops want their cut. The clients want discretion. Balance those three, and you might — might — survive.\"\n\nHe hands you a brass key, tarnished but solid. \"Last property in the district. My retirement's tied up in this too. Don't screw it up.\"",
                AutoAdvanceSeconds = 0
            },
            new NarrativeSlide
            {
                Title = "THE DIRECTIVE",
                Body = "Leo opens the dossier. Inside: a floor plan of the venue, a list of every official on the Cartel's payroll, and a single directive scrawled in red ink.\n\n\"BUILD. MANAGE. EXPAND. SURVIVE.\"\n\nHe looks at you over his reading glasses. \"The Tutorial Manager will walk you through the basics. Pay attention. The things you learn in the next ten minutes will determine whether you thrive in this city — or become another name the Cartel crosses off their list.\"\n\n\"Ready?\"",
                AutoAdvanceSeconds = 0
            }
        };
    }

    // ── UI Construction ────────────────────────────────────────────────
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
        _imageDisplay.SetSize(new Vector2(700, 220));
        _imageDisplay.Position = new Vector2(-350, -280);
        _imageDisplay.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        AddChild(_imageDisplay);

        // Decorative gold line
        var line = new ColorRect();
        line.Color = Gold;
        line.SetSize(new Vector2(500, 1));
        line.SetAnchorsPreset(Control.LayoutPreset.Center);
        line.Position = new Vector2(-250, 20);
        AddChild(line);

        // Title
        _titleLabel = new Label();
        _titleLabel.AddThemeFontSizeOverride("font_size", 22);
        _titleLabel.AddThemeColorOverride("font_color", Gold);
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _titleLabel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _titleLabel.SetSize(new Vector2(600, 35));
        _titleLabel.Position = new Vector2(-300, 30);
        AddChild(_titleLabel);

        // Body
        _bodyLabel = new RichTextLabel();
        _bodyLabel.BbcodeEnabled = true;
        _bodyLabel.FitContent = true;
        _bodyLabel.ScrollFollowing = true;
        _bodyLabel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _bodyLabel.SetSize(new Vector2(560, 260));
        _bodyLabel.Position = new Vector2(-280, 80);
        AddChild(_bodyLabel);

        // Page indicator
        _pageIndicator = new Label();
        _pageIndicator.AddThemeFontSizeOverride("font_size", 10);
        _pageIndicator.AddThemeColorOverride("font_color", GoldDim);
        _pageIndicator.HorizontalAlignment = HorizontalAlignment.Center;
        _pageIndicator.SetAnchorsPreset(Control.LayoutPreset.Center);
        _pageIndicator.SetSize(new Vector2(200, 20));
        _pageIndicator.Position = new Vector2(-100, 350);
        AddChild(_pageIndicator);

        // Next button
        _nextBtn = new Button();
        _nextBtn.Text = "NEXT ▸";
        _nextBtn.AddThemeFontSizeOverride("font_size", 14);
        _nextBtn.Flat = true;
        _nextBtn.AddThemeColorOverride("font_color", Gold);
        _nextBtn.SetAnchorsPreset(Control.LayoutPreset.Center);
        _nextBtn.SetSize(new Vector2(120, 40));
        _nextBtn.Position = new Vector2(120, 370);
        _nextBtn.Visible = false;
        _nextBtn.Pressed += OnNextPressed;
        AddChild(_nextBtn);

        // Skip button
        _skipBtn = new Button();
        _skipBtn.Text = "SKIP";
        _skipBtn.AddThemeFontSizeOverride("font_size", 10);
        _skipBtn.Flat = true;
        _skipBtn.AddThemeColorOverride("font_color", new Color(0.35f, 0.35f, 0.4f));
        _skipBtn.SetAnchorsPreset(Control.LayoutPreset.Center);
        _skipBtn.SetSize(new Vector2(80, 30));
        _skipBtn.Position = new Vector2(-200, 375);
        _skipBtn.Pressed += SkipAll;
        AddChild(_skipBtn);

        // Fade overlay
        _fadeOverlay = new ColorRect();
        _fadeOverlay.Color = new Color(0, 0, 0, 0);
        _fadeOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _fadeOverlay.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_fadeOverlay);
    }

    // ── Slide Management ───────────────────────────────────────────────
    private void ShowSlide(int index)
    {
        if (index >= _slides.Count)
        {
            CompleteSequence();
            return;
        }

        _currentSlideIndex = index;
        var slide = _slides[index];

        // Reset state
        _revealedCharIndex = 0;
        _charTimer = 0f;
        _pauseTimer = 0f;
        _isPaused = false;
        _textFullyRevealed = false;

        _titleLabel.Text = slide.Title;
        _bodyLabel.Text = "";
        _pageIndicator.Text = $"{index + 1} / {_slides.Count}";
        _nextBtn.Visible = false;

        GD.Print($"[IntroNarrativeManager] Slide {index + 1}/{_slides.Count}: {slide.Title}");
    }

    // ── Process Loop ───────────────────────────────────────────────────
    public override void _Process(double delta)
    {
        if (_currentSlideIndex >= _slides.Count || _textFullyRevealed) return;

        var slide = _slides[_currentSlideIndex];
        string fullText = slide.Body;

        // Handle punctuation pause
        if (_isPaused)
        {
            _pauseTimer += (float)delta;
            if (_pauseTimer >= PunctuationPauseSeconds)
            {
                _isPaused = false;
                _revealedCharIndex++;
                RenderCurrentText(fullText);
            }
            return;
        }

        // Typewriter effect
        _charTimer += (float)delta * CharsPerSecond;
        int targetIndex = Math.Min((int)_charTimer, fullText.Length);

        if (targetIndex > _revealedCharIndex)
        {
            _revealedCharIndex = targetIndex;
            RenderCurrentText(fullText);

            // Check if we just revealed a punctuation character
            if (_revealedCharIndex > 0 && _revealedCharIndex <= fullText.Length)
            {
                char lastChar = fullText[_revealedCharIndex - 1];
                if (PauseChars.Contains(lastChar))
                {
                    _isPaused = true;
                    _pauseTimer = 0f;

                    // Longer pause for sentence-ending punctuation
                    if (lastChar is '.' or '!' or '?')
                        _pauseTimer = -PunctuationPauseSeconds * 0.5f; // extend pause
                }
            }
        }

        // Text fully revealed
        if (_revealedCharIndex >= fullText.Length)
        {
            _textFullyRevealed = true;
            _nextBtn.Visible = true;
        }

        // Click to skip typewriter and reveal all
        if (!_textFullyRevealed && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _revealedCharIndex = fullText.Length;
            RenderCurrentText(fullText);
            _textFullyRevealed = true;
            _nextBtn.Visible = true;
        }
    }

    private void RenderCurrentText(string fullText)
    {
        if (_revealedCharIndex <= 0)
        {
            _bodyLabel.Text = "";
            return;
        }

        string visible = fullText[.._revealedCharIndex];
        string remaining = _revealedCharIndex < fullText.Length
            ? $"[color=#333333]{fullText[_revealedCharIndex..]}[/color]"
            : "";

        _bodyLabel.Text = $"[font_size=15][color=#CCCCDD]{visible}[/color]{remaining}[/font_size]";
    }

    private void OnNextPressed()
    {
        ShowSlide(_currentSlideIndex + 1);
    }

    private void SkipAll()
    {
        CompleteSequence();
    }

    private void CompleteSequence()
    {
        if (_isFading) return;
        _isFading = true;

        GD.Print("[IntroNarrativeManager] Sequence complete. Handing off to Tutorial.");

        // Fade out
        var tween = CreateTween();
        tween.TweenProperty(_fadeOverlay, "color", new Color(0, 0, 0, 1), 0.6f);
        tween.TweenCallback(Callable.From(() =>
        {
            EmitSignal(SignalName.IntroSequenceCompleted);
            QueueFree();
        }));
    }

    public override string ToString() =>
        $"[IntroNarrativeManager] Slide {_currentSlideIndex + 1}/{_slides.Count}";
}
