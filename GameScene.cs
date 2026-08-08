using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The playable scene root: constructs the world, the view layers and the
/// HUD, and wires them to each other.
///
/// Layering, back to front:
///   VenueView3D (Node3D)  — owns the orthographic camera, lights and rooms
///     └ VenuePawns3D      — people, positioned through VenueSpace
///     └ EncounterCloud3D  — puffs above rooms, likewise
///   GameHud (CanvasLayer)
///   NightLedgerScreen (CanvasLayer, above the HUD)
///
/// The pawn and cloud layers are children of the venue view so they share
/// its world space; all three place things through <see cref="VenueSpace"/>,
/// which is the single definition of where a grid cell sits in the world.
/// </summary>
public partial class GameScene : Node
{
    [Export] public bool AutoBeginFirstNight { get; set; } = true;

    /// <summary>Floor the camera opens on. Ground by default.</summary>
    [Export] public int InitialFloor { get; set; }

    /// <summary>
    /// Open with the five-room house already built instead of just the
    /// entrance.
    ///
    /// A new campaign starts with a reception and a bar and nothing else —
    /// the player builds every suite. Capture scenes that need to photograph
    /// furnished rooms set this; `main.tscn` does not.
    /// </summary>
    [Export] public bool SeedEstablishedHouse { get; set; }

    /// <summary>
    /// Capture runs only: open the decorate panel on a suite instead of
    /// opening the doors, so the shot shows the furniture shop.
    /// </summary>
    [Export] public bool CaptureDecoratePanel { get; set; }

    /// <summary>
    /// Capture runs only: play a compressed night through to the Ledger so
    /// the shot shows the end-of-night summary.
    /// </summary>
    [Export] public bool CaptureLedger { get; set; }

    /// <summary>Capture runs only: open the staff panel on the given tab.</summary>
    [Export] public bool CaptureStaffPanel { get; set; }

    /// <summary>Which staff tab a capture run opens. 0 = Roster, 1 = Hiring.</summary>
    [Export] public int CaptureStaffTab { get; set; }

    /// <summary>Capture runs only: open the influence panel.</summary>
    [Export] public bool CaptureInfluencePanel { get; set; }

    /// <summary>Capture runs only: open the Panderer's Code panel.</summary>
    [Export] public bool CapturePolicyPanel { get; set; }

    /// <summary>Capture runs only: open the licences panel.</summary>
    [Export] public bool CaptureLicencesPanel { get; set; }

    /// <summary>Capture runs only: open the labour panel.</summary>
    [Export] public bool CaptureUnionPanel { get; set; }

    /// <summary>
    /// Capture runs only: force a strike before opening the labour panel, so
    /// the shot shows the three resolutions rather than a calm risk meter.
    /// A strike takes weeks of bad conditions to arrive on its own.
    /// </summary>
    [Export] public bool CaptureUnionStrike { get; set; }

    /// <summary>
    /// Capture runs only: play this many compressed nights, then open the
    /// patrons book. Clients need repeat visits before they are worth
    /// showing, so photographing the book on night one shows nothing.
    /// </summary>
    [Export] public int CapturePatronsAfterNights { get; set; }

    /// <summary>
    /// Capture runs only: override the orthographic camera size, in metres of
    /// world height. Zero keeps the automatic fit. Small values zoom in for
    /// inspecting furniture and room detail.
    /// </summary>
    [Export] public float CaptureCameraSize { get; set; }

    /// <summary>
    /// Capture runs only: put the city into a police crackdown, so the shot
    /// shows the adverse city chip. The organic route waits 30–90 days.
    /// </summary>
    [Export] public bool CaptureCrackdown { get; set; }

    /// <summary>
    /// Capture runs only: raise this crisis and photograph the decision.
    /// Empty for none. The organic triggers need heat over 85 or the house
    /// several hundred dollars in debt.
    /// </summary>
    [Export] public string CaptureCrisis { get; set; } = "";

    /// <summary>Capture runs only: open the Info shortcut card.</summary>
    [Export] public bool CaptureHelp { get; set; }

    /// <summary>Show the four intro cards on a new campaign.</summary>
    [Export] public bool ShowIntroduction { get; set; } = true;

    /// <summary>Capture runs only: photograph the intro instead of the game.</summary>
    [Export] public bool CaptureIntro { get; set; }

    /// <summary>Seconds before an automatic screenshot. Zero disables it.</summary>
    [Export] public float ScreenshotAfterSeconds { get; set; }

    private GameBootstrap _boot;
    private VenueView3D _view;
    private VenuePawns3D _pawns;
    private EncounterCloud3D _clouds;
    private GameHud _hud;
    private NightLedgerScreen _ledger;
    private DecoratePanel _decorate;
    private StaffPanel _staff;
    private InfluencePanel _influence;
    private PolicyPanel _policy;
    private PatronsPanel _patrons;
    private LicencesPanel _licences;
    private UnionPanel _union;
    private CrisisScreen _crisis;
    private NegotiationScreen _negotiation;
    private IntroScreen _intro;
    private ScreenshotCapture _screenshot;

    /// <summary>Maps an in-flight encounter to the staff pawn working it.</summary>
    private readonly Dictionary<string, Vector3I> _encounterCells = new();

    /// <summary>Lobby clients currently drawn, by pawn id.</summary>
    private readonly List<string> _lobbyPawns = new();

