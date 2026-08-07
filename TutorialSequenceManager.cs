using Godot;
using System;

/// <summary>A single step in the tutorial sequence.</summary>
public class TutorialStep
{
    public int StepNumber;
    public string Title;
    public string Instructions;
    public string TargetNodePath;  // UI element to highlight
    public string RequiredAction;  // what the player must do
    public bool IsComplete;
    public Action OnEnter;
    public Func<bool> CompletionCheck;
}

/// <summary>
/// Manages first-time player onboarding through 5 core steps:
/// 1. Buying a property, 2. Building a room, 3. Hiring staff,
/// 4. Handling a client, 5. Paying off local police.
/// Highlights UI buttons with focus overlays and disables
/// unrelated navigation during step completion.
/// </summary>
public partial class TutorialSequenceManager : Control
{
    [Signal] public delegate void OnTutorialStartedEventHandler();
    [Signal] public delegate void OnStepCompletedEventHandler(int stepNumber, string stepTitle);
    [Signal] public delegate void OnTutorialCompletedEventHandler();

    private readonly TutorialStep[] _steps = new TutorialStep[4];
    private int _currentStepIndex = -1;
    private bool _tutorialActive;
    private bool _tutorialComplete;

    // Overlay nodes
    private ColorRect _dimOverlay;
    private PanelContainer _instructionPanel;
    private Label _titleLabel;
    private Label _instructionLabel;
    private Label _stepLabel;
    private Button _skipButton;
    private ColorRect _highlightRect;

    public bool IsActive => _tutorialActive;
    public int CurrentStep => _currentStepIndex + 1;

    public override void _Ready()
    {
        BuildSteps();
        BuildUI();
        Visible = false;
        GD.Print("[Tutorial] Initialized.");
    }

