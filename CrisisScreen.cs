using Godot;
using System.Collections.Generic;

/// <summary>
/// The decision at the end of a bad night.
///
/// The design has always called for the Ledger to end with one to three
/// choices — it is the only moment the game speaks to the player directly,
/// and it is where a long session gets its punctuation. The system that was
/// meant to supply them, <see cref="CrisisNarrativeDirector"/>, existed and
/// was never instantiated, and would have deadlocked on its first crisis if
/// it had been.
///
/// This is the screen it feeds. It renders whatever scenario the director
/// hands over, authored or generated, and every option states its price
/// before it is taken. Nothing here is hidden and nothing is free.
///
/// Modal, like <see cref="NightLedgerScreen"/>: it pauses the tree and sets
/// <c>ProcessMode.Always</c> on itself, because a paused tree stops every
/// node that inherits its process mode — including, once, the screenshot
/// capture that was supposed to photograph modal screens.
/// </summary>
public partial class CrisisScreen : CanvasLayer
{
    /// <summary>A choice was taken. Carries its index in the scenario.</summary>
    [Signal]
    public delegate void OnChoiceTakenEventHandler(int choiceIndex);

    /// <summary>The screen was closed without a decision.</summary>
    [Signal]
    public delegate void OnDismissedEventHandler();

    private Control _root;
    private Label _title;
    private Label _trigger;
    private RichTextLabel _narrative;
    private VBoxContainer _choices;

    private CrisisScenario _scenario;

    public bool IsShowing => _root != null && _root.Visible;

    public override void _Ready()
    {
        Layer = 40;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
    }

    // ── Showing ────────────────────────────────────────────────────────

    /// <summary>
    /// Display a scenario. A null or choiceless scenario is refused rather
    /// than rendered — a modal with no way out is the exact failure this
    /// system already had once.
    /// </summary>
    public bool Show(CrisisScenario scenario)
    {
        if (scenario == null || scenario.Choices == null || scenario.Choices.Count == 0)
        {
            GD.PrintErr("[CrisisScreen] Refused a scenario with no choices.");
            return false;
        }

        _scenario = scenario;

        if (_root == null) BuildSkeleton();
        Populate(scenario);

        Visible = true;
        _root.Visible = true;

        if (GetTree() != null) GetTree().Paused = true;
        return true;
    }

    /// <summary>
    /// Dismiss and unpause. Shadows <c>CanvasLayer.Hide()</c> deliberately:
    /// this screen holds a pause, so a plain visibility toggle would leave
    /// the game frozen behind an invisible window.
    /// </summary>
    public new void Hide()
    {
        if (_root != null) _root.Visible = false;
        Visible = false;
        _scenario = null;

        if (GetTree() != null) GetTree().Paused = false;
    }

    // ── Skeleton ───────────────────────────────────────────────────────

    private void BuildSkeleton()
    {
        _root = new Control { Name = "CrisisRoot" };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.ProcessMode = ProcessModeEnum.Always;
        AddChild(_root);

        var scrim = new ColorRect
        {
            Name = "Scrim",
            Color = new Color(IsoTheme.Backdrop.R, IsoTheme.Backdrop.G, IsoTheme.Backdrop.B, 0.95f)
        };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrim.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.AddChild(scrim);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 32);
        margin.AddThemeConstantOverride("margin_bottom", 32);
        _root.AddChild(margin);

        var centre = new CenterContainer();
        margin.AddChild(centre);