    /// <summary>Client pawn accompanying each in-flight encounter, by staff id.</summary>
    private readonly Dictionary<string, string> _encounterClients = new();

    private int _lobbyCounter;

    public override void _Ready()
    {
        BuildWorld();
        BuildViewLayers();
        BuildInterface();

        ConnectSignals();

        // Deferred so GameBootstrap's own deferred seeding has run and there
        // is actually a venue to look at.
        CallDeferred(nameof(OnWorldSettled));
    }

    // ── Construction ───────────────────────────────────────────────────

    private void BuildWorld()
    {
        _boot = new GameBootstrap
        {
            Name = "GameBootstrap",
            UseLegacyDayTimer = false,
            SeedEstablishedHouse = SeedEstablishedHouse
        };

        AddChild(_boot);
    }

    private void BuildViewLayers()
    {
        _view = new VenueView3D { Name = "VenueView" };
        AddChild(_view);

        _pawns = new VenuePawns3D { Name = "PawnLayer" };
        _view.AddChild(_pawns);

        _clouds = new EncounterCloud3D { Name = "CloudVfx" };
        _view.AddChild(_clouds);
    }

    private void BuildInterface()
    {
        _hud = new GameHud { Name = "GameHud" };
        AddChild(_hud);

        _ledger = new NightLedgerScreen { Name = "NightLedger" };
        AddChild(_ledger);

        // Above the ledger, because a crisis is what the player deals with
        // before they are allowed to move on to the next night.
        _crisis = new CrisisScreen { Name = "CrisisScreen" };
        AddChild(_crisis);

        // Above the crisis screen: a client is standing at the door while
        // this is up, so it is the most immediate thing on screen.
        _negotiation = new NegotiationScreen { Name = "NegotiationScreen" };
        AddChild(_negotiation);

        // Above everything: it is the first thing a new campaign sees.
        _intro = new IntroScreen { Name = "IntroScreen" };
        AddChild(_intro);

        // Side panels are Controls, so they need a CanvasLayer to sit above
        // the 3D viewport.
        var panelLayer = new CanvasLayer { Name = "PanelLayer", Layer = 20 };
        AddChild(panelLayer);

        // Both panels anchor themselves to fill their parent in _Ready, which
        // overrides any offsets set here. So each gets a sized host Control
        // that defines the left column, and fills that instead of the screen.
        _decorate = new DecoratePanel { Name = "DecoratePanel" };
        panelLayer.AddChild(MakeSidePanelHost("DecorateHost", DecoratePanelWidth, _decorate));

        // The staff panel shares that column; only one is ever open, since
        // both want the same real estate.
        _staff = new StaffPanel { Name = "StaffPanel" };
        panelLayer.AddChild(MakeSidePanelHost("StaffHost", StaffPanelWidth, _staff));

        _influence = new InfluencePanel { Name = "InfluencePanel" };
        panelLayer.AddChild(MakeSidePanelHost("InfluenceHost", InfluencePanelWidth, _influence));

        _policy = new PolicyPanel { Name = "PolicyPanel" };
        panelLayer.AddChild(MakeSidePanelHost("PolicyHost", PolicyPanel.PanelWidth, _policy));

        _patrons = new PatronsPanel { Name = "PatronsPanel" };
        panelLayer.AddChild(MakeSidePanelHost("PatronsHost", PatronsPanel.PanelWidth, _patrons));

        _licences = new LicencesPanel { Name = "LicencesPanel" };
        panelLayer.AddChild(MakeSidePanelHost("LicencesHost", LicencesPanel.PanelWidth, _licences));

        _union = new UnionPanel { Name = "UnionPanel" };
        panelLayer.AddChild(MakeSidePanelHost("UnionHost", UnionPanel.PanelWidth, _union));

        panelLayer.AddChild(BuildHelpCard());

        _screenshot = new ScreenshotCapture
        {
            Name = "ScreenshotCapture",
            AutoCaptureAfterSeconds = ScreenshotAfterSeconds
        };

        AddChild(_screenshot);
    }

    /// <summary>
    /// The shortcut list behind the Info button.
    ///
    /// Built from <see cref="GameHud.PanelKey"/> rather than typed out, so it
    /// is impossible for it to list a key that does not work — the same table
    /// drives the tooltips and the handler.
    /// </summary>
    private Control BuildHelpCard()
    {
        var host = new Control
        {
            Name = "HelpHost",
            AnchorLeft = 0f,
            AnchorTop = 1f,
            AnchorRight = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 18f,
            // Sized to clear the floor selector above it, which sits at a
            // fixed offset from the same bottom edge.
            OffsetTop = -(HudBottomHeight + 250f),
            OffsetRight = 18f + 260f,
            OffsetBottom = -(HudBottomHeight + 16f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(12, 12));
        host.AddChild(frame);
        frame.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        frame.AddChild(column);

        column.AddChild(HudStyle.MakeLabel("KEYS", 12, IsoTheme.Gold));
        column.AddChild(new HSeparator());

        foreach (HudPanel panel in Enum.GetValues<HudPanel>())
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            column.AddChild(row);

            var key = HudStyle.MakeLabel(
                GameHud.PanelKey(panel).ToString(), 12, IsoTheme.Gold);
            key.CustomMinimumSize = new Vector2(20f, 0f);
            row.AddChild(key);

            row.AddChild(HudStyle.MakeLabel(
                GameHud.PanelLabel(panel), 12, IsoTheme.TextPrimary));
        }

        column.AddChild(new HSeparator());
        column.AddChild(HudStyle.MakeLabel(
            "Click a room to furnish it.", 11, IsoTheme.TextMuted));
        column.AddChild(HudStyle.MakeLabel(
            "Esc closes any panel.", 11, IsoTheme.TextMuted));

        _help = host;
        host.Visible = false;
        return host;
    }

