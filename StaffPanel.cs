using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>
/// The two halves of the staff panel: who works here, and who could.
/// </summary>
public enum StaffPanelTab
{
    /// <summary>The people already in the house.</summary>
    Roster,

    /// <summary>The hiring market and the candidates it has produced.</summary>
    Hiring
}

// ── StaffPanel ─────────────────────────────────────────────────────────

/// <summary>
/// The house's people, in two halves.
///
/// The Roster half is deliberately not a stat block. Every card leads with a
/// name and an origin, and the four agency variables sit directly under the
/// three RPG stats so that "she is very good at this" and "she is falling
/// apart" are read in the same glance. Stress and Trauma are coloured
/// backwards from the rest — high is bad — because a green trauma bar would
/// be a lie the interface tells the player.
///
/// The Hiring half is the moral economy of <see cref="RecruitmentService"/>
/// made legible. Each channel states its price in one line before the player
/// commits, and every candidate carries their <see cref="Candidate.ConsequenceNote"/>
/// verbatim — the service wrote that sentence to be read, not summarised.
/// Debt contracts are marked in <see cref="IsoTheme.Danger"/> throughout,
/// including on staff already hired, because a bound person is a standing
/// condition of the house and not a completed transaction.
///
/// The panel owns hiring: it calls <see cref="RecruitmentService.Hire"/>,
/// <see cref="RecruitmentService.PostOpenCall"/> and
/// <see cref="RecruitmentService.SolicitOffer"/> directly, then emits
/// <see cref="SignalName.OnRosterChanged"/> so the integrator can repaint the
/// pawn layer. It never touches the roster itself beyond reading it, and it
/// never touches the view.
///
/// Built entirely in code. Safe with no recruitment service, no roster, an
/// empty roster, a full roster, and zero candidates.
/// </summary>
public partial class StaffPanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>A staff member's card was picked. Carries their <see cref="StaffMember.Id"/>.</summary>
    [Signal]
    public delegate void OnStaffSelectedEventHandler(string staffId);

    /// <summary>The player dismissed the panel.</summary>
    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    /// <summary>
    /// The roster gained someone. Emitted after a successful hire so the
    /// integrator can repaint the pawns.
    /// </summary>
    [Signal]
    public delegate void OnRosterChangedEventHandler();

    // ── Layout ─────────────────────────────────────────────────────────

    /// <summary>Panel width. Wide enough for a name, four bars and a price.</summary>
    public const int PanelWidth = 392;

    /// <summary>Training hours spent per press of a Train button.</summary>
    public const float TrainingHours = 4f;

    // ── Bound systems ──────────────────────────────────────────────────

    private RecruitmentService _recruitment;
    private bool _connected;

    /// <summary>
    /// Day the posted open call is expected to produce responses, or −1.
    /// The service keeps this private, so it is captured from the signal.
    /// </summary>
    private int _openCallArrivalDay = -1;

    // ── Widgets ────────────────────────────────────────────────────────

    private VBoxContainer _body;
    private Label _title;
    private Label _subtitle;
    private Label _notice;
    private Button _rosterTabButton;
    private Button _hiringTabButton;

    private StaffPanelTab _tab = StaffPanelTab.Roster;

    /// <summary>The half currently on screen.</summary>
    public StaffPanelTab CurrentTab => _tab;

    /// <summary>The roster this panel reads. Never cached — it can arrive late.</summary>
    private static StaffRoster Roster => StaffRoster.Instance;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUi();
        Refresh();
    }

    public override void _ExitTree() => DisconnectRecruitment();

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Point the panel at the hiring system. Null is fine — the Hiring tab
    /// then explains that no one is taking applications rather than throwing.
    /// Safe to call again and safe before the node enters the tree.
    /// </summary>
    public void Bind(RecruitmentService recruitment)
    {
        DisconnectRecruitment();

        _recruitment = recruitment;

        if (_body == null) BuildUi();

        ConnectRecruitment();
        Refresh();
    }

    /// <summary>Switch halves. Rebuilds the body.</summary>
    public void ShowTab(StaffPanelTab tab)
    {
        _tab = tab;

        if (_body == null) BuildUi();
        Refresh();
    }

    /// <summary>Re-read the roster, the market and the player's cash from scratch.</summary>
    public void Refresh()
    {
        if (_body == null) return;

        RefreshHeader();
        RefreshTabButtons();

        ClearChildren(_body);

        if (_tab == StaffPanelTab.Roster) BuildRosterTab(_body);
        else BuildHiringTab(_body);
    }

    // ── Signal wiring ──────────────────────────────────────────────────

    private void ConnectRecruitment()
    {
        if (_connected || _recruitment == null || !IsInstanceValid(_recruitment)) return;

        _recruitment.OnOpenCallPosted += HandleOpenCallPosted;
        _recruitment.OnCandidatesAvailable += HandleCandidatesAvailable;
        _recruitment.OnCandidateHired += HandleCandidateHired;
        _recruitment.OnHireRejected += HandleHireRejected;
        _connected = true;
    }

    private void DisconnectRecruitment()
    {
        if (!_connected || _recruitment == null || !IsInstanceValid(_recruitment))
        {
            _connected = false;
            return;
        }

        _recruitment.OnOpenCallPosted -= HandleOpenCallPosted;
        _recruitment.OnCandidatesAvailable -= HandleCandidatesAvailable;
        _recruitment.OnCandidateHired -= HandleCandidateHired;
        _recruitment.OnHireRejected -= HandleHireRejected;
        _connected = false;
    }

    private void HandleOpenCallPosted(int arrivesOnDay, double cost)
    {
        _openCallArrivalDay = arrivesOnDay;
        SetNotice($"Advertisement placed for ${cost:N0}. Responses expected on night {arrivesOnDay}.",
            IsoTheme.TextMuted);
        Refresh();
    }

    private void HandleCandidatesAvailable(int channel, int count)
    {
        _openCallArrivalDay = -1;
        SetNotice($"{count} {(count == 1 ? "person is" : "people are")} " +
                  $"waiting to be seen ({ChannelName((RecruitmentChannel)channel)}).",
            IsoTheme.Gold);
        Refresh();
    }

    private void HandleCandidateHired(string staffId, string staffName, int channel)
    {
        SetNotice($"{staffName} has joined the house.", IsoTheme.Good);
        Refresh();
    }

    private void HandleHireRejected(string reason)
    {
        SetNotice(reason, IsoTheme.Danger);
        Refresh();
    }

    private void SetNotice(string text, Color colour)
    {
        if (_notice == null) return;

        _notice.Text = text ?? "";
        _notice.Visible = !string.IsNullOrEmpty(_notice.Text);
        _notice.AddThemeColorOverride("font_color", colour);
    }

    // ── Construction ───────────────────────────────────────────────────

    private void BuildUi()
    {
        if (_body != null) return;

        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(PanelWidth, 0);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(14, 10));
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(frame);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        frame.AddChild(column);

        BuildHeader(column);
        BuildTabRow(column);

        _notice = HudStyle.MakeLabel("", 10, IsoTheme.TextMuted);
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.Visible = false;
        column.AddChild(_notice);

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
    }

    private void BuildHeader(VBoxContainer column)
    {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 6);
        column.AddChild(bar);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        bar.AddChild(text);

        _title = HudStyle.MakeLabel("THE HOUSE", 13, IsoTheme.Gold);
        text.AddChild(_title);

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

    private void BuildTabRow(VBoxContainer column)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        column.AddChild(row);

        _rosterTabButton = new Button
        {
            Text = "Roster",
            CustomMinimumSize = new Vector2(0, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _rosterTabButton.Pressed += () => ShowTab(StaffPanelTab.Roster);
        row.AddChild(_rosterTabButton);

        _hiringTabButton = new Button
        {
            Text = "Hiring",
            CustomMinimumSize = new Vector2(0, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _hiringTabButton.Pressed += () => ShowTab(StaffPanelTab.Hiring);
        row.AddChild(_hiringTabButton);
    }

    private void RefreshTabButtons()
    {
        HudStyle.StyleButton(_rosterTabButton,
            _tab == StaffPanelTab.Roster ? IsoTheme.Gold : IsoTheme.GoldDim, 8, 12, 8);
        HudStyle.StyleButton(_hiringTabButton,
            _tab == StaffPanelTab.Hiring ? IsoTheme.Gold : IsoTheme.GoldDim, 8, 12, 8);
    }

    private void RefreshHeader()
    {
        var roster = Roster;

        if (_title != null)
            _title.Text = _tab == StaffPanelTab.Roster ? "THE HOUSE" : "HIRING";

        if (_subtitle == null) return;

        if (roster == null)
        {
            _subtitle.Text = "No roster is loaded.";
            return;
        }

        _subtitle.Text = _tab == StaffPanelTab.Roster
            ? $"{roster.Count} of {roster.RosterCap} places filled."
            : roster.HasVacancy
                ? $"{roster.RosterCap - roster.Count} place" +
                  $"{(roster.RosterCap - roster.Count == 1 ? "" : "s")} left in the house."
                : "The house is full. No one else can be taken on.";
    }

    // ── Roster tab ─────────────────────────────────────────────────────

    private void BuildRosterTab(VBoxContainer parent)
    {
        var roster = Roster;

        if (roster == null)
        {
            parent.AddChild(Paragraph(
                "No roster is loaded, so there is no one to account for.",
                IsoTheme.TextMuted));
            return;
        }

        BuildRosterSummary(parent);

        var staff = roster.GetAll()
            .Where(s => s != null)
            .OrderBy(s => s.StaffName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (staff.Count == 0)
        {
            parent.AddChild(Paragraph(
                "Nobody works here yet. Whoever you take on first will set the " +
                "tone of the house — look at Hiring, and read what each channel costs.",
                IsoTheme.TextMuted));
            return;
        }

        foreach (var member in staff)
            parent.AddChild(MakeStaffCard(member));

        BuildRelationships(parent, roster);
    }

    private void BuildRosterSummary(VBoxContainer parent)
    {
        var roster = Roster;
        if (roster == null) return;

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

        var count = HudStyle.MakeLabel(
            $"{roster.Count} / {roster.RosterCap}", 16,
            roster.HasVacancy ? IsoTheme.TextPrimary : IsoTheme.Warning);
        head.AddChild(count);

        var caption = HudStyle.MakeLabel("ON THE ROSTER", 9, IsoTheme.TextMuted);
        caption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(caption);

        var salaries = HudStyle.MakeLabel($"${roster.GetTotalSalaries():N0}/mo", 12, IsoTheme.Gold);
        salaries.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        salaries.TooltipText = "Total monthly wage bill for everyone on the roster.";
        head.AddChild(salaries);

        if (roster.Count == 0) return;

        AddBarRow(inner, "Stress", roster.GetAverageStress(), inverted: true);
        AddBarRow(inner, "Satisfaction", roster.GetAverageSatisfaction());
        AddBarRow(inner, "Loyalty", roster.GetAverageLoyalty());

        var note = HudStyle.MakeLabel("House averages.", 9, IsoTheme.TextMuted);
        inner.AddChild(note);
    }

    private PanelContainer MakeStaffCard(StaffMember staff)
    {
        var accent = staff.IsBound ? IsoTheme.Danger : IsoTheme.GoldDim;

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, accent.Darkened(0.35f), 10, 1, 8));

        var card = new VBoxContainer();
        card.AddThemeConstantOverride("separation", 4);
        panel.AddChild(card);

        // ── Identity ───────────────────────────────────────────────────
        var nameButton = new Button
        {
            Text = staff.StaffName,
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = string.IsNullOrWhiteSpace(staff.Backstory)
                ? "Open this person's profile."
                : staff.Backstory
        };
        HudStyle.StyleButton(nameButton, accent, 6, 13, 6);
        nameButton.Alignment = HorizontalAlignment.Left;

        var capturedId = staff.Id;
        nameButton.Pressed += () => EmitSignal(SignalName.OnStaffSelected, capturedId);
        card.AddChild(nameButton);

        var role = staff.Specialization == StaffSpecialization.None
            ? staff.Role
            : staff.Specialization.ToString();

        card.AddChild(HudStyle.MakeLabel(
            $"{role} · {OriginName(staff.Origin)}", 9, IsoTheme.TextMuted));

        // ── Flags ──────────────────────────────────────────────────────
        AddStatusFlags(card, staff);

        // ── Stats ──────────────────────────────────────────────────────
        card.AddChild(HudStyle.MakeLabel("SKILL", 9, IsoTheme.GoldDim));
        AddBarRow(card, "Charisma", staff.Charisma);
        AddBarRow(card, "Negotiation", staff.Negotiation);
        AddBarRow(card, "Discretion", staff.Discretion);

        // ── Agency ─────────────────────────────────────────────────────
        card.AddChild(HudStyle.MakeLabel("WELLBEING", 9, IsoTheme.GoldDim));
        AddBarRow(card, "Stress", staff.Stress, inverted: true);
        AddBarRow(card, "Satisfaction", staff.Satisfaction);
        AddBarRow(card, "Trauma", staff.Trauma, inverted: true);
        AddBarRow(card, "Loyalty", staff.Loyalty);

        if (staff.MaxSatisfaction < 99f)
            card.AddChild(Small(
                $"Trauma caps their satisfaction at {staff.MaxSatisfaction:F0}. " +
                "They cannot be made fully content while it stands.",
                IsoTheme.TextMuted));

        // ── Ambition ───────────────────────────────────────────────────
        AddAmbition(card, staff);

        // ── Specialize ─────────────────────────────────────────────────
        AddSpecializeControl(card, staff);

        // ── Train ──────────────────────────────────────────────────────
        AddTrainControl(card, staff);

        return panel;
    }

    private void AddStatusFlags(VBoxContainer card, StaffMember staff)
    {
        var flags = new List<string>();

        if (staff.IsBurningOut) flags.Add("BURNING OUT");
        if (staff.IsQuitRisk) flags.Add("WANTS TO LEAVE");
        if (staff.IsDefectionRisk) flags.Add("MAY DEFECT");

        if (flags.Count > 0)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            card.AddChild(row);

            foreach (var flag in flags)
                row.AddChild(MakeBadge(flag, IsoTheme.Danger));
        }

        if (!staff.IsBound) return;

        // Bound staff are the one flag that gets a sentence rather than a
        // badge. "Cannot quit" is a convenience from the ledger's point of
        // view and a cage from theirs, and the copy should side with them.
        card.AddChild(MakeBadge($"BOUND — ${staff.ContractDebt:N0} OUTSTANDING", IsoTheme.Danger));
        card.AddChild(Small(
            staff.IsQuitRisk
                ? "They want to leave and the contract will not let them. " +
                  "Nothing about that improves on its own."
                : "Held by their debt. They cannot leave, whatever they come to think of you.",
            IsoTheme.Danger));
    }

    private void AddAmbition(VBoxContainer card, StaffMember staff)
    {
        card.AddChild(HudStyle.MakeLabel("WANTS", 9, IsoTheme.GoldDim));

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        card.AddChild(head);

        var name = HudStyle.MakeLabel(AmbitionName(staff.Ambition), 11,
            staff.AmbitionFulfilled ? IsoTheme.Good : IsoTheme.TextPrimary);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);

        if (staff.AmbitionFulfilled)
            head.AddChild(MakeBadge("FULFILLED", IsoTheme.Good));

        AddBarRow(card, "Progress", staff.AmbitionProgress);

        card.AddChild(Small(
            staff.AmbitionFulfilled
                ? "They got what they came for, and they know who gave it to them."
                : AmbitionAdvice(staff),
            IsoTheme.TextMuted));
    }

    private void AddSpecializeControl(VBoxContainer card, StaffMember staff)
    {
        if (staff.Specialization != StaffSpecialization.None) return;

        var eligible = staff.GetEligibleSpecializations() ?? new List<StaffSpecialization>();
        if (eligible.Count == 0) return;

        card.AddChild(HudStyle.MakeLabel("READY TO SPECIALISE", 9, IsoTheme.GoldDim));
        card.AddChild(Small(
            "A one-time choice. Ask them what they are for, and it stays asked.",
            IsoTheme.TextMuted));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        card.AddChild(row);

        foreach (var spec in eligible)
        {
            var button = new Button
            {
                Text = spec.ToString(),
                CustomMinimumSize = new Vector2(0, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = SpecializationTooltip(spec)
            };
            HudStyle.StyleButton(button, IsoTheme.Good, 6, 10, 6);

            var capturedStaff = staff;
            var capturedSpec = spec;
            button.Pressed += () => Specialize(capturedStaff, capturedSpec);

            row.AddChild(button);
        }
    }

    private void Specialize(StaffMember staff, StaffSpecialization spec)
    {
        if (staff == null) return;

        if (staff.TryUnlockSpecialization(spec))
            SetNotice($"{staff.StaffName} now works as a {spec}.", IsoTheme.Good);

        Refresh();
    }

    private void AddTrainControl(VBoxContainer card, StaffMember staff)
    {
        card.AddChild(HudStyle.MakeLabel("TRAIN", 9, IsoTheme.GoldDim));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        card.AddChild(row);

        AddTrainButton(row, staff, "Charisma");
        AddTrainButton(row, staff, "Negotiation");
        AddTrainButton(row, staff, "Discretion");
    }

    private void AddTrainButton(HBoxContainer row, StaffMember staff, string statName)
    {
        var button = new Button
        {
            Text = statName[..3],
            CustomMinimumSize = new Vector2(0, 24),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = $"{TrainingHours:0} hours of {statName.ToLowerInvariant()} coaching. " +
                          "Gains shrink as the stat approaches 100."
        };
        HudStyle.StyleButton(button, IsoTheme.Gold, 6, 10, 6);

        var capturedStaff = staff;
        var capturedStat = statName;
        button.Pressed += () => TrainStat(capturedStaff, capturedStat);

        row.AddChild(button);
    }

    private void TrainStat(StaffMember staff, string statName)
    {
        if (staff == null) return;

        var gain = staff.Train(statName, TrainingHours);
        SetNotice($"{staff.StaffName}: {statName} +{gain:F1}.", IsoTheme.TextMuted);

        Refresh();
    }

    private void BuildRelationships(VBoxContainer parent, StaffRoster roster)
    {
        var bonds = roster.GetBondedPairs() ?? new List<(StaffMember, StaffMember)>();
        var rivalries = roster.GetRivalPairs() ?? new List<(StaffMember, StaffMember)>();

        if (bonds.Count == 0 && rivalries.Count == 0) return;

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim.Darkened(0.4f), 8, 1, 8));
        parent.AddChild(panel);

        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 2);
        panel.AddChild(inner);

        inner.AddChild(HudStyle.MakeLabel("BETWEEN THEM", 9, IsoTheme.GoldDim));

        foreach (var pair in bonds)
        {
            if (pair.Item1 == null || pair.Item2 == null) continue;
            inner.AddChild(HudStyle.MakeLabel(
                $"{pair.Item1.StaffName} and {pair.Item2.StaffName} look out for each other.",
                10, IsoTheme.Good));
        }

        foreach (var pair in rivalries)
        {
            if (pair.Item1 == null || pair.Item2 == null) continue;
            inner.AddChild(HudStyle.MakeLabel(
                $"{pair.Item1.StaffName} and {pair.Item2.StaffName} cannot stand each other.",
                10, IsoTheme.Danger));
        }

        inner.AddChild(Small(
            "Bonded pairs on the same floor take less stress; rivals take more.",
            IsoTheme.TextMuted));
    }

    // ── Hiring tab ─────────────────────────────────────────────────────

    private void BuildHiringTab(VBoxContainer parent)
    {
        if (_recruitment == null || !IsInstanceValid(_recruitment))
        {
            parent.AddChild(Paragraph(
                "No hiring system is running. Nobody is taking applications.",
                IsoTheme.TextMuted));
            return;
        }

        parent.AddChild(HudStyle.MakeLabel("WHERE PEOPLE COME FROM", 10, IsoTheme.GoldDim));
        parent.AddChild(Small(
            "Every route below costs something other than money. Read the second line.",
            IsoTheme.TextMuted));

        foreach (RecruitmentChannel channel in Enum.GetValues<RecruitmentChannel>())
            parent.AddChild(MakeChannelRow(channel));

        var candidates = _recruitment.Candidates ?? (IReadOnlyList<Candidate>)Array.Empty<Candidate>();
        var live = candidates.Where(c => c?.Staff != null).ToList();

        parent.AddChild(HudStyle.MakeLabel(
            live.Count == 0 ? "WAITING TO BE SEEN" : $"WAITING TO BE SEEN — {live.Count}",
            10, IsoTheme.GoldDim));

        if (live.Count == 0)
        {
            parent.AddChild(Paragraph(
                "Nobody is waiting. Post an open call, or go looking — and be honest " +
                "with yourself about which of those you are prepared to do.",
                IsoTheme.TextMuted));
            return;
        }

        foreach (var candidate in live)
            parent.AddChild(MakeCandidateCard(candidate));
    }

    private PanelContainer MakeChannelRow(RecruitmentChannel channel)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, ChannelAccent(channel).Darkened(0.5f), 8, 1, 8));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        text.AddThemeConstantOverride("separation", 1);
        row.AddChild(text);

        text.AddChild(HudStyle.MakeLabel(ChannelName(channel), 11, ChannelAccent(channel)));

        var blurb = Small(ChannelBlurb(channel), IsoTheme.TextMuted);
        blurb.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(blurb);

        var status = ChannelStatusLine(channel);
        if (!string.IsNullOrEmpty(status))
            text.AddChild(Small(status, ChannelStatusColour(channel)));

        var action = MakeChannelButton(channel);
        if (action != null) row.AddChild(action);

        return panel;
    }

    private Button MakeChannelButton(RecruitmentChannel channel)
    {
        if (channel == RecruitmentChannel.ReputationWalkIn) return null;

        var pending = channel == RecruitmentChannel.OpenCall && _recruitment.OpenCallPending;

        var button = new Button
        {
            Text = channel == RecruitmentChannel.OpenCall ? "Post" : "Ask",
            CustomMinimumSize = new Vector2(64, 30),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Disabled = pending
        };

        button.TooltipText = channel switch
        {
            RecruitmentChannel.OpenCall => pending
                ? "An advertisement is already out. Wait for the responses."
                : $"Place a discreet advertisement for ${_recruitment.OpenCallPostingFee:N0}.",
            RecruitmentChannel.Poaching =>
                "Put an offer in front of someone who already works for a rival.",
            RecruitmentChannel.DebtAcquisition =>
                "Buy a contract, and the person tied to it, from whoever holds the debt.",
            _ => "Get someone out of a house that is worse than yours."
        };

        HudStyle.StyleButton(button, ChannelAccent(channel), 8, 12, 8);

        var captured = channel;
        button.Pressed += () => Solicit(captured);

        return button;
    }

    private void Solicit(RecruitmentChannel channel)
    {
        if (_recruitment == null || !IsInstanceValid(_recruitment)) return;

        if (channel == RecruitmentChannel.OpenCall)
        {
            _recruitment.PostOpenCall();
            Refresh();
            return;
        }

        var candidate = _recruitment.SolicitOffer(channel);
        if (candidate?.Staff != null)
            SetNotice($"{candidate.Staff.StaffName} is willing to be seen.", IsoTheme.TextMuted);

        Refresh();
    }

    private PanelContainer MakeCandidateCard(Candidate candidate)
    {
        var staff = candidate.Staff;
        var grim = candidate.Channel == RecruitmentChannel.DebtAcquisition;
        var accent = grim ? IsoTheme.Danger : ChannelAccent(candidate.Channel);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, accent.Darkened(grim ? 0.15f : 0.4f), 10, grim ? 2 : 1, 8));

        var card = new VBoxContainer();
        card.AddThemeConstantOverride("separation", 4);
        panel.AddChild(card);

        // ── Identity and price ─────────────────────────────────────────
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        card.AddChild(head);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        head.AddChild(text);

        var name = HudStyle.MakeLabel(staff.StaffName, 13, IsoTheme.TextPrimary);
        name.ClipText = true;
        text.AddChild(name);
        text.AddChild(HudStyle.MakeLabel(
            $"{staff.Role} · {ChannelName(candidate.Channel)}", 9, accent));

        var cash = GameStateManager.Instance?.Cash;
        var affordable = !cash.HasValue || cash.Value >= candidate.HireCost;

        var price = HudStyle.MakeLabel(
            candidate.HireCost <= 0 ? "no fee" : $"${candidate.HireCost:N0}", 14,
            candidate.HireCost <= 0 ? IsoTheme.TextMuted : affordable ? IsoTheme.Gold : IsoTheme.Danger);
        price.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(price);

        // ── Stats ──────────────────────────────────────────────────────
        AddBarRow(card, "Charisma", staff.Charisma);
        AddBarRow(card, "Negotiation", staff.Negotiation);
        AddBarRow(card, "Discretion", staff.Discretion);

        // ── What they arrive carrying ──────────────────────────────────
        AddBarRow(card, "Trauma", staff.Trauma, inverted: true);
        AddBarRow(card, "Loyalty", staff.Loyalty);

        card.AddChild(Small(
            $"Wants {AmbitionName(staff.Ambition).ToLowerInvariant()} · " +
            $"${staff.Salary:N0}/mo wage", IsoTheme.TextMuted));

        if (staff.ContractDebt > 0)
            card.AddChild(Small(
                $"Carries ${staff.ContractDebt:N0} of contract debt. Hiring them makes " +
                "the house the holder of it, and they will not be able to leave.",
                IsoTheme.Danger));

        // ── The moral cost, in the service's own words ─────────────────
        if (!string.IsNullOrWhiteSpace(candidate.ConsequenceNote))
            card.AddChild(Paragraph(candidate.ConsequenceNote,
                grim ? IsoTheme.Danger : IsoTheme.Warning));

        // ── Expiry ─────────────────────────────────────────────────────
        var day = GameStateManager.Instance?.DayCount ?? 0;
        var remaining = candidate.ExpiresOnDay - day;

        card.AddChild(Small(
            remaining <= 0
                ? "Leaving tonight."
                : remaining == 1
                    ? "One night left before they take another offer."
                    : $"{remaining} nights left before they take another offer.",
            remaining <= 1 ? IsoTheme.Warning : IsoTheme.TextMuted));

        // ── Hire ───────────────────────────────────────────────────────
        card.AddChild(MakeHireButton(candidate, affordable, cash));

        return panel;
    }

    private Button MakeHireButton(Candidate candidate, bool affordable, double? cash)
    {
        var roster = Roster;
        var full = roster != null && !roster.HasVacancy;
        var noRoster = roster == null;
        var blocked = full || noRoster || !affordable;

        var button = new Button
        {
            Text = blocked
                ? noRoster ? "No roster"
                : full ? "House is full"
                : "Cannot afford"
                : candidate.HireCost <= 0 ? "Take them on" : $"Take them on — ${candidate.HireCost:N0}",
            CustomMinimumSize = new Vector2(0, 30),
            Disabled = blocked,
            TooltipText = noRoster
                ? "No roster is loaded, so nobody can be taken on."
                : full
                    ? $"Every place is filled ({roster.Count}/{roster.RosterCap}). " +
                      "Someone would have to leave first."
                    : !affordable
                        ? $"The fee is ${candidate.HireCost:N0}; the house has ${cash ?? 0:N0}."
                        : "They join the roster tonight, exactly as shown."
        };

        HudStyle.StyleButton(button,
            candidate.Channel == RecruitmentChannel.DebtAcquisition ? IsoTheme.Danger : IsoTheme.Gold,
            8, 12, 8);

        if (!blocked)
        {
            var captured = candidate;
            button.Pressed += () => HireCandidate(captured);
        }

        return button;
    }

    private void HireCandidate(Candidate candidate)
    {
        if (_recruitment == null || !IsInstanceValid(_recruitment) || candidate == null) return;

        if (!_recruitment.Hire(candidate))
        {
            // The service emits its own rejection reason; the notice handler
            // has already picked it up and refreshed.
            Refresh();
            return;
        }

        Refresh();
        EmitSignal(SignalName.OnRosterChanged);
    }

    // ── Copy ───────────────────────────────────────────────────────────

    private static string ChannelName(RecruitmentChannel channel) => channel switch
    {
        RecruitmentChannel.OpenCall => "Open call",
        RecruitmentChannel.Poaching => "Poached",
        RecruitmentChannel.DebtAcquisition => "Debt acquisition",
        RecruitmentChannel.ReputationWalkIn => "Walk-in",
        RecruitmentChannel.Rescue => "Rescue",
        _ => channel.ToString()
    };

    private static string ChannelBlurb(RecruitmentChannel channel) => channel switch
    {
        RecruitmentChannel.OpenCall =>
            "A discreet advertisement. Slow, unremarkable people who owe the house nothing.",
        RecruitmentChannel.Poaching =>
            "Buy someone away from a rival. Capable and expensive, and the rival remembers it.",
        RecruitmentChannel.DebtAcquisition =>
            "Buy a debt and the person tied to it. Cheap, badly hurt, and unable to leave. " +
            "The street will hear how you fill your rooms.",
        RecruitmentChannel.ReputationWalkIn =>
            "People present themselves once the house's name is worth something. No fee, no favour owed.",
        RecruitmentChannel.Rescue =>
            "Pull someone out of a worse house. They arrive hurt, and they do not forget who opened the door.",
        _ => ""
    };

    private static Color ChannelAccent(RecruitmentChannel channel) => channel switch
    {
        RecruitmentChannel.DebtAcquisition => IsoTheme.Danger,
        RecruitmentChannel.Poaching => IsoTheme.Warning,
        RecruitmentChannel.ReputationWalkIn => IsoTheme.Gold,
        RecruitmentChannel.Rescue => IsoTheme.Good,
        _ => IsoTheme.GoldDim
    };

    private string ChannelStatusLine(RecruitmentChannel channel)
    {
        if (_recruitment == null) return "";

        switch (channel)
        {
            case RecruitmentChannel.OpenCall:
                if (!_recruitment.OpenCallPending)
                    return $"Fee ${_recruitment.OpenCallPostingFee:N0}. " +
                           "Responses take two to four nights.";

                return _openCallArrivalDay >= 0
                    ? $"Posted. Responses expected on night {_openCallArrivalDay}."
                    : "Posted. Responses expected within a few nights.";

            case RecruitmentChannel.ReputationWalkIn:
                var reputation = GameStateManager.Instance?.Reputation;
                var threshold = _recruitment.WalkInReputationThreshold;

                if (!reputation.HasValue)
                    return $"Needs Reputation {threshold:F0}. Cannot be solicited.";

                return reputation.Value >= threshold
                    ? $"Reputation {reputation.Value:F0} of {threshold:F0} — met. " +
                      "People may present themselves any night."
                    : $"Reputation {reputation.Value:F0} of {threshold:F0} — not yet. " +
                      "Cannot be solicited; the house has to earn it.";

            default:
                return "";
        }
    }

    private Color ChannelStatusColour(RecruitmentChannel channel)
    {
        if (channel != RecruitmentChannel.ReputationWalkIn) return IsoTheme.TextMuted;

        var reputation = GameStateManager.Instance?.Reputation;
        if (_recruitment == null || !reputation.HasValue) return IsoTheme.TextMuted;

        return reputation.Value >= _recruitment.WalkInReputationThreshold
            ? IsoTheme.Good
            : IsoTheme.TextMuted;
    }

    private static string OriginName(StaffOrigin origin) => origin switch
    {
        StaffOrigin.OpenCall => "answered an advertisement",
        StaffOrigin.Poached => "taken from a rival house",
        StaffOrigin.Debt => "acquired with their debt",
        StaffOrigin.WalkIn => "came to you",
        StaffOrigin.Rescue => "pulled out of a worse house",
        _ => origin.ToString()
    };

    private static string AmbitionName(StaffAmbition ambition) => ambition switch
    {
        StaffAmbition.Money => "Money",
        StaffAmbition.Freedom => "Freedom",
        StaffAmbition.Status => "Status",
        StaffAmbition.Revenge => "Revenge",
        _ => ambition.ToString()
    };

    /// <summary>One plain sentence on what would actually move this ambition.</summary>
    private static string AmbitionAdvice(StaffMember staff) => staff.Ambition switch
    {
        StaffAmbition.Money => "Pay them well — bonuses and the clients who tip.",
        StaffAmbition.Freedom => staff.IsBound
            ? $"Pay down their contract. ${staff.ContractDebt:N0} still stands between them and the door."
            : "Pay down what they owe. They want the door unlocked, not the room improved.",
        StaffAmbition.Status => "Give them the VIP work, and pay for the training.",
        StaffAmbition.Revenge => string.IsNullOrWhiteSpace(staff.AssociatedFaction)
            ? "Move against the house that hurt them."
            : $"Move against {staff.AssociatedFaction}.",
        _ => ""
    };

    private static string SpecializationTooltip(StaffSpecialization spec) => spec switch
    {
        StaffSpecialization.Hostess => "Hostess — raises satisfaction for every client in her room.",
        StaffSpecialization.Negotiator => "Negotiator — better haggling, and two negotiations a night.",
        StaffSpecialization.Fixer => "Fixer — cuts the Heat generated by their floor.",
        StaffSpecialization.Informant => "Informant — gathers client intel for the blackmail network.",
        StaffSpecialization.Matron => "Matron — mentors the roster; slows stress house-wide.",
        _ => ""
    };

    // ── Widget helpers ─────────────────────────────────────────────────

    /// <summary>
    /// A labelled bar. <paramref name="inverted"/> flips the colour ramp for
    /// Stress and Trauma, where a high reading is the bad one — colouring
    /// those forwards would paint a person in crisis green.
    /// </summary>
    private static void AddBarRow(VBoxContainer parent, string label, float value, bool inverted = false)
    {
        var clamped = Mathf.Clamp(value, 0f, 100f);
        var colour = IsoTheme.GetScoreColor(inverted ? 100f - clamped : clamped);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        parent.AddChild(row);

        var name = HudStyle.MakeLabel(label, 10, IsoTheme.TextMuted);
        name.CustomMinimumSize = new Vector2(78, 0);
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(name);

        var bar = MakeBar(8);
        bar.Value = clamped;

        var radius = Mathf.Max(2, (int)bar.CustomMinimumSize.Y / 2);
        bar.AddThemeStyleboxOverride("fill", HudStyle.Box(colour, colour, radius, 0, 0));
        row.AddChild(bar);

        var readout = HudStyle.MakeLabel($"{clamped:F0}", 10, colour);
        readout.CustomMinimumSize = new Vector2(28, 0);
        readout.HorizontalAlignment = HorizontalAlignment.Right;
        readout.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(readout);
    }

    private static ProgressBar MakeBar(int height)
    {
        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, height),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };

        var radius = Mathf.Max(2, height / 2);
        var track = IsoTheme.Backdrop;

        bar.AddThemeStyleboxOverride("background", HudStyle.Box(track, track, radius, 0, 0));
        bar.AddThemeStyleboxOverride("fill",
            HudStyle.Box(IsoTheme.GoldDim, IsoTheme.GoldDim, radius, 0, 0));

        return bar;
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
    /// end of the frame, and rebuilt rows would otherwise stack below stale ones.
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

    public override string ToString()
    {
        var roster = Roster;
        var candidates = _recruitment?.Candidates?.Count ?? 0;

        return $"[StaffPanel] {_tab} — " +
               $"{(roster == null ? "no roster" : $"{roster.Count}/{roster.RosterCap}")}, " +
               $"{candidates} candidate{(candidates == 1 ? "" : "s")}";
    }
}
