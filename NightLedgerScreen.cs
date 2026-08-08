using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The end-of-night summary.
///
/// This is the pacing beat the whole loop resolves into: the doors close, the
/// clouds fade, and the player is handed the books. Because the encounters
/// themselves are deliberately a black box, this screen is where the night
/// actually *happens* for the player — the quality bar chart is the only place
/// the outcome distribution is ever legible, and it is drawn with the exact
/// colours <see cref="EncounterResolver.GetVfxParameters"/> gave the clouds so
/// the two readings agree.
///
/// Built entirely in code against <see cref="IsoTheme"/>, with the built-in
/// default font and no external assets.
/// </summary>
public partial class NightLedgerScreen : CanvasLayer
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when the player dismisses the ledger.</summary>
    [Signal]
    public delegate void OnContinuePressedEventHandler();

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Whether showing the ledger pauses the scene tree.</summary>
    [Export] public bool PausesGame { get; set; } = true;

    /// <summary>Whether Escape / Enter dismiss the screen as well as the button.</summary>
    [Export] public bool DismissOnInput { get; set; } = true;

    /// <summary>Widest a quality bar may draw, in pixels.</summary>
    [Export] public float MaxBarWidth { get; set; } = 260f;

    /// <summary>
    /// Net cash that reads as a full-green night. The Net colour ramp is
    /// normalised against this so the colour means something relative to the
    /// scale of the house rather than to an absolute figure.
    /// </summary>
    [Export] public double NetColorScale { get; set; } = 500.0;

    // ── Nodes ──────────────────────────────────────────────────────────

    private Control _root;
    private Label _headerLabel;
    private Label _subheaderLabel;
    private VBoxContainer _content;
    private Button _continueButton;

    private NightReport _report;

    /// <summary>The report currently on display. Null when nothing has been shown.</summary>
    public NightReport CurrentReport => _report;

    /// <summary>Whether the ledger is currently up.</summary>
    public bool IsShowing => _root != null && _root.Visible;

    // ── Quality display order ──────────────────────────────────────────

    /// <summary>Best first — the eye should land on the good news.</summary>
    private static readonly EncounterQuality[] QualityOrder =
    {
        EncounterQuality.Exceptional,
        EncounterQuality.Good,
        EncounterQuality.Adequate,
        EncounterQuality.Poor,
        EncounterQuality.Disastrous
    };

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Must keep running while the tree is paused, or its own button dies
        // with the game it paused.
        ProcessMode = ProcessModeEnum.Always;
        Layer = 40;

        BuildSkeleton();
        Hide();

        GD.Print("[NightLedgerScreen] Ready.");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!DismissOnInput || !IsShowing) return;

        if (@event.IsActionPressed("ui_cancel") || @event.IsActionPressed("ui_accept"))
        {
            GetViewport()?.SetInputAsHandled();
            OnContinueButtonPressed();
        }
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Populate and display the ledger. A null report is tolerated and renders
    /// an empty night rather than throwing — a failed settle should not take
    /// the UI down with it.
    /// </summary>
    public void Show(NightReport report)
    {
        _report = report;

        if (_root == null) BuildSkeleton();

        Populate(report);

        Visible = true;
        _root.Visible = true;

        if (PausesGame && GetTree() != null)
            GetTree().Paused = true;

        _continueButton?.GrabFocus();
    }

    /// <summary>
    /// Dismiss the ledger and unpause. Deliberately shadows
    /// <c>CanvasLayer.Hide()</c> — hiding this screen has to release the pause
    /// it took, so the plain visibility toggle is never the right call here.
    /// </summary>
    public new void Hide()
    {
        if (_root != null) _root.Visible = false;
        Visible = false;

        if (PausesGame && GetTree() != null)
            GetTree().Paused = false;
    }

    // ── Skeleton ───────────────────────────────────────────────────────

    private void BuildSkeleton()
    {
        _root = new Control { Name = "LedgerRoot" };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.ProcessMode = ProcessModeEnum.Always;
        AddChild(_root);

        var scrim = new ColorRect
        {
            Name = "Scrim",
            Color = new Color(IsoTheme.Backdrop.R, IsoTheme.Backdrop.G, IsoTheme.Backdrop.B, 0.94f)
        };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrim.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.AddChild(scrim);

        var outerMargin = new MarginContainer { Name = "OuterMargin" };
        outerMargin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        outerMargin.AddThemeConstantOverride("margin_left", 40);
        outerMargin.AddThemeConstantOverride("margin_right", 40);
        // Tight, because the panel now expands to fill what is left and
        // every pixel here comes straight off the scroll region.
        outerMargin.AddThemeConstantOverride("margin_top", 16);
        outerMargin.AddThemeConstantOverride("margin_bottom", 16);
        _root.AddChild(outerMargin);

        // Centred horizontally by expanding spacers, not by a CenterContainer.
        //
        // A CenterContainer sizes its child to the child's *minimum*, so the
        // panel could never grow taller than its contents demanded no matter
        // how much room the window had. The scroll region was therefore fixed
        // at its 460px minimum at every resolution, and the Standing section
        // — reputation, heat, public feeling — sat permanently below the fold
        // behind a thin scrollbar most players would never notice.
        var centre = new HBoxContainer { Name = "Centre" };
        outerMargin.AddChild(centre);

        centre.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var panel = new PanelContainer { Name = "Panel" };
        panel.CustomMinimumSize = new Vector2(680f, 0f);
        panel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        panel.AddThemeStyleboxOverride("panel", MakePanelStyle());
        centre.AddChild(panel);

        centre.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var innerMargin = new MarginContainer { Name = "InnerMargin" };
        innerMargin.AddThemeConstantOverride("margin_left", 30);
        innerMargin.AddThemeConstantOverride("margin_right", 30);
        innerMargin.AddThemeConstantOverride("margin_top", 18);
        innerMargin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(innerMargin);

        var body = new VBoxContainer { Name = "Body" };
        body.AddThemeConstantOverride("separation", 10);
        innerMargin.AddChild(body);

        _headerLabel = MakeLabel("Night", 34, IsoTheme.Gold);
        _headerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_headerLabel);

        _subheaderLabel = MakeLabel("", 14, IsoTheme.TextMuted);
        _subheaderLabel.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_subheaderLabel);

        body.AddChild(MakeRule());

        var scroll = new ScrollContainer { Name = "Scroll" };

        // Low minimum, because the panel now expands: this is the floor for a
        // short window, not the height it always renders at. Leaving it at
        // 460 would force the panel taller than a 720p screen can show.
        scroll.CustomMinimumSize = new Vector2(0f, 200f);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        body.AddChild(scroll);

        _content = new VBoxContainer { Name = "Content" };
        _content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _content.AddThemeConstantOverride("separation", 14);
        scroll.AddChild(_content);

        body.AddChild(MakeRule());

        _continueButton = new Button
        {
            Name = "ContinueButton",
            Text = "Continue",
            CustomMinimumSize = new Vector2(0f, 52f)
        };
        _continueButton.AddThemeFontSizeOverride("font_size", 22);
        _continueButton.AddThemeColorOverride("font_color", IsoTheme.TextPrimary);
        _continueButton.AddThemeColorOverride("font_hover_color", IsoTheme.Gold);
        _continueButton.ProcessMode = ProcessModeEnum.Always;
        _continueButton.Pressed += OnContinueButtonPressed;
        body.AddChild(_continueButton);
    }

    private void OnContinueButtonPressed()
    {
        Hide();
        EmitSignal(SignalName.OnContinuePressed);
    }

    // ── Population ─────────────────────────────────────────────────────

    private void Populate(NightReport report)
    {
        int night = report?.Night ?? 0;

        _headerLabel.Text = night > 0 ? $"Night {night}" : "Night —";
        _subheaderLabel.Text = FrameNight(night);

        foreach (var child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }

        AddFinancials(report);
        AddClientStats(report);
        AddQualityChart(report);
        AddStringList("The night's talk", report?.Highlights, IsoTheme.TextPrimary,
            "Nothing worth repeating.");
        AddStringList("Incidents", report?.Incidents, IsoTheme.Danger,
            "No incidents. A quiet house.");
        AddStandings(report);
    }

    /// <summary>
    /// A little calendar framing so nights read as a run rather than a counter.
    /// </summary>
    private static string FrameNight(int night)
    {
        if (night <= 0) return "No books to settle.";

        int week = (night - 1) / 7 + 1;
        int day = (night - 1) % 7 + 1;

        return $"Week {week}, day {day} — the books, closed at dawn.";
    }

    private void AddFinancials(NightReport report)
    {
        var section = AddSection("The takings");

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        section.AddChild(grid);

        double revenue = report?.Revenue ?? 0.0;
        double commission = report?.StaffCommission ?? 0.0;
        double upkeep = report?.Upkeep ?? 0.0;
        double salaries = report?.Salaries ?? 0.0;
        double net = report?.Net ?? 0.0;

        AddMoneyRow(grid, "Revenue", revenue, IsoTheme.TextPrimary, 16);
        AddMoneyRow(grid, "Staff commission", -commission, IsoTheme.TextMuted, 16);
        AddMoneyRow(grid, "Furniture upkeep", -upkeep, IsoTheme.TextMuted, 16);
        AddMoneyRow(grid, "Salaries", -salaries, IsoTheme.TextMuted, 16);

        var spacerLeft = new Control { CustomMinimumSize = new Vector2(0f, 4f) };
        var spacerRight = new Control { CustomMinimumSize = new Vector2(0f, 4f) };
        grid.AddChild(spacerLeft);
        grid.AddChild(spacerRight);

        AddMoneyRow(grid, "Net", net, NetColor(net), 22);
    }

    private void AddMoneyRow(GridContainer grid, string caption, double amount, Color valueColor, int size)
    {
        var label = MakeLabel(caption, size, size >= 22 ? IsoTheme.TextPrimary : IsoTheme.TextMuted);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(label);

        var sign = amount < 0 ? "-" : "";
        var value = MakeLabel($"{sign}${Math.Abs(amount):N2}", size, valueColor);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(value);
    }

    /// <summary>
    /// Green when the night paid for itself, red when it did not, normalised
    /// against <see cref="NetColorScale"/> so the ramp in between means
    /// something. Same ramp as everything else that scores 0–100.
    /// </summary>
    private Color NetColor(double net)
    {
        double scale = Math.Abs(NetColorScale) < 1.0 ? 1.0 : NetColorScale;
        float t = Mathf.Clamp((float)(net / scale), -1f, 1f);
        return IsoTheme.GetScoreColor(50f + t * 50f);
    }

    private void AddClientStats(NightReport report)
    {
        var section = AddSection("The door");

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        section.AddChild(row);

        AddStatBlock(row, "Arrived", (report?.ClientsArrived ?? 0).ToString(), IsoTheme.TextPrimary);
        AddStatBlock(row, "Served", (report?.ClientsServed ?? 0).ToString(), IsoTheme.Good);
        AddStatBlock(row, "Turned away", (report?.ClientsTurnedAway ?? 0).ToString(),
            (report?.ClientsTurnedAway ?? 0) > 0 ? IsoTheme.Warning : IsoTheme.TextMuted);
        AddStatBlock(row, "New regulars", (report?.NewRegulars ?? 0).ToString(), IsoTheme.Gold);
    }

    private void AddStatBlock(HBoxContainer row, string caption, string value, Color valueColor)
    {
        var box = new VBoxContainer();
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.AddThemeConstantOverride("separation", 2);
        row.AddChild(box);

        var valueLabel = MakeLabel(value, 26, valueColor);
        valueLabel.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(valueLabel);

        var captionLabel = MakeLabel(caption, 12, IsoTheme.TextMuted);
        captionLabel.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(captionLabel);
    }

    /// <summary>
    /// Horizontal bars per quality band. Colours come straight from
    /// <see cref="EncounterResolver.GetVfxParameters"/> — the same values the
    /// clouds used tonight — so the player can connect what they watched to
    /// what they are now reading.
    /// </summary>
    private void AddQualityChart(NightReport report)
    {
        var section = AddSection("How it went");

        var counts = report?.QualityCounts;
        int total = 0;
        int peak = 0;

        if (counts != null)
        {
            foreach (var kvp in counts)
            {
                total += kvp.Value;
                if (kvp.Value > peak) peak = kvp.Value;
            }
        }

        if (total == 0)
        {
            section.AddChild(MakePlaceholder("No encounters were worked tonight."));
            return;
        }

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        section.AddChild(grid);

        foreach (var quality in QualityOrder)
        {
            int count = 0;
            counts?.TryGetValue(quality, out count);

            var (tint, _, _) = EncounterResolver.GetVfxParameters(quality);

            var name = MakeLabel(quality.ToString(), 14, count > 0 ? IsoTheme.TextPrimary : IsoTheme.TextMuted);
            name.CustomMinimumSize = new Vector2(120f, 0f);
            grid.AddChild(name);

            float fraction = peak <= 0 ? 0f : (float)count / peak;
            float width = Mathf.Max(3f, MaxBarWidth * fraction);

            var bar = new ColorRect
            {
                Color = count > 0 ? tint : new Color(tint.R, tint.G, tint.B, 0.22f),
                CustomMinimumSize = new Vector2(width, 16f)
            };
            bar.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            grid.AddChild(bar);

            var countLabel = MakeLabel(count.ToString(), 14,
                count > 0 ? IsoTheme.TextPrimary : IsoTheme.TextMuted);
            countLabel.CustomMinimumSize = new Vector2(34f, 0f);
            countLabel.HorizontalAlignment = HorizontalAlignment.Right;
            grid.AddChild(countLabel);
        }
    }

    private void AddStringList(string title, List<string> lines, Color color, string placeholder)
    {
        var section = AddSection(title);

        if (lines == null || lines.Count == 0)
        {
            section.AddChild(MakePlaceholder(placeholder));
            return;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var label = MakeLabel($"·  {line}", 14, color);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            section.AddChild(label);
        }
    }

    private void AddStandings(NightReport report)
    {
        var section = AddSection("Standing");

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 18);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        section.AddChild(row);

        float rep = report?.ReputationDelta ?? 0f;
        float heat = report?.HeatDelta ?? 0f;
        float sentiment = report?.SentimentDelta ?? 0f;

        // Reputation up is good; heat up is not — the colours have to invert.
        AddStatBlock(row, "Reputation", Signed(rep), DeltaColor(rep, higherIsBetter: true));
        AddStatBlock(row, "Heat", Signed(heat), DeltaColor(heat, higherIsBetter: false));

        // Sentiment moved only through crisis choices until the night loop
        // was wired to it, so this block would have read ±0.0 every night.
        AddStatBlock(row, "Public feeling", Signed(sentiment),
            DeltaColor(sentiment, higherIsBetter: true));
    }

    private static string Signed(float value)
    {
        if (Mathf.IsZeroApprox(value)) return "±0.0";
        return value > 0f ? $"+{value:F1}" : $"{value:F1}";
    }

    private static Color DeltaColor(float value, bool higherIsBetter)
    {
        if (Mathf.IsZeroApprox(value)) return IsoTheme.TextMuted;

        bool favourable = higherIsBetter ? value > 0f : value < 0f;
        return favourable ? IsoTheme.Good : IsoTheme.Danger;
    }

    // ── Widget helpers ─────────────────────────────────────────────────

    private VBoxContainer AddSection(string title)
    {
        var section = new VBoxContainer();
        section.AddThemeConstantOverride("separation", 4);
        section.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _content.AddChild(section);

        var heading = MakeLabel(title.ToUpperInvariant(), 12, IsoTheme.GoldDim);
        section.AddChild(heading);

        return section;
    }

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var label = new Label { Text = text ?? "" };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>An empty list gets a muted line rather than a hole in the layout.</summary>
    private static Label MakePlaceholder(string text)
    {
        var label = MakeLabel(text, 13, IsoTheme.TextMuted);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    private static Control MakeRule()
    {
        var rule = new ColorRect
        {
            Color = new Color(IsoTheme.GoldDim.R, IsoTheme.GoldDim.G, IsoTheme.GoldDim.B, 0.55f),
            CustomMinimumSize = new Vector2(0f, 1f)
        };
        rule.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return rule;
    }

    private static StyleBoxFlat MakePanelStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = IsoTheme.Facade,
            BorderColor = IsoTheme.GoldDim,
            ContentMarginLeft = 0f,
            ContentMarginRight = 0f,
            ContentMarginTop = 0f,
            ContentMarginBottom = 0f,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };

        style.SetBorderWidthAll(2);
        return style;
    }

    public override string ToString() =>
        $"[NightLedgerScreen] {(IsShowing ? "showing" : "hidden")} — {_report?.ToString() ?? "no report"}";
}
