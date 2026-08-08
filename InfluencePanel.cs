using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The bribe ledger: standing monthly allocations to the three municipal
/// figures, and what each relationship buys.
///
/// This exists because expansion is gated on Commissioner favour and there
/// was no way to raise it — a house could never grow past its founding three
/// floors, which made the gate a dead end rather than a decision. Favour is
/// bought slowly with money that produces nothing directly, which is exactly
/// the point: the political layer is a long, unglamorous investment that
/// only pays off later.
/// </summary>
public partial class InfluencePanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    [Signal]
    public delegate void OnAllocationChangedEventHandler(int figure, double amount);

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Steps offered for a monthly allocation, in dollars.</summary>
    private static readonly double[] AllocationSteps = { 0, 100, 250, 500, 1000 };

    // ── State ──────────────────────────────────────────────────────────

    private PoliticalInfluenceSystem _politics;
    private VenueBuilding _venue;

    private VBoxContainer _body;
    private Label _subtitle;
    private bool _built;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready() => Build();

    public void Bind(PoliticalInfluenceSystem politics, VenueBuilding venue)
    {
        _politics = politics;
        _venue = venue;

        Build();
        Refresh();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;

        // SetAnchorsPreset alone only rewrites the anchors and leaves the
        // offsets where they were, so the control keeps a stale rect and the
        // PanelContainer inside falls back to its minimum size — which
        // collapsed this panel to a bare header strip. The AndOffsets variant
        // zeroes both. It also has to run after AddChild, since anchors are
        // resolved against the parent.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel());
        AddChild(frame);

        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        frame.AddChild(outer);

        // Header
        var header = new HBoxContainer();
        outer.AddChild(header);

        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddChild(titles);

        titles.AddChild(HudStyle.MakeLabel("INFLUENCE", 16, IsoTheme.Gold));
        _subtitle = HudStyle.MakeLabel("", 11, IsoTheme.TextMuted);
        titles.AddChild(_subtitle);

        var close = new Button { Text = "✕", CustomMinimumSize = new Vector2(30, 30) };
        HudStyle.StyleButton(close, IsoTheme.Danger);
        close.Pressed += () => EmitSignal(SignalName.OnCloseRequested);
        header.AddChild(close);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        outer.AddChild(scroll);

        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_body);
    }

    // ── Rendering ──────────────────────────────────────────────────────

    public void Refresh()
    {
        Build();
        if (_body == null) return;

        foreach (var child in _body.GetChildren()) child.QueueFree();

        if (_politics == null)
        {
            _body.AddChild(HudStyle.MakeLabel(
                "No one to talk to.", 12, IsoTheme.TextMuted));
            return;
        }

        _subtitle.Text = $"${_politics.TotalMonthlyAllocation:N0} a month, paid every thirty days.";

        _body.AddChild(Wrap(
            "Favour decays on its own. A standing allocation buys it back slowly; " +
            "missing a payment costs more than it saves.", IsoTheme.TextMuted));

        foreach (MunicipalFigure figure in Enum.GetValues<MunicipalFigure>())
            _body.AddChild(BuildFigureCard(figure));

        _body.AddChild(BuildPermitSection());
    }

    private Control BuildFigureCard(MunicipalFigure figure)
    {
        var relationship = _politics.GetRelationship(figure);

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel(relationship.Name, 13, IsoTheme.TextPrimary));
        box.AddChild(Wrap(GetRoleDescription(figure), IsoTheme.TextMuted));

        box.AddChild(BuildBar("Favour", relationship.Favor,
            IsoTheme.GetScoreColor(relationship.Favor)));

        box.AddChild(Wrap(GetBenefitLine(figure), IsoTheme.Good));

        // Allocation buttons
        box.AddChild(HudStyle.MakeLabel("MONTHLY", 9, IsoTheme.Gold));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        box.AddChild(row);

        foreach (var step in AllocationSteps)
        {
            var active = Mathf.IsEqualApprox((float)relationship.MonthlyAllocation, (float)step);

            var button = new Button
            {
                Text = step <= 0 ? "None" : $"${step:N0}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Disabled = active
            };

            HudStyle.StyleButton(button, active ? IsoTheme.Gold : IsoTheme.GoldDim, fontSize: 11);

            var amount = step;
            button.Pressed += () =>
            {
                _politics.SetAllocation(figure, amount);
                EmitSignal(SignalName.OnAllocationChanged, (int)figure, amount);
                Refresh();
            };

            row.AddChild(button);
        }

        return card;
    }

    private Control BuildPermitSection()
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel("ZONING", 11, IsoTheme.Gold));

        if (!_politics.ZoningPermitsUnlocked)
        {
            box.AddChild(Wrap(
                $"Commissioner Ashford will not discuss permits below 60 favour. " +
                $"He is at {_politics.CommissionerFavor:F0}.", IsoTheme.Warning));
            return card;
        }

        if (_venue == null)
        {
            box.AddChild(Wrap("Permits are available.", IsoTheme.Good));
            return card;
        }

        var next = _venue.HighestFloor + 1;
        var required = _venue.GetExpansionReputationRequired();
        var cost = _venue.GetFloorCost(next);

        box.AddChild(Wrap(
            $"Next storey: floor {next}, ${cost:N0}, needs {required:F0} reputation.",
            IsoTheme.TextMuted));

        box.AddChild(Wrap(
            _venue.CanBuyFloor(next, out var reason)
                ? "Everything is in place. Build it from the Rooms panel."
                : reason,
            _venue.CanBuyFloor(next, out _) ? IsoTheme.Good : IsoTheme.Warning));

        return card;
    }

    // ── Copy ───────────────────────────────────────────────────────────

    private static string GetRoleDescription(MunicipalFigure figure) => figure switch
    {
        MunicipalFigure.PrecinctCaptain =>
            "Runs the precinct. Paid enough, his patrols find other streets to walk.",
        MunicipalFigure.DistrictAttorney =>
            "Decides what gets charged. Useful only after something has gone wrong.",
        MunicipalFigure.CityCommissioner =>
            "Signs the zoning. Nothing gets built above this house without him.",
        _ => ""
    };

    private string GetBenefitLine(MunicipalFigure figure) => figure switch
    {
        MunicipalFigure.PrecinctCaptain =>
            $"Bleeding off {_politics.HeatReductionMultiplier * 100f:F0}% of the heat you draw.",

        MunicipalFigure.DistrictAttorney =>
            $"{_politics.ChargeDismissalChance * 100f:F0}% chance charges are dropped after a raid.",

        MunicipalFigure.CityCommissioner =>
            _politics.ZoningPermitsUnlocked
                ? "Will sign an expansion permit."
                : "Will not sign anything yet.",

        _ => ""
    };

    // ── Small builders ─────────────────────────────────────────────────

    private static Control BuildBar(string label, float value, Color colour)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var name = HudStyle.MakeLabel(label, 10, IsoTheme.TextMuted);
        name.CustomMinimumSize = new Vector2(54, 0);
        row.AddChild(name);

        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = Mathf.Clamp(value, 0f, 100f),
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 10),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };

        bar.AddThemeStyleboxOverride("background",
            HudStyle.Box(IsoTheme.FacadeShadow, IsoTheme.FacadeShadow, radius: 4, borderWidth: 0, padding: 0));
        bar.AddThemeStyleboxOverride("fill",
            HudStyle.Box(colour, colour, radius: 4, borderWidth: 0, padding: 0));

        row.AddChild(bar);

        var number = HudStyle.MakeLabel($"{value:F0}", 10, colour);
        number.CustomMinimumSize = new Vector2(26, 0);
        number.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(number);

        return row;
    }

    private static Label Wrap(string text, Color colour)
    {
        var label = HudStyle.MakeLabel(text, 10, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    public override string ToString() =>
        $"[InfluencePanel] ${_politics?.TotalMonthlyAllocation ?? 0:N0}/mo";
}
