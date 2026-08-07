using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The playable scene root: constructs the world, the view layers and the
/// HUD, and wires them to each other.
///
/// Layering, back to front:
///   ColorRect backdrop  — Node2D has no bounded rect of its own to fill
///   IsometricDollhouseView (Node2D)
///     └ VenuePawnLayer   — a child so it inherits the view's pan and zoom
///     └ EncounterCloudVfx — likewise, so clouds stay pinned to their rooms
///   GameHud (CanvasLayer)
///   NightLedgerScreen (CanvasLayer, above the HUD)
///
/// The view layers are children of the dollhouse view deliberately. They all
/// position via <see cref="IsoTheme.GridToScreen"/> in the same local space,
/// so parenting keeps them aligned under camera movement for free rather
/// than mirroring a transform every frame.
/// </summary>
public partial class GameScene : Node
{
    [Export] public bool AutoBeginFirstNight { get; set; } = true;

    /// <summary>Floor the camera opens on. Ground by default.</summary>
    [Export] public int InitialFloor { get; set; }

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

    /// <summary>Seconds before an automatic screenshot. Zero disables it.</summary>
    [Export] public float ScreenshotAfterSeconds { get; set; }

    private GameBootstrap _boot;
    private IsometricDollhouseView _view;
    private VenuePawnLayer _pawns;
    private EncounterCloudVfx _clouds;
    private GameHud _hud;
    private NightLedgerScreen _ledger;
    private DecoratePanel _decorate;
    private StaffPanel _staff;
    private ScreenshotCapture _screenshot;

    /// <summary>Maps an in-flight encounter to the staff pawn working it.</summary>
    private readonly Dictionary<string, Vector3I> _encounterCells = new();

    /// <summary>Lobby clients currently drawn, by pawn id.</summary>
    private readonly List<string> _lobbyPawns = new();

    private int _lobbyCounter;

    public override void _Ready()
    {
        BuildBackdrop();
        BuildWorld();
        BuildViewLayers();
        BuildInterface();

        ConnectSignals();

        // Deferred so GameBootstrap's own deferred seeding has run and there
        // is actually a venue to look at.
        CallDeferred(nameof(OnWorldSettled));
    }

    // ── Construction ───────────────────────────────────────────────────

    private void BuildBackdrop()
    {
        var backdrop = new ColorRect
        {
            Name = "Backdrop",
            Color = IsoTheme.Backdrop,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        AddChild(backdrop);
    }

    private void BuildWorld()
    {
        _boot = new GameBootstrap
        {
            Name = "GameBootstrap",
            UseLegacyDayTimer = false
        };

        AddChild(_boot);
    }

    private void BuildViewLayers()
    {
        _view = new IsometricDollhouseView { Name = "DollhouseView" };
        AddChild(_view);

        _pawns = new VenuePawnLayer { Name = "PawnLayer", ZIndex = 100 };
        _view.AddChild(_pawns);

        _clouds = new EncounterCloudVfx
        {
            Name = "CloudVfx",

            // Defaults billowed wide enough to blanket the whole room and
            // hide the furniture underneath. The cloud is a status indicator
            // sitting *above* the room, not weather covering it — so it rides
            // higher, reads smaller, and stays translucent.
            CloudRadius = 13f,
            CloudLift = 74f,
            CloudOpacity = 0.5f,
            MaxPuffs = 5
        };

        _view.AddChild(_clouds);
    }

    private void BuildInterface()
    {
        _hud = new GameHud { Name = "GameHud" };
        AddChild(_hud);

        _ledger = new NightLedgerScreen { Name = "NightLedger" };
        AddChild(_ledger);

        // Side panels are Controls, so they need a CanvasLayer to sit above
        // the Node2D dollhouse.
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

        _screenshot = new ScreenshotCapture
        {
            Name = "ScreenshotCapture",
            AutoCaptureAfterSeconds = ScreenshotAfterSeconds
        };

        AddChild(_screenshot);
    }

    private const float DecoratePanelWidth = 360f;
    private const float StaffPanelWidth = 400f;

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

        _decorate.OnRoomChanged += OnDecorateRoomChanged;
        _decorate.OnCloseRequested += () => _decorate.Visible = false;

        _staff.OnCloseRequested += () => _staff.Visible = false;
        _staff.OnRosterChanged += OnRosterChanged;

        // The HUD's roster controls open the staff panel.
        _hud.OnStaffSelected += _ => ToggleStaffPanel(true);
    }

    /// <summary>A hire or departure changes who is posted, so redraw the pawns.</summary>
    private void OnRosterChanged()
    {
        RefreshStaffPawns();
        _hud?.RefreshAll();
    }

