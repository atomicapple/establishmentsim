using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The book: every client the house has served more than once, what they are
/// worth, and what is known about them.
///
/// This is the readable face of <see cref="RegularsRegistry"/>, which is where
/// the game's long loop closes. Service quality raises satisfaction,
/// satisfaction brings people back, people who come back spend more and
/// eventually become Patrons, and Patrons are the only clients an Informant
/// can take anything from. Intel buys political favour, favour buys permits,
/// permits let the house grow. None of that chain is visible anywhere else,
/// so this panel states each link plainly on the card of the person it
/// applies to.
///
/// First-timers are deliberately excluded. Someone seen once is not yet in
/// the book, and listing strangers would bury the handful of names that
/// actually matter.
///
/// Built entirely in code. Safe with no registry bound, an empty book, and a
/// book of fifty or more people.
/// </summary>
public partial class PatronsPanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>The player dismissed the panel.</summary>
    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    /// <summary>A client's card was picked. Carries their <see cref="Patron.Id"/>.</summary>
    [Signal]
    public delegate void OnPatronSelectedEventHandler(string patronId);

    // ── Layout ─────────────────────────────────────────────────────────

    /// <summary>Panel width. Wide enough for a name, a purse and a bar.</summary>
    public const int PanelWidth = 400;

    // ── Bound systems ──────────────────────────────────────────────────

    private RegularsRegistry _regulars;

    // ── Widgets ────────────────────────────────────────────────────────

    private VBoxContainer _body;
    private Label _subtitle;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUi();
        Refresh();
    }

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Point the panel at the registry. Null is fine — the body then says the
    /// book is not loaded rather than throwing. Safe to call again, and safe
    /// before the node enters the tree.
    /// </summary>
    public void Bind(RegularsRegistry regulars)
    {
        _regulars = regulars;

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
        // where they were, so the control keeps a stale rect and the
        // PanelContainer inside collapses to its minimum size — a bare header
        // strip. The AndOffsets variant zeroes both, and it has to run after
        // AddChild because anchors resolve against the parent.
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
        _body.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_body);

        // The footer sits outside the scroll: the rule it states applies to
        // every card, and it should not scroll away from them.
        column.AddChild(Small(
            "Only Patrons can be mined for intel, and only an Informant on the " +
            "roster does the mining.", IsoTheme.GoldDim));
    }

    private void BuildHeader(VBoxContainer column)
    {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 6);
        column.AddChild(bar);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        bar.AddChild(text);

        text.AddChild(HudStyle.MakeLabel("THE BOOK", 13, IsoTheme.Gold));

        _subtitle = HudStyle.MakeLabel("", 9, IsoTheme.TextMuted);
        _subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddChild(_subtitle);

        var close = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(28, 28),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        HudStyle.StyleButton(close, IsoTheme.Danger, 8, 12, 4);
        close.Pressed += () => EmitSignal(SignalName.OnCloseRequested);
        bar.AddChild(close);
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>Re-read the book from scratch and rebuild every card.</summary>
    public void Refresh()
    {
        BuildUi();
        if (_body == null) return;

        ClearChildren(_body);

        if (_regulars == null || !IsInstanceValid(_regulars))
        {
            if (_subtitle != null) _subtitle.Text = "The book is not loaded.";

            _body.AddChild(Paragraph(
                "No register is bound, so the house is keeping no names.",
                IsoTheme.TextMuted));
            return;
        }

        var book = GetBook();

        if (_subtitle != null)
            _subtitle.Text =
                $"{_regulars.RegularCount} regular{(_regulars.RegularCount == 1 ? "" : "s")}, " +
                $"{_regulars.PatronCount} patron{(_regulars.PatronCount == 1 ? "" : "s")}.";

        BuildSummary(_body, book);

        if (book.Count == 0)
        {
            _body.AddChild(Paragraph(
                "Nobody is in the book yet. A client has to come back a second " +
                "time before the house bothers to remember them, and they only " +
                "come back if the first night was worth repeating.",
                IsoTheme.TextMuted));
            return;
        }

        foreach (var patron in book)
            _body.AddChild(MakePatronCard(patron));
    }

    /// <summary>
    /// The book, in the order it is read: Patrons first because they are the
    /// ones with something to take, then Regulars, each group by lifetime
    /// spend descending. First-timers are not in the book at all.
    /// </summary>
    private List<Patron> GetBook()
    {
        var all = _regulars?.All;
        if (all == null) return new List<Patron>();

        return all
            .Where(p => p != null && p.Standing != PatronStanding.FirstTime)
            .OrderByDescending(p => p.Standing == PatronStanding.Patron)
            .ThenByDescending(p => p.TotalSpend)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void BuildSummary(VBoxContainer parent, List<Patron> book)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim.Darkened(0.4f), 8, 1, 8));
        parent.AddChild(panel);

        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 3);
        panel.AddChild(inner);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        inner.AddChild(head);

        head.AddChild(HudStyle.MakeLabel($"{book.Count}", 16, IsoTheme.TextPrimary));

        var caption = HudStyle.MakeLabel("KNOWN CLIENTS", 9, IsoTheme.TextMuted);
        caption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(caption);

        var lifetime = book.Sum(p => p.TotalSpend);
        var spend = HudStyle.MakeLabel($"${lifetime:N0}", 14, IsoTheme.Gold);
        spend.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        spend.TooltipText = "Everything these people have spent in the house, all nights combined.";
        head.AddChild(spend);

        var unmined = _regulars?.GetIntelTargets()?.Count ?? 0;

        inner.AddChild(Small(
            unmined == 0
                ? "No patron is left unmined. Nothing new to take until another " +
                  "regular is promoted."
                : $"{unmined} patron{(unmined == 1 ? "" : "s")} " +
                  $"{(unmined == 1 ? "has" : "have")} not been mined for intel yet.",
            unmined == 0 ? IsoTheme.TextMuted : IsoTheme.Gold));
    }

    // ── Cards ──────────────────────────────────────────────────────────

    private PanelContainer MakePatronCard(Patron patron)
    {
        var isPatron = patron.Standing == PatronStanding.Patron;
        var accent = isPatron ? IsoTheme.Gold : IsoTheme.GoldDim;

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, accent.Darkened(isPatron ? 0.2f : 0.4f),
                10, isPatron ? 2 : 1, 8));

        var card = new VBoxContainer();
        card.AddThemeConstantOverride("separation", 4);
        panel.AddChild(card);

        // ── Identity ───────────────────────────────────────────────────
        var nameButton = new Button
        {
            Text = patron.Name,
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Alignment = HorizontalAlignment.Left,
            TooltipText = $"First seen on night {patron.DayFirstSeen}, " +
                          $"last on night {patron.DayLastSeen}."
        };
        HudStyle.StyleButton(nameButton, accent, 6, 13, 6);

        var capturedId = patron.Id;
        nameButton.Pressed += () => EmitSignal(SignalName.OnPatronSelected, capturedId);
        card.AddChild(nameButton);

        var badges = new HBoxContainer();
        badges.AddThemeConstantOverride("separation", 4);
        card.AddChild(badges);

        badges.AddChild(MakeBadge(
            isPatron ? "PATRON" : "REGULAR",
            isPatron ? IsoTheme.Gold : IsoTheme.TextPrimary));

        if (isPatron && !patron.IntelGathered)
            badges.AddChild(MakeBadge("UNMINED", IsoTheme.Gold));

        // ── Worth ──────────────────────────────────────────────────────
        card.AddChild(HudStyle.MakeLabel("WORTH", 9, IsoTheme.GoldDim));
        card.AddChild(Small(
            $"{patron.Visits} visit{(patron.Visits == 1 ? "" : "s")} · " +
            $"${patron.TotalSpend:N0} lifetime · ${patron.AverageSpend:N0} a night",
            IsoTheme.TextPrimary));

        AddBarRow(card, "Satisfaction", patron.Satisfaction);

        // ── What they want ─────────────────────────────────────────────
        card.AddChild(HudStyle.MakeLabel("WANTS", 9, IsoTheme.GoldDim));

        var wants = new HBoxContainer();
        wants.AddThemeConstantOverride("separation", 4);
        card.AddChild(wants);

        wants.AddChild(MakeBadge(
            RoomName(patron.PreferredRoom).ToUpperInvariant(), IsoTheme.TextMuted));
        wants.AddChild(MakeBadge(
            patron.PreferredStyle.ToString().ToUpperInvariant(),
            IsoTheme.GetStyleColor(patron.PreferredStyle)));

        card.AddChild(Small(
            $"Expects an appointment of {patron.ExpectedAppointment:F0}. " +
            "Furnish that room in that style, to that standard, or they stop coming.",
            IsoTheme.TextMuted));

        // ── Last seen ──────────────────────────────────────────────────
        card.AddChild(Small(AbsenceLine(patron), AbsenceColour(patron)));

        // ── Intel ──────────────────────────────────────────────────────
        card.AddChild(Small(IntelLine(patron), IntelColour(patron)));

        return panel;
    }

    // ── Copy ───────────────────────────────────────────────────────────

    /// <summary>How long since they were last through the door.</summary>
    private static string AbsenceLine(Patron patron)
    {
        var nights = Mathf.Max(0, patron.NightsAbsent);

        return nights switch
        {
            0 => "Here tonight.",
            1 => "Last seen last night.",
            <= 6 => $"Last seen {nights} nights ago.",
            _ => $"Not seen in {nights} nights."
        };
    }

    /// <summary>
    /// Absence is coloured on a rising scale rather than a threshold, because
    /// the registry writes people off gradually and the player should see it
    /// coming rather than be told after the fact.
    /// </summary>
    private static Color AbsenceColour(Patron patron)
    {
        var nights = Mathf.Max(0, patron.NightsAbsent);

        if (nights <= 3) return IsoTheme.TextMuted;
        if (nights <= 9) return IsoTheme.Warning;

        return IsoTheme.Danger;
    }

    private static string IntelLine(Patron patron) => patron.Standing switch
    {
        PatronStanding.Patron when patron.IntelGathered =>
            "Intel already taken. There is a file on them.",

        PatronStanding.Patron =>
            "Nothing taken from them yet. They spend like this and have a great " +
            "deal to lose — an Informant would find plenty.",

        _ =>
            "Not a patron yet. Nothing worth taking until they are a fixture."
    };

    private static Color IntelColour(Patron patron) => patron.Standing switch
    {
        PatronStanding.Patron when patron.IntelGathered => IsoTheme.TextMuted,
        PatronStanding.Patron => IsoTheme.Gold,
        _ => IsoTheme.TextMuted
    };

    /// <summary>Enum names read badly as prose; these are the house's words.</summary>
    private static string RoomName(RoomType type) => type switch
    {
        RoomType.VIPSuite => "VIP suite",
        RoomType.PrivateSuite => "Private suite",
        RoomType.VIPEntrance => "VIP entrance",
        _ => type.ToString()
    };

    // ── Widget helpers ─────────────────────────────────────────────────

    private static void AddBarRow(VBoxContainer parent, string label, float value)
    {
        var clamped = Mathf.Clamp(value, 0f, 100f);
        var colour = IsoTheme.GetScoreColor(clamped);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        parent.AddChild(row);

        var name = HudStyle.MakeLabel(label, 10, IsoTheme.TextMuted);
        name.CustomMinimumSize = new Vector2(78, 0);
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(name);

        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = clamped,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 8),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };

        bar.AddThemeStyleboxOverride("background",
            HudStyle.Box(IsoTheme.Backdrop, IsoTheme.Backdrop, 4, 0, 0));
        bar.AddThemeStyleboxOverride("fill", HudStyle.Box(colour, colour, 4, 0, 0));
        row.AddChild(bar);

        var readout = HudStyle.MakeLabel($"{clamped:F0}", 10, colour);
        readout.CustomMinimumSize = new Vector2(28, 0);
        readout.HorizontalAlignment = HorizontalAlignment.Right;
        readout.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(readout);
    }

    private static PanelContainer MakeBadge(string text, Color colour)
    {
        var badge = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel",
            HudStyle.Box(new Color(colour, 0.22f), colour, 4, 1, 4));

        var label = HudStyle.MakeLabel(text, 8, colour);
        label.MouseFilter = MouseFilterEnum.Ignore;
        badge.AddChild(label);

        return badge;
    }

    private static Label Small(string text, Color colour)
    {
        var label = HudStyle.MakeLabel(text, 9, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    private static Label Paragraph(string text, Color colour)
    {
        var label = HudStyle.MakeLabel(text, 10, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>
    /// Detach before freeing: <c>QueueFree</c> does not take effect until the
    /// end of the frame, and rebuilt cards would otherwise stack below stale
    /// ones for a frame.
    /// </summary>
    private static void ClearChildren(Node parent)
    {
        if (parent == null) return;

        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    public override string ToString() =>
        _regulars == null
            ? "[PatronsPanel] no register bound"
            : $"[PatronsPanel] {_regulars.RegularCount} regulars, " +
              $"{_regulars.PatronCount} patrons";
}