    private void BuildSteps()
    {
        // Spec-aligned 4-step tutorial: Spatial → Haggling → Ledger → Upgrades
        _steps[0] = new TutorialStep
        {
            StepNumber = 1, Title = "The Spatial Micro-Loop: Room Management",
            Instructions = "Look at the Isometric Building View. A dirty room is flashing — click it and assign a worker to clean it.\n\nThe Cleaning icon will change to a green checkmark when complete. Keeping rooms clean maintains your Luxury Score and keeps clients happy.",
            TargetNodePath = "VenueBuilding",
            RequiredAction = "clean_room"
        };

        _steps[1] = new TutorialStep
        {
            StepNumber = 2, Title = "The Core Negotiation Loop: Haggling",
            Instructions = "A wealthy VIP client has arrived. The Client Interaction Interface shows their offer: $3,500/night with Medium Patience.\n\nClick 'Counter-Offer' to use your staff's Charisma for a better deal, or 'Upsell Service' to leverage Negotiation for premium add-ons. Every counter-offer costs patience — push too hard and they'll walk.",
            TargetNodePath = "EventDialogUI",
            RequiredAction = "complete_haggle"
        };

        _steps[2] = new TutorialStep
        {
            StepNumber = 3, Title = "The Economic Meso-Loop: The Ledger",
            Instructions = "The daily tick has completed. Review the Financial Ledger — notice the 'Heat Mitigation / Bribes' line highlighted at -$2,500.\n\nTo keep the precinct off your back, you must spend money on bribes. Click 'Pay Bribe' to allocate $2,500 and lower Heat by 25 points. Managing Heat is the difference between thriving and being raided.",
            TargetNodePath = "FinancialLedger",
            RequiredAction = "pay_bribe"
        };

        _steps[3] = new TutorialStep
        {
            StepNumber = 4, Title = "The Strategic Macro-Loop: Upgrades",
            Instructions = "To permanently reduce Heat generation, invest in facility upgrades. Open the Research & Upgrades Tree and purchase 'Advanced Security Systems' under the Facility Enhancements branch for $4,000.\n\nThis permanently reduces global room Heat generation. Strategic investments compound over time — a single upgrade can save you thousands in bribes over a campaign.",
            TargetNodePath = "ResearchTreeUI",
            RequiredAction = "purchase_upgrade"
        };
    }

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // Dim overlay
        _dimOverlay = new ColorRect();
        _dimOverlay.Color = new Color(0, 0, 0, 0.5f);
        _dimOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_dimOverlay);

        // Highlight rectangle
        _highlightRect = new ColorRect();
        _highlightRect.Color = new Color(1f, 0.85f, 0.2f, 0.3f);
        _highlightRect.Visible = false;
        AddChild(_highlightRect);

        // Instruction panel
        _instructionPanel = new PanelContainer();
        _instructionPanel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _instructionPanel.SetSize(new Vector2(0, 140));
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.1f, 0.95f),
            BorderWidthTop = 2, BorderColor = new Color(1f, 0.8f, 0.3f)
        };
        _instructionPanel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_instructionPanel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        _instructionPanel.AddChild(margin);

        var vbox = new VBoxContainer();
        margin.AddChild(vbox);

        _stepLabel = MakeLabel("Step 1/5", 11, new Color(0.6f, 0.6f, 0.7f));
        vbox.AddChild(_stepLabel);

        _titleLabel = MakeLabel("", 18, new Color(1f, 0.85f, 0.3f));
        vbox.AddChild(_titleLabel);

        _instructionLabel = MakeLabel("", 13, new Color(0.85f, 0.85f, 0.9f));
        vbox.AddChild(_instructionLabel);

        _skipButton = new Button();
        _skipButton.Text = "Skip Tutorial";
        _skipButton.AddThemeFontSizeOverride("font_size", 11);
        _skipButton.Pressed += SkipTutorial;
        vbox.AddChild(_skipButton);
    }

    // ── Tutorial Control ───────────────────────────────────────────────

    public void StartTutorial()
    {
        if (_tutorialComplete) return;
        _tutorialActive = true;
        _currentStepIndex = 0;
        Visible = true;
        ShowStep(0);
        EmitSignal(SignalName.OnTutorialStarted);
        GD.Print("[Tutorial] Started.");
    }

    public void NotifyAction(string action)
    {
        if (!_tutorialActive) return;
        var step = _steps[_currentStepIndex];
        if (step.RequiredAction == action)
            CompleteCurrentStep();
    }

    private void CompleteCurrentStep()
    {
        var step = _steps[_currentStepIndex];
        step.IsComplete = true;
        EmitSignal(SignalName.OnStepCompleted, step.StepNumber, step.Title);
        GD.Print($"[Tutorial] Step {step.StepNumber} complete: {step.Title}");

        _currentStepIndex++;
        if (_currentStepIndex >= _steps.Length)
        {
            CompleteTutorial();
            return;
        }
        ShowStep(_currentStepIndex);
    }

    private void ShowStep(int index)
    {
        var step = _steps[index];
        _stepLabel.Text = $"Step {step.StepNumber}/{_steps.Length}";
        _titleLabel.Text = step.Title;
        _instructionLabel.Text = step.Instructions;

        // Highlight target UI element
        HighlightTarget(step.TargetNodePath);
    }

    private void CompleteTutorial()
    {
        _tutorialActive = false;
        _tutorialComplete = true;
        _highlightRect.Visible = false;
        Visible = false;
        EmitSignal(SignalName.OnTutorialCompleted);
        GD.Print("[Tutorial] Completed!");
    }

    private void SkipTutorial()
    {
        _tutorialActive = false;
        _tutorialComplete = true;
        _highlightRect.Visible = false;
        Visible = false;
        GD.Print("[Tutorial] Skipped.");
    }

    // ── Highlight ──────────────────────────────────────────────────────

    private void HighlightTarget(string nodePath)
    {
        if (string.IsNullOrEmpty(nodePath))
        {
            _highlightRect.Visible = false;
            return;
        }

        var target = GetTree()?.Root?.FindChild(nodePath, true, false) as Control;
        if (target == null)
        {
            _highlightRect.Visible = false;
            return;
        }

        var globalPos = target.GlobalPosition;
        var size = target.Size;
        _highlightRect.Position = globalPos - new Vector2(4, 4);
        _highlightRect.SetSize(size + new Vector2(8, 8));
        _highlightRect.Visible = true;
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label(); l.Text = text;
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    public override string ToString() =>
        $"[Tutorial] Active={_tutorialActive} Step={CurrentStep}/5 Complete={_tutorialComplete}";
}