    /// <summary>
    /// Show or hide the staff panel. The two left-hand panels are mutually
    /// exclusive because they occupy the same column.
    /// </summary>
    private void ToggleStaffPanel(bool show)
    {
        _staff.Visible = show;
        if (show)
        {
            _decorate.Visible = false;
            _staff.Refresh();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        switch (key.Keycode)
        {
            // No HUD affordance opens the roster yet, so it is on a key.
            case Key.S:
                ToggleStaffPanel(!_staff.Visible);
                break;

            case Key.Escape:
                _staff.Visible = false;
                _decorate.Visible = false;
                break;
        }
    }

    /// <summary>Repaint the dollhouse after a purchase or sale changes a room.</summary>
    private void OnDecorateRoomChanged(int x, int y, int floor)
    {
        _view.Refresh();
        _hud?.RefreshAll();
    }

    private void OnWorldReady()
    {
        _view.Bind(_boot.Venue);
        _hud.Bind(_boot.Night, _boot.Venue);
        _decorate.Bind(_boot.Catalog, _boot.Venue);
        _staff.Bind(_boot.Recruitment);

        var night = _boot.Night;
        night.OnEncounterStarted += OnEncounterStarted;
        night.OnEncounterResolved += OnEncounterResolved;
        night.OnNightConcluded += OnNightConcluded;
        night.OnClientArrived += OnClientArrived;
        night.OnPhaseChanged += OnPhaseChanged;

        _view.FocusedFloor = InitialFloor;
        FitCameraToBuilding();

        RefreshStaffPawns();
    }

    /// <summary>
    /// Frame the whole building in the viewport.
    ///
    /// Centring alone is not enough: a tall building overflows vertically and
    /// the top floor gets clipped, and the required zoom changes every time
    /// the player buys a storey. This derives the zoom from the drawn bounds
    /// against the free screen area, so it stays correct as the house grows.
    /// </summary>
    private void FitCameraToBuilding()
    {
        if (_view == null) return;

        var bounds = _view.GetBuildingBounds();
        if (bounds.Size.X <= 1f || bounds.Size.Y <= 1f)
        {
            _view.CenterOnBuilding();
            return;
        }

        var viewport = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);

        // The HUD occupies the top bar, the right panel and the lower centre.
        // Fit against what is actually free rather than the whole window.
        var usable = new Vector2(
            Mathf.Max(320f, viewport.X - HudRightPanelWidth - HudLeftMargin),
            Mathf.Max(240f, viewport.Y - HudTopBarHeight - HudBottomHeight));

        var zoom = Mathf.Min(usable.X / bounds.Size.X, usable.Y / bounds.Size.Y) * FitPadding;

        _view.Zoom = zoom;

        // Centre the bounds inside the usable region, then shift right and
        // down to clear the top bar and left of the build panel.
        var center = bounds.Position + bounds.Size * 0.5f;
        _view.PanOffset = new Vector2(
            HudLeftMargin + usable.X * 0.5f - center.X * zoom,
            HudTopBarHeight + usable.Y * 0.5f - center.Y * zoom);
    }

    private const float HudTopBarHeight = 58f;
    private const float HudRightPanelWidth = 268f;
    private const float HudLeftMargin = 150f;
    private const float HudBottomHeight = 130f;

    /// <summary>Leave a margin so the building never touches the HUD edges.</summary>
    private const float FitPadding = 0.95f;

    private void OnWorldSettled()
    {
        if (AutoBeginFirstNight && _boot?.Night?.Phase == NightPhase.Idle)
        {
            _boot.Night.BeginNight();
            _boot.AutoAssignStaff();
            RefreshStaffPawns();
        }

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

        if (CaptureStaffPanel)
        {
            ToggleStaffPanel(true);
            _staff.ShowTab((StaffPanelTab)Mathf.Clamp(CaptureStaffTab, 0, 1));
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
        _decorate.Visible = true;

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

        // Move the working staff member into the room, then start the cloud.
        _pawns.MovePawn(StaffPawnId(staffId), cell);
        _clouds.StartCloud(staffId, cell, duration);

        // One client leaves the lobby to join them.
        PopLobbyPawn();
    }

    private void OnEncounterResolved(string staffId, int quality, double payment, int incident)
    {
        _clouds.ResolveCloud(staffId, (EncounterQuality)quality);
        _encounterCells.Remove(staffId);

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
        _ledger.Show(_boot.Night.CurrentReport);
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

    private void PopLobbyPawn()
    {
        if (_lobbyPawns.Count == 0) return;

        _pawns.RemovePawn(_lobbyPawns[0]);
        _lobbyPawns.RemoveAt(0);
    }

    private void ClearLobbyPawns()
    {
        foreach (var id in _lobbyPawns) _pawns.RemovePawn(id);
        _lobbyPawns.Clear();
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
