using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── LicencesPanel ──────────────────────────────────────────────────────

/// <summary>
/// The permits ledger: the only page in the game that sells a ceiling rather
/// than a thing.
///
/// Every other purchase the house makes buys an instance — this room, that
/// armchair, one more hire — and none of them move a limit. Licences move
/// limits and nothing else does, so the panel says that plainly in the footer
/// and states each licence's <see cref="FacilityLicence.Effect"/> as the
/// headline of its card. The effect is the product; the price and the wait
/// are what it costs.
///
/// Everything shown is read off <see cref="FacilityLicences"/> rather than
/// recomputed here. Held state comes from
/// <see cref="FacilityLicences.IsGranted"/>, the countdown comes from
/// <see cref="FacilityLicences.InProgress"/>, and a blocked card prints the
/// refusal from <see cref="FacilityLicences.CanApply"/> verbatim — the system
/// owns the reason, and a second copy of that wording here would drift from
/// it the first time a rule changed.
///
/// Built entirely in code. Safe with no system bound, nothing held,
/// everything held, and applications part-way through their wait.
/// </summary>
public partial class LicencesPanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>The player dismissed the panel.</summary>
    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    /// <summary>
    /// An application was accepted and paid for. Carries the
    /// <see cref="FacilityLicence.Id"/>. Emitted only on success — a refusal
    /// is shown on the card and goes no further.
    /// </summary>
    [Signal]
    public delegate void OnLicenceAppliedEventHandler(string licenceId);

    // ── Layout ─────────────────────────────────────────────────────────

    /// <summary>Panel width. Wide enough for an effect line without shredding it.</summary>
    public const int PanelWidth = 400;

    // ── Bound systems ──────────────────────────────────────────────────

    private FacilityLicences _licences;

    // ── Widgets ────────────────────────────────────────────────────────

    private VBoxContainer _body;
    private Label _subtitle;
    private Label _status;

    private string _statusText = "";
    private Color _statusColour = IsoTheme.TextMuted;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUi();
        Refresh();
    }

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Point the panel at the licence system. Null is legal — the body then
    /// says nothing is bound rather than throwing. Safe to call again, and
    /// safe before the node enters the tree.
    /// </summary>
    public void Bind(FacilityLicences licences)
    {
        _licences = licences;
        SetStatus("", IsoTheme.TextMuted);

        BuildUi();
        Refresh();
    }

    // ── Construction ───────────────────────────────────────────────────

    private void BuildUi()
    {
        if (_body != null) return;

        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(PanelWidth, 0);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(14, 10));
        AddChild(frame);

        // SetAnchorsPreset alone rewrites the anchors and leaves the offsets
        // stale, so the control keeps its old rect and the PanelContainer
        // inside collapses to its minimum size — a bare header strip. The
        // AndOffsets variant zeroes both, and it has to run after AddChild
        // because anchors resolve against the parent.
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

        text.AddChild(HudStyle.MakeLabel("LICENCES", 16, IsoTheme.Gold));

        _subtitle = HudStyle.MakeLabel("", 9, IsoTheme.TextMuted);
        _subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddChild(_subtitle);

        var close = new Button { Text = "✕", CustomMinimumSize = new Vector2(30, 30) };
        HudStyle.StyleButton(close, IsoTheme.Danger);
        close.Pressed += () => EmitSignal(SignalName.OnCloseRequested);
        bar.AddChild(close);
    }

    // The footer sits outside the scroll: the rule it states is the reason
    // the page exists, and it should not scroll away from the cards.
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
            "A licence is the only thing that raises a limit. Everything else the " +
            "house buys — a room, a chair, a hire — buys one more of a thing, not " +
            "a higher ceiling to put things under.",
            IsoTheme.GoldDim));

        _status = Wrap("", IsoTheme.TextMuted);
        _status.Visible = false;
        box.AddChild(_status);
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>Rebuild every card from the system's current state.</summary>
    public void Refresh()
    {
        BuildUi();
        if (_body == null) return;

        foreach (var child in _body.GetChildren()) child.QueueFree();

        var total = FacilityLicences.Catalogue?.Count ?? 0;

        if (_licences == null)
        {
            _subtitle.Text = $"0 of {total} held";
            _body.AddChild(Wrap(
                "No licensing office is bound to this panel. Nothing can be applied " +
                "for from here.", IsoTheme.TextMuted));
            RenderStatus();
            return;
        }

        var held = _licences.Granted?.Count ?? 0;
        var pending = _licences.InProgress?.Count ?? 0;

        _subtitle.Text = pending > 0
            ? $"{held} of {total} held  ·  {pending} with the clerks"
            : $"{held} of {total} held";

        foreach (var line in new[]
                 {
                     LicenceLine.Craft, LicenceLine.House,
                     LicenceLine.Hours, LicenceLine.Space
                 })
        {
            var group = (FacilityLicences.Catalogue ?? new List<FacilityLicence>())
                .Where(l => l != null && l.Line == line)
                // Tier 1 before tier 2: a licence with no prerequisite is the
                // one you can reach first, whatever order the catalogue lists.
                .OrderBy(l => string.IsNullOrEmpty(l.Prerequisite) ? 0 : 1)
                .ThenBy(l => l.Cost)
                .ToList();

            if (group.Count == 0) continue;

            _body.AddChild(BuildLineHeading(line));

            foreach (var licence in group)
                _body.AddChild(BuildCard(licence, LineColour(line)));
        }

        RenderStatus();
    }

    private Control BuildLineHeading(LicenceLine line)
    {
        var accent = LineColour(line);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 1);

        box.AddChild(HudStyle.MakeLabel(line.ToString().ToUpperInvariant(), 12, accent));
        box.AddChild(Wrap(LineControls(line), 9, IsoTheme.TextMuted));

        return box;
    }

    // ── Cards ──────────────────────────────────────────────────────────

    private Control BuildCard(FacilityLicence licence, Color accent)
    {
        var id = licence?.Id;

        var granted = id != null && _licences != null && _licences.IsGranted(id);
        var inProgress = id != null && _licences != null && _licences.IsInProgress(id);

        var trim = granted ? IsoTheme.Good
            : inProgress ? IsoTheme.Warning
            : accent;

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, trim, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        // Name, and the state word on the same line so a scan down the column
        // reads as a list of what is held.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        box.AddChild(head);

        var name = HudStyle.MakeLabel(licence?.Name ?? "(unnamed licence)", 13, IsoTheme.TextPrimary);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        head.AddChild(name);

        if (granted)
            head.AddChild(HudStyle.MakeLabel("HELD", 9, IsoTheme.Good));
        else if (inProgress)
            head.AddChild(HudStyle.MakeLabel("FILED", 9, IsoTheme.Warning));

        // Price and wait. Muted: these are the cost of the product, not the
        // product.
        box.AddChild(Wrap(
            $"${(licence?.Cost ?? 0):N0}  ·  {Mathf.Max(1, licence?.Days ?? 1)} days to be granted",
            9, IsoTheme.TextMuted));

        // The effect is what is actually being bought, so it gets the accent
        // and the larger type.
        if (!string.IsNullOrWhiteSpace(licence?.Effect))
            box.AddChild(Wrap(licence.Effect, 12, granted ? IsoTheme.Good : accent));

        if (!string.IsNullOrWhiteSpace(licence?.Flavour))
            box.AddChild(Wrap(licence.Flavour, 9, IsoTheme.TextMuted));

        AppendState(box, licence, granted, inProgress);

        return card;
    }

    /// <summary>
    /// The one row that changes: held, waiting, appliable, or refused. Every
    /// branch is decided by the system, never by a guess made here.
    /// </summary>
    private void AppendState(
        VBoxContainer box, FacilityLicence licence, bool granted, bool inProgress)
    {
        var id = licence?.Id;

        if (granted)
        {
            box.AddChild(Wrap("Granted. The ceiling is already moved.", IsoTheme.Good));
            return;
        }

        if (inProgress)
        {
            var days = DaysRemaining(id);
            box.AddChild(Wrap(
                days == 1
                    ? "Filed. It is granted tomorrow."
                    : $"Filed. {days} days before it is granted.",
                IsoTheme.Warning));
            return;
        }

        if (_licences == null || string.IsNullOrEmpty(id))
        {
            var dead = new Button { Text = "Apply", Disabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            HudStyle.StyleButton(dead, IsoTheme.GoldDim, fontSize: 12);
            box.AddChild(dead);
            return;
        }

        bool canApply;
        string reason;
        try
        {
            canApply = _licences.CanApply(id, out reason);
        }
        catch (Exception e)
        {
            canApply = false;
            reason = e.Message;
        }

        // Blocked: print the system's refusal exactly as it gave it. Rewording
        // it here would mean two places decide what the rule is.
        if (!canApply && !string.IsNullOrWhiteSpace(reason))
            box.AddChild(Wrap(reason, IsoTheme.Warning));

        var button = new Button
        {
            Text = "Apply",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Disabled = !canApply
        };
        HudStyle.StyleButton(button, canApply ? LineColour(licence.Line) : IsoTheme.GoldDim, fontSize: 12);

        if (canApply)
            button.Pressed += () => ApplyFor(id);

        box.AddChild(button);
    }

    // ── Applying ───────────────────────────────────────────────────────

    private void ApplyFor(string id)
    {
        if (_licences == null || string.IsNullOrEmpty(id))
        {
            SetStatus("Nothing to apply for.", IsoTheme.Warning);
            return;
        }

        bool ok;
        try
        {
            ok = _licences.Apply(id);
        }
        catch (Exception e)
        {
            SetStatus($"The application failed: {e.Message}", IsoTheme.Danger);
            Refresh();
            return;
        }

        Refresh();

        var licence = FacilityLicences.Get(id);

        if (ok)
        {
            var days = DaysRemaining(id);
            SetStatus(
                $"{licence?.Name ?? id} filed. {days} day(s) before it is granted.",
                IsoTheme.Good);

            EmitSignal(SignalName.OnLicenceApplied, id);
            return;
        }

        // The system already refused and said why; ask it again rather than
        // inventing a second explanation.
        string reason = "";
        try { _licences.CanApply(id, out reason); }
        catch (Exception) { /* leave it blank rather than guess */ }

        SetStatus(
            string.IsNullOrWhiteSpace(reason) ? "Refused, without a reason given." : reason,
            IsoTheme.Danger);
    }

    private int DaysRemaining(string id)
    {
        if (_licences?.InProgress == null || string.IsNullOrEmpty(id)) return 0;
        return _licences.InProgress.TryGetValue(id, out var days) ? days : 0;
    }

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

    // ── Copy ───────────────────────────────────────────────────────────

    /// <summary>What each line actually controls, said in plain terms.</summary>
    private static string LineControls(LicenceLine line) => line switch
    {
        LicenceLine.Craft => "The quality of furniture the catalogue will stock.",
        LicenceLine.House => "How many people may be kept on the books.",
        LicenceLine.Hours => "How long a night may run.",
        LicenceLine.Space => "How much a room may hold.",
        _ => ""
    };

    /// <summary>
    /// One colour per line so the four groups read as separate ledgers. All
    /// four come from <see cref="IsoTheme"/>; none are invented here.
    /// </summary>
    private static Color LineColour(LicenceLine line) => line switch
    {
        LicenceLine.Craft => IsoTheme.Gold,
        LicenceLine.House => IsoTheme.Good,
        LicenceLine.Hours => IsoTheme.Warning,
        LicenceLine.Space => IsoTheme.GetStyleColor(FurnitureStyle.Modern),
        _ => IsoTheme.TextMuted
    };

    // ── Small builders ─────────────────────────────────────────────────

    private static Label Wrap(string text, Color colour) => Wrap(text, 10, colour);

    private static Label Wrap(string text, int fontSize, Color colour)
    {
        var label = HudStyle.MakeLabel(text, fontSize, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    public override string ToString() =>
        $"[LicencesPanel] {(_licences == null ? "unbound" : $"{_licences.Granted.Count} held")}";
}