    private Control _help;

    private const float DecoratePanelWidth = 360f;
    private const float StaffPanelWidth = 400f;
    private const float InfluencePanelWidth = 380f;

    /// <summary>
    /// Wrap a side panel in a host Control that defines the left column.
    ///
    /// The panels anchor themselves to fill their parent during _Ready, so
    /// setting offsets on the panel directly is silently overridden and it
    /// covers the whole screen. Giving each one a correctly-sized parent to
    /// fill is the fix; the host ignores mouse input itself so it never eats
    /// clicks meant for the dollhouse behind it.
    /// </summary>
    private Control MakeSidePanelHost(string hostName, float width, Control panel)
    {
        var host = new Control
        {
            Name = hostName,
            AnchorTop = 0f,
            AnchorBottom = 1f,
            OffsetLeft = 16f,
            OffsetTop = HudTopBarHeight + 12f,
            OffsetRight = 16f + width,
            OffsetBottom = -(HudBottomHeight + 12f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        panel.Visible = false;
        host.AddChild(panel);

        return host;
    }

    // ── Wiring ─────────────────────────────────────────────────────────

    private void ConnectSignals()
    {
        _boot.OnWorldReady += OnWorldReady;

        // HUD → world
        _hud.OnFloorUpRequested += () => _view.FocusUp();
        _hud.OnFloorDownRequested += () => _view.FocusDown();
        _hud.OnBuildRoomRequested += OnBuildRoomRequested;
        _hud.OnBuyFloorRequested += OnBuyFloorRequested;
        _hud.OnSpeedRequested += OnSpeedRequested;
        _hud.OnPauseToggled += OnPauseToggled;

        // View → HUD, so the floor label follows the view rather than the
        // button that happened to change it.
        _view.OnFocusedFloorChanged += OnFocusedFloorChanged;
        _view.OnRoomClicked += OnRoomClicked;

        _ledger.OnContinuePressed += OnLedgerContinue;

        _crisis.OnChoiceTaken += OnCrisisChoiceTaken;
        _crisis.OnDismissed += OnCrisisDismissed;

        _intro.OnDismissed += RefreshNextStep;

        _negotiation.OnPriceAgreed += price => _boot?.Night?.ResolveNegotiation(price);
        _negotiation.OnClientRefused += () => _boot?.Night?.DeclineClient();

        _decorate.OnRoomChanged += OnDecorateRoomChanged;
        _decorate.OnCloseRequested += () => _decorate.Visible = false;

        _staff.OnCloseRequested += () => _staff.Visible = false;
        _staff.OnRosterChanged += OnRosterChanged;

        _influence.OnCloseRequested += () => _influence.Visible = false;
        _influence.OnAllocationChanged += (_, _) => _hud?.RefreshAll();

        _policy.OnCloseRequested += () => _policy.Visible = false;
        _policy.OnPolicyEnacted += OnPolicyEnacted;

        _patrons.OnCloseRequested += () => _patrons.Visible = false;

        _licences.OnCloseRequested += () => _licences.Visible = false;
        _licences.OnLicenceApplied += _ => _hud?.RefreshAll();

        _union.OnCloseRequested += () => _union.Visible = false;
        _union.OnDisputeResolved += OnDisputeResolved;

        // The HUD's roster controls open the staff panel.
        _hud.OnStaffSelected += _ => ToggleStaffPanel(true);
        _hud.OnPanelRequested += panel => TogglePanel((HudPanel)panel);
        _hud.OnToolRequested += tool => OnToolPressed((HudTool)tool);
    }

    /// <summary>
    /// Surface a refused action on the HUD's alert chip, so the player is
    /// told why something did not happen instead of it silently failing.
    /// </summary>
    private void OnActionRejected(string reason)
    {
        _hud?.SetStatusChip(HudTool.Alert, reason, true);
        GD.Print($"[GameScene] Refused: {reason}");
    }

    /// <summary>A hire or departure changes who is posted, so redraw the pawns.</summary>
    private void OnRosterChanged()
    {
        RefreshStaffPawns();
        _hud?.RefreshAll();
        RefreshNextStep();
    }

    /// <summary>
    /// Show or hide the staff panel. The two left-hand panels are mutually
    /// exclusive because they occupy the same column.
    /// </summary>
    private void ToggleStaffPanel(bool show) => ShowOnly(show ? _staff : null);

    private void ToggleInfluencePanel(bool show) => ShowOnly(show ? _influence : null);

    private void TogglePolicyPanel(bool show) => ShowOnly(show ? _policy : null);

    private void TogglePatronsPanel(bool show) => ShowOnly(show ? _patrons : null);

    private void ToggleLicencesPanel(bool show) => ShowOnly(show ? _licences : null);

    private void ToggleUnionPanel(bool show) => ShowOnly(show ? _union : null);

    /// <summary>
    /// The single route to a side panel. The top-bar buttons and the keyboard
    /// shortcuts both come through here, so the two can never disagree about
    /// what opens or how it toggles.
    /// </summary>
    private void TogglePanel(HudPanel panel)
    {
        var target = PanelFor(panel);
        ShowOnly(target.Visible ? null : target);
    }

    private Control PanelFor(HudPanel panel) => panel switch
    {
        HudPanel.Staff => _staff,
        HudPanel.Patrons => _patrons,
        HudPanel.Policy => _policy,
        HudPanel.Influence => _influence,
        HudPanel.Licences => _licences,
        HudPanel.Union => _union,
        _ => null
    };

    /// <summary>
    /// A strike is the one thing in the game that acts on the player without
    /// being asked, so it opens its own panel. Finding out a third of the
    /// house has walked out only because you happened to press U is not a
    /// notification.
    /// </summary>
    private void OnStrikeTriggered(float risk, int strikingStaffCount)
    {
        _hud?.SetStatusChip(HudTool.Alert,
            $"Strike — {strikingStaffCount} out", true);

        ToggleUnionPanel(true);
        GD.Print($"[GameScene] Strike at {risk:F0}% risk: {strikingStaffCount} staff out.");
    }

    /// <summary>
    /// A settled strike moves salaries, satisfaction, heat and sentiment at
    /// once, so refresh everything that reads any of them.
    /// </summary>
    private void OnDisputeResolved(string method)
    {
        _hud?.RefreshAll();
        _staff?.Refresh();
        GD.Print($"[GameScene] Labour dispute resolved by {method}.");
    }

    /// <summary>
    /// A signed policy changes standing modifiers across the whole house, so
    /// refresh everything that reads them.
    /// </summary>
    private void OnPolicyEnacted(string policyKey)
    {
        _hud?.RefreshAll();
        _staff?.Refresh();
        GD.Print($"[GameScene] Policy enacted: {policyKey}");
    }

    /// <summary>
    /// Show one left-column panel and hide the rest. They all occupy the same
    /// space, so opening one has to close the others.
    /// </summary>
    private void ShowOnly(Control panel)
    {
        _decorate.Visible = panel == _decorate;
        _staff.Visible = panel == _staff;
        _influence.Visible = panel == _influence;
        _policy.Visible = panel == _policy;
        _patrons.Visible = panel == _patrons;
        _licences.Visible = panel == _licences;
        _union.Visible = panel == _union;

        if (panel == _staff) _staff.Refresh();
        else if (panel == _influence) _influence.Refresh();
        else if (panel == _policy) _policy.Refresh();
        else if (panel == _patrons) _patrons.Refresh();
        else if (panel == _licences) _licences.Refresh();
        else if (panel == _union) _union.Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (key.Keycode == Key.Escape)
        {
            ShowOnly(null);
            return;
        }

        // Shortcuts for the panels the top bar also opens. The rail is the
        // discoverable route; these stay for anyone who has learned them, and
        // the tooltips name them so they can be learned at all.
        foreach (HudPanel panel in Enum.GetValues<HudPanel>())
        {
            if (GameHud.PanelKey(panel) != key.Keycode) continue;

            TogglePanel(panel);
            return;
        }
    }

    // ── Learning the game ──────────────────────────────────────────────

    /// <summary>
    /// Recompute the "next step" prompt from the world.
    ///
    /// Cheap enough to call after anything the player does, and derived
    /// rather than scripted, so it can never tell them to undo something they
    /// sensibly did out of order.
    /// </summary>
    private void RefreshNextStep()
    {
        if (_hud == null || _boot?.Venue == null || _boot.Night == null) return;

        var step = Onboarding.Next(_boot.Venue, _boot.Night);

        _hud.SetNextStep(Onboarding.Caption(step), Onboarding.Detail(step));
    }

    // ── At the door ────────────────────────────────────────────────────

    /// <summary>
    /// A client worth haggling over has arrived. The night is parked until
    /// the screen answers, so this must always end in either
    /// <c>ResolveNegotiation</c> or <c>DeclineClient</c>.
    /// </summary>
    private void OnNegotiationRequested(
        string clientName, double asking, double fairPrice, double budget, string staffName)
    {
        var night = _boot?.Night;
        var client = night?.NegotiationClient;

        if (client == null)
        {
            night?.DeclineClient();
            return;
        }

        var staff = StaffRoster.Instance?.GetAll()
            .FirstOrDefault(s => s.StaffName == staffName);

        var room = _boot?.Venue?.Rooms.Values
            .FirstOrDefault(r => _boot.Venue.IsRevenueGenerating(r));

        // A screen that refuses to open would strand the night, so falling
        // back to the asking price is the safe failure rather than silence.
        if (!_negotiation.Show(client, staff, room, asking))
            night.ResolveNegotiation(asking);
    }

    // ── The three tools ────────────────────────────────────────────────

    /// <summary>
    /// The quick-action cluster, which emitted <c>OnToolRequested</c> into an
    /// empty room since it was built — three round buttons and three top-bar
    /// chips that did nothing when pressed.
    ///
    /// None of them gets a new system. Each one takes the player to something
    /// that already exists, which is what a quick action should do.
    /// </summary>
    private void OnToolPressed(HudTool tool)
    {
        switch (tool)
        {
            case HudTool.Cleaning:
                ShowWorstRoom();
                break;

            case HudTool.Alert:
                ShowWhatIsWrong();
                break;

            case HudTool.Info:
                _help.Visible = !_help.Visible;
                break;
        }
    }

    /// <summary>
    /// Open the decorate panel on whichever room is in the worst condition.
    /// Furniture wears where it is used, so the room that needs attention is
    /// rarely the one the player was last looking at.
    /// </summary>
    private void ShowWorstRoom()
    {
        var venue = _boot?.Venue;
        if (venue == null) return;

        var worst = venue.Rooms.Values
            .Where(venue.IsRevenueGenerating)
            .OrderBy(r => r.AppointmentScore)
            .FirstOrDefault();

        if (worst == null)
        {
            _hud?.SetStatusChip(HudTool.Cleaning, "Nothing to tend", false);
            return;
        }

        _view.FocusedFloor = worst.GridPosition.Z;
        OnRoomClicked(worst.GridPosition.X, worst.GridPosition.Y, worst.GridPosition.Z);
    }

    /// <summary>
    /// Take the player to whatever raised the alert, in the order that
    /// matters: an unanswered crisis, then a strike, then the city.
    /// </summary>
    private void ShowWhatIsWrong()
    {
        if (ShowPendingCrisis()) return;

        if (_boot?.Union?.StrikeActive == true)
        {
            ToggleUnionPanel(true);
            return;
        }

        if (_boot?.Macro?.IsAdverse == true)
        {
            RefreshCityChip();
            _hud?.SetStatusChip(HudTool.Alert, _boot.Macro.GetShortCaption(), true);
            return;
        }

        _hud?.SetStatusChip(HudTool.Alert, "Nothing pressing", false);
    }

    // ── The city ───────────────────────────────────────────────────────

    private void RefreshCityChip()
    {
        var macro = _boot?.Macro;
        if (macro == null || _hud == null) return;

        _hud.SetCityPhase(macro.GetShortCaption(), macro.GetPlainSummary(), macro.IsAdverse);
    }

    private void OnCityPhaseChanged(int oldPhase, int newPhase, string phaseName)
    {
        RefreshCityChip();

        // Worth an alert as well as a chip: this is the moment the house's
        // takings change by up to two thirds, through no decision of the
        // player's. The alert states the consequence rather than repeating
        // the chip's caption — two chips saying the same words is noise.
        var macro = _boot?.Macro;
        if (macro?.IsAdverse == true)
        {
            var drop = Mathf.RoundToInt((1f - macro.FootfallMultiplier) * 100f);

            _hud?.SetStatusChip(HudTool.Alert,
                drop > 0 ? $"Custom down {drop}%" : "Police watching", true);
        }

        GD.Print($"[GameScene] The city turned: {phaseName}.");
    }

    /// <summary>
    /// The engine warns at 80% through a phase. Nothing listened, so the
    /// countdown that exists specifically to let a player prepare had never
    /// reached one.
    /// </summary>
    private void OnCityPhaseWarning(int daysRemaining)
    {
        RefreshCityChip();

        _hud?.SetStatusChip(HudTool.Info,
            $"City turns in ~{daysRemaining}d", true);

        GD.Print($"[GameScene] City conditions change in about {daysRemaining} days.");
    }

    /// <summary>Repaint the dollhouse after a purchase or sale changes a room.</summary>
    private void OnDecorateRoomChanged(int x, int y, int floor)
    {
        _view.Refresh();
        _hud?.RefreshAll();
        RefreshNextStep();
    }

    private void OnWorldReady()
    {
        _view.Bind(_boot.Venue);
        _pawns.Bind(_boot.Venue);
        _clouds.Bind(_boot.Venue);
        _hud.Bind(_boot.Night, _boot.Venue);

        // Refusals carry the reason — "Commissioner Ashford is at 25 favour
        // and wants 60" — which is worthless if it only reaches the console.
        _boot.Venue.OnBuildRejected += OnActionRejected;
        _boot.Catalog.OnPurchaseRejected += OnActionRejected;
        _boot.Recruitment.OnHireRejected += OnActionRejected;
        _decorate.Bind(_boot.Catalog, _boot.Venue);
        _staff.Bind(_boot.Recruitment);

        _influence.Bind(
            GetTree()?.Root?.FindChild("PoliticalInfluenceSystem", true, false)
                as PoliticalInfluenceSystem,
            _boot.Venue);

        _policy.Bind(_boot.Policies);
        _patrons.Bind(_boot.Regulars);
        _licences.Bind(_boot.Licences);
        _union.Bind(_boot.Union);

        // Both macro signals had no listeners, so the largest single force on
        // revenue in the game was invisible: a crackdown quarters the footfall
        // for a month and nothing said so.
        if (_boot.Macro != null)
        {
            _boot.Macro.OnPhaseChanged += OnCityPhaseChanged;
            _boot.Macro.OnPhaseWarning += OnCityPhaseWarning;
            if (CaptureCrackdown) _boot.Macro.ForcePhase(MacroPhase.PoliceCrackdown);

            RefreshCityChip();
        }

        // The strike fired into an empty room before this — the signal had no
        // listeners anywhere, so the house could go on strike in silence.
        if (_boot.Union != null) _boot.Union.OnStrikeTriggered += OnStrikeTriggered;

        var night = _boot.Night;
        night.OnEncounterStarted += OnEncounterStarted;
        night.OnEncounterResolved += OnEncounterResolved;
        night.OnNightConcluded += OnNightConcluded;
        night.OnClientArrived += OnClientArrived;
        night.OnPhaseChanged += OnPhaseChanged;
        night.OnNegotiationRequested += OnNegotiationRequested;
        night.NegotiationUiPresent = true;

        _negotiation.Bind(_boot.Negotiation);

        _view.FocusedFloor = InitialFloor;
        FitCameraToBuilding();

        RefreshStaffPawns();
    }

    /// <summary>
    /// Frame the whole building.
    ///
    /// In 2D this had to derive a zoom from the drawn pixel bounds against
    /// the free screen area. An orthographic camera makes that the view's own
    /// business — it knows the world extent and its own Size — so this is now
    /// a passthrough, kept as a seam for HUD-aware framing later.
    /// </summary>
    private void FitCameraToBuilding()
    {
        _view?.CenterOnBuilding();

        if (CaptureCameraSize > 0f) _view?.SetCameraSize(CaptureCameraSize);
    }

    private const float HudTopBarHeight = 58f;
    private const float HudBottomHeight = 130f;

    private void OnWorldSettled()
    {
        if (AutoBeginFirstNight && _boot?.Night?.Phase == NightPhase.Idle)
        {
            _boot.Night.BeginNight();
            _boot.AutoAssignStaff();
            RefreshStaffPawns();
        }

        RefreshNextStep();

        // A new campaign is introduced; an established house is not, because
        // it is either a capture run or a player who has already seen this.
        // Never during a screenshot run either — a modal over every shot
        // would be its own kind of bug.
        if (ShowIntroduction && !SeedEstablishedHouse && ScreenshotAfterSeconds <= 0f)
            _intro.ShowIntro();

        if (ScreenshotAfterSeconds <= 0f || _boot?.Night == null) return;

        if (CaptureDecoratePanel)
        {
            // Decorating is a Preparation-time activity, so leave the doors
            // shut and photograph the panel over a furnished suite.
            var suite = _boot.Venue?.GetRoomsOnFloor(1)
                ?.FirstOrDefault(r => _boot.Venue.IsRevenueGenerating(r));

            if (suite != null) OnRoomClicked(
                suite.GridPosition.X, suite.GridPosition.Y, suite.GridPosition.Z);

            return;
        }

        if (CaptureInfluencePanel)
        {
            ToggleInfluencePanel(true);
            return;
        }

        if (CapturePolicyPanel)
        {
            TogglePolicyPanel(true);
            return;
        }

        if (CaptureLicencesPanel)
        {
            ToggleLicencesPanel(true);
            return;
        }

        if (!string.IsNullOrEmpty(CaptureCrisis) &&
            System.Enum.TryParse<CrisisTrigger>(CaptureCrisis, true, out var forced))
        {
            _boot.Crises?.ForceCrisis(forced);
            ShowPendingCrisis();
            return;
        }

        if (CaptureIntro)
        {
            _intro.ShowIntro();
            return;
        }

        if (CaptureHelp)
        {
            _help.Visible = true;
            return;
        }

        if (CaptureUnionPanel)
        {
            if (CaptureUnionStrike) _boot.Union?.ForceStrike();

            ToggleUnionPanel(true);
            return;
        }

        if (CaptureStaffPanel)
        {
            ToggleStaffPanel(true);
            _staff.ShowTab((StaffPanelTab)Mathf.Clamp(CaptureStaffTab, 0, 1));
            return;
        }

        if (CapturePatronsAfterNights > 0)
        {
            // Compress hard and let the night loop run itself, so the book
            // has real repeat clients in it by the time the shutter opens.
            _boot.Night.ServiceDurationSeconds = 0.6f;
            _boot.Night.EncounterDurationSeconds = 0.05f;

            _autoPlayNightsLeft = CapturePatronsAfterNights;
            _boot.Night.OpenDoors();
            return;
        }

        if (CaptureLedger)
        {
            // Compress hard so the whole night resolves well before the
            // shutter, leaving the Ledger on screen.
            _boot.Night.ServiceDurationSeconds = 2f;
            _boot.Night.EncounterDurationSeconds = 0.15f;
            _boot.Night.OpenDoors();
            return;
        }

        // Otherwise photograph the game working rather than the paused
        // Preparation screen: compress the night and open the doors so the
        // shot catches live clients, pawns and encounter clouds.
        _boot.Night.ServiceDurationSeconds = ScreenshotAfterSeconds * 3f;
        _boot.Night.EncounterDurationSeconds = 4f;
        _boot.Night.OpenDoors();
    }

    // ── HUD handlers ───────────────────────────────────────────────────

    private void OnBuildRoomRequested(int roomType)
    {
        var venue = _boot?.Venue;
        if (venue == null) return;

        var type = (RoomType)roomType;
        var floor = _view.FocusedFloor;

        // Place into the first empty tile that can actually take the room's
        // footprint, rather than failing silently on the first candidate.
        foreach (var tile in venue.GetEmptyTiles(floor))
        {
            var candidate = VenueBuilding.CreateRoom(type, tile);
            if (!venue.CanPlaceRoom(candidate, out _)) continue;

            if (venue.BuildRoom(type, tile) != null)
            {
                _view.Refresh();
                RefreshNextStep();
                return;
            }
        }

        GD.Print($"[GameScene] No room for a {type} on floor {floor}.");
    }

    private void OnBuyFloorRequested(int floor)
    {
        if (_boot?.Venue?.BuyFloor(floor) == true)
        {
            _view.Refresh();
            _view.FocusedFloor = floor;
        }
    }

    private void OnSpeedRequested(float scale) => _boot?.Loop?.SetTimeScale(scale);

    private void OnPauseToggled() => _boot?.Loop?.TogglePause();

    private void OnFocusedFloorChanged(int floor)
    {
        _pawns.FocusedFloor = floor;
        _hud.SetCurrentFloor(floor);
    }

    private void OnRoomClicked(int x, int y, int floor)
    {
        var tile = new Vector3I(x, y, floor);
        var room = _boot?.Venue?.GetRoom(tile);
        if (room == null) return;

        // Clicking a room opens it for decoration. This is the only route to
        // the furniture shop, and therefore the only way a player reaches the
        // style-coherence mechanic at all.
        _decorate.ShowRoom(tile);
        ShowOnly(_decorate);

        GD.Print($"[GameScene] Decorating {room.RoomName} — " +
                 $"appointment {room.AppointmentScore:F0}, {room.Furniture.Count} pieces.");
    }

    // ── Night handlers ─────────────────────────────────────────────────

    private void OnPhaseChanged(int oldPhase, int newPhase)
    {
        var phase = (NightPhase)newPhase;

        // Building is a Preparation-time activity; the panel greys out once
        // the doors open so the player cannot rebuild under a live client.
        _hud?.SetStatusChip(HudTool.Info, phase.ToString(), phase == NightPhase.Service);
        RefreshNextStep();

        if (phase == NightPhase.Idle || phase == NightPhase.Preparation)
        {
            _clouds.ClearAll();
            ClearLobbyPawns();

            // The HUD's own night button also drives the director, so the
            // night can leave the Ledger phase without our Continue handler
            // running. Dismiss the screen on the phase change rather than on
            // the button, or it strands itself over the game.
            if (_ledger.IsShowing) _ledger.Hide();
        }
    }

    private void OnEncounterStarted(string staffId, int x, int y, int floor, float duration)
    {
        var cell = new Vector3I(x, y, floor);
        _encounterCells[staffId] = cell;

        // Move the working staff member into the room.
        var staffPawn = StaffPawnId(staffId);
        _pawns.MovePawn(staffPawn, cell);

        // Walk a waiting client up to join them rather than deleting them from
        // the lobby. The encounter is meant to read as two people meeting, and
        // a client that simply vanishes loses that entirely.
        var clientPawn = TakeLobbyPawn();
        if (clientPawn != null)
        {
            _encounterClients[staffId] = clientPawn;
            _pawns.MovePawn(clientPawn, cell);
        }

        // The conversation beat: both talk while they walk in and settle,
        // before the cloud takes over. Rigs without a talk clip fall back to
        // a neutral pose, so this is safe with the current exports.
        _pawns.SetPawnState(staffPawn, CharacterAnimation.Talk);
        if (clientPawn != null) _pawns.SetPawnState(clientPawn, CharacterAnimation.Talk);

        _clouds.StartCloud(staffId, cell, duration);
    }

    private void OnEncounterResolved(string staffId, int quality, double payment, int incident)
    {
        _clouds.ResolveCloud(staffId, (EncounterQuality)quality);
        _encounterCells.Remove(staffId);

        // The client leaves; the staff member goes back to standing.
        if (_encounterClients.TryGetValue(staffId, out var clientPawn))
        {
            _pawns.RemovePawn(clientPawn);
            _encounterClients.Remove(staffId);
        }

        _pawns.SetPawnState(StaffPawnId(staffId), CharacterAnimation.Idle);

        var band = (EncounterQuality)quality;
        if ((EncounterIncident)incident != EncounterIncident.None)
            _hud?.SetStatusChip(HudTool.Alert, band.ToString(), true);
    }

    private void OnClientArrived(string clientName, int tier)
    {
        var venue = _boot?.Venue;
        if (venue == null) return;

        var cell = GetLobbyCell(venue);
        var id = $"client:{_lobbyCounter++}";

        _pawns.AddPawn(id, cell, isStaff: false, clientName);
        _lobbyPawns.Add(id);
    }

    private void OnNightConcluded(int night, double revenue, double net)
    {
        _clouds.ClearAll();
        ClearLobbyPawns();

        // A capture run plays itself through to the state worth photographing
        // instead of stopping at the first Ledger.
        if (_autoPlayNightsLeft > 0)
        {
            _autoPlayNightsLeft--;

            if (_autoPlayNightsLeft > 0)
            {
                CallDeferred(nameof(PlayAnotherNight));
                return;
            }

            CallDeferred(nameof(OpenBookForCapture));
            return;
        }

        // A crisis takes precedence over the books. It is the one thing the
        // player is not allowed to scroll past.
        if (ShowPendingCrisis()) return;

        _ledger.Show(_boot.Night.CurrentReport);
    }

    // ── Crises ─────────────────────────────────────────────────────────

    /// <summary>
    /// Put an unanswered crisis on screen, if there is one.
    /// </summary>
    /// <returns>True if a crisis was shown, so the Ledger should wait.</returns>
    private bool ShowPendingCrisis()
    {
        var director = _boot?.Crises;
        if (director?.CrisisActive != true || director.ActiveScenario == null) return false;

        return _crisis.Show(director.ActiveScenario);
    }

    private void OnCrisisChoiceTaken(int choiceIndex)
    {
        var director = _boot?.Crises;

        // Hide first: ExecuteChoice moves cash and heat, and the HUD refresh
        // that follows should not happen behind a modal that is about to
        // close anyway.
        _crisis.Hide();

        if (director?.ExecuteChoice(choiceIndex) != true)
        {
            // Never leave the director latched on a refused choice — that is
            // the exact deadlock this system shipped with.
            director?.DismissCrisis();
        }

        _hud?.RefreshAll();
        _staff?.Refresh();

        // The books still have to be read.
        _ledger.Show(_boot.Night.CurrentReport);
    }

    private void OnCrisisDismissed()
    {
        _crisis.Hide();
        _boot?.Crises?.DismissCrisis();
        _ledger.Show(_boot.Night.CurrentReport);
    }

    /// <summary>Number of nights a capture run should still play unattended.</summary>
    private int _autoPlayNightsLeft;

    private void PlayAnotherNight()
    {
        _boot.Night.ConcludeNight();
        _boot.Night.BeginNight();
        _boot.AutoAssignStaff();
        RefreshStaffPawns();
        _boot.Night.OpenDoors();
    }

    private void OpenBookForCapture()
    {
        _boot.Night.ConcludeNight();
        TogglePatronsPanel(true);
    }

    private void OnLedgerContinue()
    {
        _ledger.Hide();

        // Both guard on phase internally, so this stays correct even if the
        // HUD's night button already advanced us.
        _boot.Night.ConcludeNight();
        _view.Refresh();

        _boot.Night.BeginNight();
        _boot.AutoAssignStaff();

        RefreshStaffPawns();
        RefreshCityChip();
        _hud.RefreshAll();
    }

    // ── Pawns ──────────────────────────────────────────────────────────

    private static string StaffPawnId(string staffId) => $"staff:{staffId}";

    /// <summary>
    /// Rebuild the staff pawns from tonight's postings. Staff without a
    /// posting are shown in the lounge rather than hidden, so an idle
    /// employee is visible as a cost rather than silently absent.
    /// </summary>
    private void RefreshStaffPawns()
    {
        var roster = StaffRoster.Instance;
        var venue = _boot?.Venue;
        var night = _boot?.Night;
        if (roster == null || venue == null || night == null) return;

        foreach (var staff in roster.GetAll())
        {
            var assignment = night.Assignments.FirstOrDefault(a => a.StaffId == staff.Id);
            var cell = assignment?.RoomTile ?? GetLobbyCell(venue);
            var id = StaffPawnId(staff.Id);

            if (_pawns.HasPawn(id)) _pawns.MovePawn(id, cell);
            else _pawns.AddPawn(id, cell, isStaff: true, staff.StaffName);
        }
    }

    /// <summary>
    /// Where unposted staff and waiting clients stand. Prefers a ground-floor
    /// lounge, falling back to the building origin.
    /// </summary>
    private static Vector3I GetLobbyCell(VenueBuilding venue)
    {
        var lounge = venue.GetRoomsOnFloor(0)
            .FirstOrDefault(r => r.Type is RoomType.Lounge or RoomType.Bar);

        return lounge?.GridPosition ?? new Vector3I(0, 0, 0);
    }

    /// <summary>
    /// Take the longest-waiting client out of the lobby queue without
    /// destroying its pawn — the caller walks it into a room.
    /// </summary>
    private string TakeLobbyPawn()
    {
        if (_lobbyPawns.Count == 0) return null;

        var id = _lobbyPawns[0];
        _lobbyPawns.RemoveAt(0);
        return id;
    }

    private void ClearLobbyPawns()
    {
        foreach (var id in _lobbyPawns) _pawns.RemovePawn(id);
        _lobbyPawns.Clear();

        // Clients mid-encounter are not in the queue but still on screen.
        foreach (var id in _encounterClients.Values) _pawns.RemovePawn(id);
        _encounterClients.Clear();
    }

    // ── Frame update ───────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_boot?.Night == null || _hud == null) return;

        // Client satisfaction is the headline meter in the reference art.
        // Derived from the running night's outcome mix so it actually moves
        // during service rather than only at the Ledger.
        _hud.SetClientSatisfaction(GetLiveSatisfaction());
    }

    /// <summary>
    /// A 0–10 read on how the night is going, weighted by outcome band.
    /// Sits at a neutral 5 before anything has resolved.
    /// </summary>
    private float GetLiveSatisfaction()
    {
        var counts = _boot.Night.CurrentReport?.QualityCounts;
        if (counts == null || counts.Count == 0) return 5f;

        float total = counts.Values.Sum();
        if (total <= 0f) return 5f;

        float weighted = 0f;
        foreach (var (band, count) in counts)
        {
            weighted += band switch
            {
                EncounterQuality.Exceptional => 10f,
                EncounterQuality.Good => 8f,
                EncounterQuality.Adequate => 5.5f,
                EncounterQuality.Poor => 2.5f,
                _ => 0f
            } * count;
        }

        return Mathf.Clamp(weighted / total, 0f, 10f);
    }
}