        var panel = new PanelContainer();
        panel.CustomMinimumSize = new Vector2(640f, 0f);
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.PanelFill, IsoTheme.Danger, radius: 14, borderWidth: 2, padding: 0));
        centre.AddChild(panel);

        var inner = new MarginContainer();
        inner.AddThemeConstantOverride("margin_left", 30);
        inner.AddThemeConstantOverride("margin_right", 30);
        inner.AddThemeConstantOverride("margin_top", 24);
        inner.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(inner);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        inner.AddChild(body);

        _trigger = HudStyle.MakeLabel("", 10, IsoTheme.Danger);
        _trigger.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_trigger);

        _title = HudStyle.MakeLabel("", 30, IsoTheme.Gold);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_title);

        body.AddChild(new HSeparator());

        _narrative = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(0f, 96f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _narrative.AddThemeFontSizeOverride("normal_font_size", 15);
        _narrative.AddThemeColorOverride("default_color", IsoTheme.TextPrimary);
        body.AddChild(_narrative);

        body.AddChild(new HSeparator());

        _choices = new VBoxContainer();
        _choices.AddThemeConstantOverride("separation", 8);
        body.AddChild(_choices);

        body.AddChild(HudStyle.MakeLabel(
            "There is no option here that costs nothing.", 10, IsoTheme.TextMuted));
    }

    // ── Content ────────────────────────────────────────────────────────

    private void Populate(CrisisScenario scenario)
    {
        _title.Text = scenario.Title ?? "A Difficult Night";
        _trigger.Text = FormatTrigger(scenario.Trigger);
        _narrative.Text = scenario.Narrative ?? "";

        foreach (var child in _choices.GetChildren()) child.QueueFree();

        for (var i = 0; i < scenario.Choices.Count; i++)
            _choices.AddChild(BuildChoice(scenario.Choices[i], i));
    }

    private Control BuildChoice(CrisisChoice choice, int index)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 10));

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        card.AddChild(column);

        if (!string.IsNullOrWhiteSpace(choice.Description))
        {
            var description = HudStyle.MakeLabel(choice.Description, 12, IsoTheme.TextPrimary);
            description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(description);
        }

        // The stated effects, spelled out. A choice whose consequences are a
        // surprise is not a decision, it is a coin toss.
        var effects = DescribeEffects(choice.Effects);
        if (!string.IsNullOrEmpty(effects))
        {
            var line = HudStyle.MakeLabel(effects, 10, IsoTheme.TextMuted);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(line);
        }

        var button = new Button
        {
            Text = choice.Label ?? $"Option {index + 1}",
            CustomMinimumSize = new Vector2(0f, 38f),
            ProcessMode = ProcessModeEnum.Always
        };

        HudStyle.StyleButton(button, IsoTheme.Gold, 8, 14, 8);
        button.Pressed += () => EmitSignal(SignalName.OnChoiceTaken, index);
        column.AddChild(button);

        return card;
    }

    private static string DescribeEffects(CrisisEffects effects)
    {
        if (effects == null) return "";

        var parts = new List<string>();

        if (!Mathf.IsZeroApprox((float)effects.Cash))
            parts.Add(effects.Cash > 0
                ? $"cash +${effects.Cash:N0}"
                : $"cash −${-effects.Cash:N0}");

        if (!Mathf.IsZeroApprox(effects.Heat))
            parts.Add($"heat {effects.Heat:+0.#;−0.#}");

        if (!Mathf.IsZeroApprox(effects.Reputation))
            parts.Add($"reputation {effects.Reputation:+0.#;−0.#}");

        if (!Mathf.IsZeroApprox(effects.PublicSentiment))
            parts.Add($"public feeling {effects.PublicSentiment:+0.#;−0.#}");

        return string.Join("  ·  ", parts);
    }

    private static string FormatTrigger(string trigger) => trigger switch
    {
        "PoliceRaid" => "THE POLICE",
        "PublicScandal" => "THE PRESS",
        "WorkerWalkout" => "THE HOUSE",
        "RivalAttack" => "A RIVAL",
        "FinancialCollapse" => "THE BOOKS",
        "StaffBreakdown" => "ONE OF YOURS",
        "ReputationCollapse" => "THE EMPTY ROOMS",
        _ => "TONIGHT"
    };

    public override string ToString() =>
        $"[CrisisScreen] {(IsShowing ? $"showing \"{_scenario?.Title}\"" : "hidden")}";
}
