using Godot;
using System;
using System.Collections.Generic;

// ── UnionPanel ─────────────────────────────────────────────────────────

/// <summary>
/// The labour page: what the house's own people think of how they are being
/// treated, and — when they stop working — what can be offered to end it.
///
/// This panel exists because <see cref="UnionizationManager"/> could already
/// act on the player with no way to answer. A strike fires, revenue and
/// reputation bleed every day, and all three resolutions
/// (<see cref="UnionizationManager.NegotiateProfitSharing"/>,
/// <see cref="UnionizationManager.ConcedeToDemands"/>,
/// <see cref="UnionizationManager.HireStrikebreakers"/>) sit on the manager
/// unreachable. This is the face for them.
///
/// Two states, and only two:
///
/// <list type="bullet">
/// <item>No strike — the risk meter, the threshold, and the factors actually
/// driving the number. Every factor shown is one the manager's own
/// <c>CalculateRiskDelta</c> reads. Nothing is invented here, and where the
/// calculation ignores something the player can see (loyalty), the panel says
/// so rather than implying a link that does not exist.</item>
/// <item>Strike active — headcount out, days run, daily cost, and the three
/// resolutions as cards, each stating its real price as the code charges it.</item>
/// </list>
///
/// The costs quoted on the cards are the constants in
/// <see cref="UnionizationManager"/>, not a second set kept here. If those
/// numbers move, this reads a stale figure — so where a value is a live
/// property (<c>DailyRevenueLossDuringStrike</c>,
/// <c>DailyReputationLossDuringStrike</c>, <c>StrikeSeverityThreshold</c>,
/// <c>ExploitationPolicyRiskBonus</c>) it is read off the manager. The three
/// resolution prices are hard-coded inside those methods and cannot be read,
/// so they are quoted as literals here and marked as such in this comment:
/// OPEX +$100/day, concession −$2,000, strikebreakers Heat +20 / sentiment −10.
///
/// Built entirely in code. Safe with no manager bound, no roster, an empty
/// roster, calm conditions, rising risk, and an active strike.
/// </summary>
public partial class UnionPanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>The player dismissed the panel.</summary>
    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    /// <summary>
    /// A strike was ended from this panel. Carries the name of the
    /// <see cref="UnionizationManager"/> method that was called:
    /// "NegotiateProfitSharing", "ConcedeToDemands" or "HireStrikebreakers".
    /// </summary>
    [Signal]
    public delegate void OnDisputeResolvedEventHandler(string method);

    // ── Layout ─────────────────────────────────────────────────────────

    /// <summary>Panel width. Wide enough for a resolution's cost block without shredding it.</summary>
    public const int PanelWidth = 400;

    /// <summary>What a full concession costs in cash, read from the manager.</summary>
    private double ConcessionCost => _union?.ConcessionCost ?? 2000.0;

    /// <summary>Daily OPEX added by profit-sharing. Literal inside <c>NegotiateProfitSharing</c>.</summary>
    private const double ProfitSharingOpex = 100.0;

    // ── Bound systems ──────────────────────────────────────────────────

    private UnionizationManager _union;

    // ── Widgets ────────────────────────────────────────────────────────

    private VBoxContainer _body;
    private Label _subtitle;
    private Label _status;
    private bool _built;

    private string _statusText = "";
    private Color _statusColour = IsoTheme.TextMuted;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        Build();
        Refresh();
    }

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Point the panel at the unionization system. Null is legal — the body
    /// then says nothing is bound rather than throwing. Safe to call again,
    /// and safe before the node enters the tree.
    /// </summary>
    public void Bind(UnionizationManager union)
    {
        _union = union;
        SetStatus("", IsoTheme.TextMuted);

        Build();
        Refresh();
    }

    // ── Construction ───────────────────────────────────────────────────

    private void Build()
    {
        if (_built) return;
        _built = true;

        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(PanelWidth, 0);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(14, 10));
        AddChild(frame);

        // SetAnchorsPreset alone rewrites the anchors and leaves the offsets
        // stale, so the control keeps its old rect and the PanelContainer
        // inside collapses to its minimum size — a bare header strip. The
        // AndOffsets variant zeroes both, and it has to run after AddChild
        // because anchors resolve against the parent. This has bitten three
        // separate panels in this repo.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        frame.AddChild(column);

        BuildHeader(column);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        column.AddChild(scroll);

        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_body);

        BuildFooter(column);
    }

    private void BuildHeader(VBoxContainer column)
    {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 6);
        column.AddChild(bar);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        bar.AddChild(text);

        text.AddChild(HudStyle.MakeLabel("LABOUR", 16, IsoTheme.Gold));

        _subtitle = HudStyle.MakeLabel("", 9, IsoTheme.TextMuted);
        _subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddChild(_subtitle);

        var close = new Button { Text = "✕", CustomMinimumSize = new Vector2(30, 30) };
        HudStyle.StyleButton(close, IsoTheme.Danger);
        close.Pressed += () => EmitSignal(SignalName.OnCloseRequested);
        bar.AddChild(close);
    }

    // The footer sits outside the scroll. What it states is the premise of the
    // whole page and should not scroll away from the meter.
    private void BuildFooter(VBoxContainer column)
    {
        var footer = new PanelContainer();
        footer.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 8));
        column.AddChild(footer);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        footer.AddChild(box);

        box.AddChild(Wrap(
            "Union risk is not a mood. It is a running total of how the house " +
            "has treated the people who work in it, and it is only ever paid " +
            "down by treating them differently.",
            IsoTheme.GoldDim));

        _status = Wrap("", IsoTheme.TextMuted);
        _status.Visible = false;
        box.AddChild(_status);
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>Rebuild the whole body from the manager's current state.</summary>
    public void Refresh()
    {
        Build();
        if (_body == null) return;

        foreach (var child in _body.GetChildren()) child.QueueFree();

        if (_union == null)
        {
            if (_subtitle != null) _subtitle.Text = "Nothing bound";
            _body.AddChild(Wrap(
                "No unionization system is bound to this panel. Nothing about " +
                "the house's labour position can be read or acted on from here.",
                IsoTheme.TextMuted));
            RenderStatus();
            return;
        }

        if (_union.StrikeActive) BuildStrikeState();
        else BuildRisingState();

        RenderStatus();
    }

    // ── State: no strike ───────────────────────────────────────────────

    private void BuildRisingState()
    {
        var risk = _union.UnionRisk;
        var threshold = _union.StrikeSeverityThreshold;
        var delta = EstimateDailyDelta();

        _subtitle.Text = delta > 0.05f
            ? "No strike. Risk is climbing."
            : delta < -0.05f
                ? "No strike. Risk is falling."
                : "No strike. Risk is steady.";

        _body.AddChild(BuildRiskMeter(risk, threshold, delta));
        _body.AddChild(BuildDriversCard(delta));
        _body.AddChild(BuildThresholdCard(threshold, risk, delta));
    }

    /// <summary>
    /// The meter. Coloured by <c>GetScoreColor(100 - risk)</c> because the ramp
    /// runs red-to-green and high risk is the bad end — passing the raw risk
    /// would paint a house on the brink in reassuring green.
    /// </summary>
    private Control BuildRiskMeter(float risk, float threshold, float delta)
    {
        var tint = IsoTheme.GetScoreColor(100f - risk);

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, tint, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        card.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        box.AddChild(head);

        var title = HudStyle.MakeLabel("UNION RISK", 12, IsoTheme.Gold);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        head.AddChild(HudStyle.MakeLabel($"{risk:F0}%", 18, tint));

        var meter = new UnionRiskBar
        {
            Risk = risk,
            Threshold = threshold,
            CustomMinimumSize = new Vector2(0, 16),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        box.AddChild(meter);

        box.AddChild(Wrap(
            $"Walkout at {threshold:F0}%.  Today's movement: {Signed(delta)} per day.",
            9, IsoTheme.TextMuted));

        return card;
    }

    /// <summary>
    /// What the number is actually made of. Every line here corresponds to a
    /// factor in <c>UnionizationManager.CalculateRiskDelta</c>, quoted at the
    /// manager's own thresholds. Loyalty is shown because the player watches
    /// it, and is explicitly marked as not feeding the calculation — the
    /// alternative is implying a lever that does not exist.
    /// </summary>
    private Control BuildDriversCard(float delta)
    {
        var roster = StaffRoster.Instance;
        var count = roster?.Count ?? 0;

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel("WHAT IS DRIVING IT", 12, IsoTheme.Gold));

        if (roster == null || count == 0)
        {
            box.AddChild(Wrap(
                "Nobody is on the books. With no staff there is nothing to " +
                "organise, and the risk figure moves only on standing policy.",
                IsoTheme.TextMuted));
            AppendPolicyDrivers(box);
            return card;
        }

        var stress = roster.GetAverageStress();
        var satisfaction = roster.GetAverageSatisfaction();
        var loyalty = roster.GetAverageLoyalty();

        // Factor 1 — stress above 70 adds (stress − 70) × 0.3 per day.
        AppendDriver(box, "Average stress", stress,
            IsoTheme.GetScoreColor(100f - stress),
            stress > 70f
                ? $"Above 70. Adds {(stress - 70f) * 0.3f:F1} risk every day."
                : stress < 40f
                    ? "Below 40. Low enough to count toward the daily cool-off."
                    : "Between 40 and 70. Adds nothing, and earns nothing back.");

        // Factor 3 — satisfaction below 30 adds (30 − satisfaction) × 0.2 per day.
        AppendDriver(box, "Average satisfaction", satisfaction,
            IsoTheme.GetScoreColor(satisfaction),
            satisfaction < 30f
                ? $"Below 30. Adds {(30f - satisfaction) * 0.2f:F1} risk every day."
                : satisfaction > 60f
                    ? "Above 60. High enough to count toward the daily cool-off."
                    : "Between 30 and 60. Adds nothing, and earns nothing back.");

        // Factor 4 — the only thing that pays risk down on its own.
        if (stress < 40f && satisfaction > 60f)
            box.AddChild(Wrap(
                "Stress under 40 and satisfaction over 60 together take 1.5 risk " +
                "off every day. This is the only condition that lowers it by itself.",
                IsoTheme.Good));
        else
            box.AddChild(Wrap(
                "Stress under 40 and satisfaction over 60 together would take 1.5 " +
                "risk off every day. Both are required; neither alone does anything.",
                IsoTheme.TextMuted));

        // Loyalty is shown but is honestly labelled as inert here.
        AppendDriver(box, "Average loyalty", loyalty,
            IsoTheme.GetScoreColor(loyalty),
            "Not read by the union calculation. It governs who walks out for " +
            "good and who testifies, not whether a strike is called.");

        AppendPolicyDrivers(box);

        box.AddChild(Wrap(
            $"Net movement, {count} on the books: {Signed(delta)} per day.",
            10, delta > 0.05f ? IsoTheme.Warning : delta < -0.05f ? IsoTheme.Good : IsoTheme.TextMuted));

        return card;
    }

    /// <summary>
    /// The two standing-policy factors. Both are read off
    /// <see cref="PolicyTreeManager"/> the same way the manager reads them —
    /// by node name, because that is how systems find each other here.
    /// </summary>
    private void AppendPolicyDrivers(VBoxContainer box)
    {
        var policies = FindPolicyTree();
        if (policies == null) return;

        if (policies.ActiveBranch == PolicyBranch.SystemicExploitation)
            box.AddChild(Wrap(
                $"The Systemic Exploitation branch is in force. It adds " +
                $"{_union.ExploitationPolicyRiskBonus:F1} risk every day, on its own, " +
                "regardless of how the staff are feeling.",
                IsoTheme.Danger));

        if (policies.StaffQuitDisabled)
            box.AddChild(Wrap(
                "Staff are held by contract debt and cannot leave. That adds 1.0 " +
                "risk every day: people who cannot walk out individually organise " +
                "instead.",
                IsoTheme.Danger));
    }

    private Control BuildThresholdCard(float threshold, float risk, float delta)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.Warning, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel($"AT {threshold:F0}%", 12, IsoTheme.Warning));

        box.AddChild(Wrap(
            $"Roughly half the roster walks out. Trade stops paying: the house " +
            $"loses ${_union.DailyRevenueLossDuringStrike:N0} in cash and " +
            $"{_union.DailyReputationLossDuringStrike:F1} reputation every day the " +
            "strike runs, and risk will not fall below 60% while it does. It ends " +
            "when the house answers it, and not before.",
            IsoTheme.TextPrimary));

        if (delta > 0.05f && risk < threshold)
        {
            var days = Mathf.CeilToInt((threshold - risk) / delta);
            box.AddChild(Wrap(
                days <= 1
                    ? "At today's rate, that is tomorrow."
                    : $"At today's rate, that is about {days} days away.",
                IsoTheme.Danger));
        }
        else if (delta <= 0.05f)
        {
            box.AddChild(Wrap(
                "At today's rate the house is not moving toward it.", IsoTheme.Good));
        }

        return card;
    }

    // ── State: strike active ───────────────────────────────────────────

    private void BuildStrikeState()
    {
        _subtitle.Text = "The house is not working.";

        _body.AddChild(BuildStrikeBanner());

        _body.AddChild(Wrap(
            "These are the house's own people, refusing to work over how they " +
            "have been treated. It ends when the house answers them. Three " +
            "answers are available, and each costs something different.",
            11, IsoTheme.TextPrimary));

        var cash = GameStateManager.Instance?.Cash ?? 0.0;

        // ── Negotiate ──────────────────────────────────────────────────
        _body.AddChild(BuildResolutionCard(
            "Negotiate profit sharing",
            IsoTheme.Good,
            new[]
            {
                $"Operating costs rise by ${ProfitSharingOpex:N0} a day, permanently. " +
                "There is no end date on it.",
                "Every member of staff gains 15 satisfaction.",
                "Union risk falls by 40 points. It does not reset."
            },
            "Nothing up front. The house pays for this every day it keeps trading.",
            canAfford: true,
            blockedReason: null,
            act: () => _union.NegotiateProfitSharing(),
            method: nameof(UnionizationManager.NegotiateProfitSharing)));

        // ── Concede ────────────────────────────────────────────────────
        var canConcede = cash >= ConcessionCost;
        _body.AddChild(BuildResolutionCard(
            "Concede to their demands",
            IsoTheme.Gold,
            new[]
            {
                $"${ConcessionCost:N0} paid out at once.",
                "Every member of staff is set to 80 satisfaction and loses 20 stress.",
                "Debt bondage is lifted. Anyone held by contract can leave again.",
                "Union risk resets to zero."
            },
            "The most expensive answer in cash, and the only one that settles the " +
            "question rather than deferring it.",
            canAfford: canConcede,
            blockedReason: canConcede
                ? null
                : $"The house holds ${cash:N0}. This costs ${ConcessionCost:N0}.",
            act: () => _union.ConcedeToDemands(),
            method: nameof(UnionizationManager.ConcedeToDemands)));

        // ── Strikebreakers ─────────────────────────────────────────────
        // Stated as the code applies it. No editorial either way.
        _body.AddChild(BuildResolutionCard(
            "Hire strikebreakers",
            IsoTheme.Danger,
            new[]
            {
                "Nothing is paid to the staff. The work resumes without them.",
                "Heat rises by 20.",
                "Public sentiment falls by 10.",
                "Every member of staff loses 25 satisfaction and gains 15 stress — " +
                "including those who did not strike.",
                "Union risk falls by 20 points, the smallest of the three."
            },
            "Costs no cash. The house pays for it in heat, in public standing, and " +
            "in the people it keeps.",
            canAfford: true,
            blockedReason: null,
            act: () => _union.HireStrikebreakers(),
            method: nameof(UnionizationManager.HireStrikebreakers)));
    }

    private Control BuildStrikeBanner()
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(IsoTheme.Danger.Darkened(0.72f), IsoTheme.Danger,
                radius: 8, borderWidth: 2, padding: 10));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel("STRIKE", 22, IsoTheme.Danger));

        var walkedOut = Math.Max(0, _union.StrikingStaffCount);
        var days = Math.Max(0, _union.StrikeDurationDays);
        var headcount = StaffRoster.Instance?.Count ?? 0;

        box.AddChild(Wrap(
            headcount > 0
                ? $"{walkedOut} of {headcount} staff are out."
                : $"{walkedOut} staff are out.",
            13, IsoTheme.TextPrimary));

        box.AddChild(Wrap(
            days <= 0
                ? "It started today."
                : days == 1
                    ? "It has run one day."
                    : $"It has run {days} days.",
            11, IsoTheme.TextPrimary));

        box.AddChild(Wrap(
            $"Costing ${_union.DailyRevenueLossDuringStrike:N0} and " +
            $"{_union.DailyReputationLossDuringStrike:F1} reputation every day it continues" +
            (days > 0
                ? $" — ${_union.DailyRevenueLossDuringStrike * days:N0} and " +
                  $"{_union.DailyReputationLossDuringStrike * days:F1} reputation so far."
                : "."),
            11, IsoTheme.Danger));

        return card;
    }

    /// <summary>
    /// One resolution. The cost lines are the whole point of the card, so they
    /// sit above the button and are stated flatly, in the order the method
    /// applies them.
    /// </summary>
    private Control BuildResolutionCard(
        string title, Color accent, IEnumerable<string> costs, string summary,
        bool canAfford, string blockedReason, Action act, string method)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, canAfford ? accent : IsoTheme.GoldDim,
                radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        box.AddChild(Wrap(title, 14, canAfford ? accent : IsoTheme.TextMuted));

        if (costs != null)
            foreach (var line in costs)
                if (!string.IsNullOrWhiteSpace(line))
                    box.AddChild(Wrap("· " + line, 10, IsoTheme.TextPrimary));

        if (!string.IsNullOrWhiteSpace(summary))
            box.AddChild(Wrap(summary, 9, IsoTheme.TextMuted));

        if (!canAfford && !string.IsNullOrWhiteSpace(blockedReason))
            box.AddChild(Wrap(blockedReason, 10, IsoTheme.Warning));

        var button = new Button
        {
            Text = title,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Disabled = !canAfford
        };
        HudStyle.StyleButton(button, canAfford ? accent : IsoTheme.GoldDim, fontSize: 12);

        if (canAfford)
            button.Pressed += () => Resolve(act, method, title);

        box.AddChild(button);

        return card;
    }

    // ── Acting ─────────────────────────────────────────────────────────

    private void Resolve(Action act, string method, string title)
    {
        if (_union == null || act == null)
        {
            SetStatus("There is nothing bound to act on.", IsoTheme.Warning);
            return;
        }

        if (!_union.StrikeActive)
        {
            // The manager's three resolutions all early-return when no strike
            // is running, so a stale card would silently do nothing at all.
            SetStatus("The strike is already over.", IsoTheme.TextMuted);
            Refresh();
            return;
        }

        try
        {
            act();
        }
        catch (Exception e)
        {
            SetStatus($"The resolution failed: {e.Message}", IsoTheme.Danger);
            Refresh();
            return;
        }

        Refresh();

        SetStatus(
            _union.StrikeActive
                ? $"{title} was offered and the strike did not end."
                : $"{title}. The house is working again.",
            _union.StrikeActive ? IsoTheme.Warning : IsoTheme.Good);

        EmitSignal(SignalName.OnDisputeResolved, method);
    }

    // ── Reading the manager ────────────────────────────────────────────

    /// <summary>
    /// Tonight's risk movement, asked of the manager directly.
    ///
    /// This was originally a second copy of the manager's arithmetic, because
    /// <c>CalculateRiskDelta</c> was private. It is public now, so the meter
    /// is labelled with the number the simulation will actually apply and
    /// cannot drift away from it.
    /// </summary>
    private float EstimateDailyDelta() => _union?.CalculateRiskDelta() ?? 0f;

    private PolicyTreeManager FindPolicyTree() =>
        GetTree()?.Root?.FindChild("PolicyTreeManager", recursive: true, owned: false)
            as PolicyTreeManager;

    // ── Status line ────────────────────────────────────────────────────

    private void SetStatus(string text, Color colour)
    {
        _statusText = text ?? "";
        _statusColour = colour;
        RenderStatus();
    }

    private void RenderStatus()
    {
        if (_status == null) return;

        _status.Text = _statusText ?? "";
        _status.AddThemeColorOverride("font_color", _statusColour);
        _status.Visible = !string.IsNullOrEmpty(_status.Text);
    }

    // ── Small builders ─────────────────────────────────────────────────

    private void AppendDriver(VBoxContainer box, string name, float value, Color tint, string reading)
    {
        var group = new VBoxContainer();
        group.AddThemeConstantOverride("separation", 1);
        box.AddChild(group);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        group.AddChild(row);

        var label = HudStyle.MakeLabel(name, 11, IsoTheme.TextPrimary);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(HudStyle.MakeLabel($"{value:F0}", 11, tint));

        group.AddChild(Wrap(reading, 9, IsoTheme.TextMuted));
    }

    private static string Signed(float value) =>
        Mathf.Abs(value) < 0.05f ? "no change" : $"{(value > 0 ? "+" : "")}{value:F1}";

    private static Label Wrap(string text, Color colour) => Wrap(text, 10, colour);

    private static Label Wrap(string text, int fontSize, Color colour)
    {
        var label = HudStyle.MakeLabel(text, fontSize, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    public override string ToString() =>
        _union == null
            ? "[UnionPanel] unbound"
            : $"[UnionPanel] risk {_union.UnionRisk:F0}%" +
              (_union.StrikeActive ? $", STRIKE day {_union.StrikeDurationDays}" : "");
}

// ── UnionRiskBar ───────────────────────────────────────────────────────

/// <summary>
/// The risk meter, drawn procedurally because a plain
/// <see cref="ProgressBar"/> cannot mark the walkout threshold, and the
/// threshold is the single most important thing on the bar — a fill at 74%
/// means nothing without knowing that 80 is the wall.
///
/// The fill is tinted by <c>IsoTheme.GetScoreColor(100 - risk)</c>: the ramp
/// runs red-to-green, and here the high end is the bad one.
/// </summary>
public partial class UnionRiskBar : Control
{
    private float _risk;
    private float _threshold = 80f;

    /// <summary>Current union risk, 0–100.</summary>
    public float Risk
    {
        get => _risk;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(clamped, _risk)) return;
            _risk = clamped;
            QueueRedraw();
        }
    }

    /// <summary>Where the walkout fires, 0–100.</summary>
    public float Threshold
    {
        get => _threshold;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(clamped, _threshold)) return;
            _threshold = clamped;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var box = Size;
        if (box.X <= 1f || box.Y <= 1f) return;

        var track = new Rect2(Vector2.Zero, box);
        DrawRect(track, IsoTheme.Backdrop, true);

        // Everything past the threshold is shaded, so the wall reads as a
        // region rather than a single line the fill might hide under.
        var wallX = box.X * Mathf.Clamp(_threshold / 100f, 0f, 1f);
        if (wallX < box.X)
            DrawRect(new Rect2(wallX, 0f, box.X - wallX, box.Y),
                new Color(IsoTheme.Danger, 0.22f), true);

        var fillWidth = box.X * Mathf.Clamp(_risk / 100f, 0f, 1f);
        if (fillWidth > 0f)
            DrawRect(new Rect2(0f, 0f, fillWidth, box.Y),
                IsoTheme.GetScoreColor(100f - _risk), true);

        // The threshold itself, over the fill.
        DrawRect(new Rect2(wallX - 1f, -1f, 2f, box.Y + 2f), IsoTheme.Danger, true);

        DrawRect(track, IsoTheme.GoldDim, false, 1f);
    }
}
