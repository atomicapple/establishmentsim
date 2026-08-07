using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>Room categories placeable in the building.</summary>
public enum RoomType
{
    Empty,
    Lounge,         // Entertainment, high noise, client-facing
    VIPSuite,       // Premium private suite, noise-sensitive
    PrivateSuite,   // Standard private suite, noise-sensitive
    Security,       // Monitoring, reduces heat for neighbours
    Service,        // Backend operations, staff-only
    Bar,            // Moderate noise, drink service
    Medical,        // Staff recovery room (requires Medical Care policy)
    Storage,        // Supplies and inventory
    VIPEntrance,    // Discreet street access for high-tier clients
    Soundproofing   // Structural upgrade cell — kills noise for its neighbours
}

/// <summary>What a floor index means for revenue, scrutiny, and prestige.</summary>
public enum FloorKind
{
    Basement,   // z < 0 — no revenue, pure liability if discovered
    Ground,     // z == 0 — public face: screening, upsell, street exposure
    Upper       // z > 0 — private suites, prestige scales with height
}

/// <summary>Why two rooms are interacting.</summary>
public enum SynergyKind
{
    Noise,           // Noisy room bleeding into a quiet one (sideways or downward)
    Security,        // Security coverage suppressing heat
    Ambiance,        // Luxury rooms flattering each other
    Scrutiny,        // Street visibility contaminating the floor above
    FloorPrestige    // Height premium on upper floors
}

/// <summary>
/// Construction cost and baseline stats for a room type. This is the single
/// source of truth for room pricing — extracted from the old
/// <c>BlueprintBuildUI.RoomCosts</c> table, which was the only place these
/// numbers existed.
/// </summary>
public struct RoomBlueprint
{
    public double Cash;
    public float Luxury;
    public float Discretion;
    public float Noise;

    /// <summary>Default footprint in tiles.</summary>
    public Vector2I Size;

    /// <summary>How exposed to the street this room type is by default (0–100).</summary>
    public float StreetVisibility;

    /// <summary>Baseline soundproofing the shell ships with (0–100).</summary>
    public float Soundproofing;

    public string DisplayName;

    public override string ToString() =>
        $"{DisplayName} ${Cash:N0} ({Size.X}×{Size.Y}) " +
        $"Lux={Luxury:F0} Disc={Discretion:F0} Noise={Noise:F0}";
}

/// <summary>Describes a directional interaction between two placed rooms.</summary>
public struct AdjacencySynergy
{
    public Vector3I RoomA;
    public Vector3I RoomB;
    public SynergyKind Kind;
    public string Description;
    public float EffectValue;

    public override string ToString() =>
        $"{RoomA}↔{RoomB} [{Kind}]: {Description} ({EffectValue:+0.0%;-0.0%})";
}

// ── RoomModule Resource ────────────────────────────────────────────────

/// <summary>
/// A single room occupying a rectangular footprint on one floor of the
/// building. Rooms are no longer single cells: <see cref="Size"/> covers
/// several tiles, and the furniture a room can hold scales with its area.
/// </summary>
[GlobalClass]
public partial class RoomModule : Resource
{
    [Export] public string RoomName { get; set; } = "New Room";
    [Export] public RoomType Type { get; set; } = RoomType.Empty;

    /// <summary>
    /// Origin tile. X/Y are the floorplan coordinates, <b>Z is the floor
    /// index</b> — negative is basement, 0 is ground, positive is upstairs.
    /// </summary>
    [Export] public Vector3I GridPosition { get; set; }

    /// <summary>Footprint in tiles. Placement checks every covered tile.</summary>
    [Export] public Vector2I Size { get; set; } = Vector2I.One;

    // ── Core Stats (0–100) ─────────────────────────────────────────────

    [Export(PropertyHint.Range, "0,100,1")]
    public float LuxuryScore
    {
        get => _luxuryScore;
        set => _luxuryScore = Mathf.Clamp(value, 0f, 100f);
    }
    private float _luxuryScore = 30f;

    [Export(PropertyHint.Range, "0,100,1")]
    public float Soundproofing
    {
        get => _soundproofing;
        set => _soundproofing = Mathf.Clamp(value, 0f, 100f);
    }
    private float _soundproofing = 20f;

    [Export(PropertyHint.Range, "0,100,1")]
    public float DiscretionRating
    {
        get => _discretionRating;
        set => _discretionRating = Mathf.Clamp(value, 0f, 100f);
    }
    private float _discretionRating = 40f;

    /// <summary>Noise this room emits at full tilt, 0–100.</summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public float NoiseLevel
    {
        get => _noiseLevel;
        set => _noiseLevel = Mathf.Clamp(value, 0f, 100f);
    }
    private float _noiseLevel;

    /// <summary>
    /// How much of this room is visible from the street, 0–100. Feeds the
    /// upward scrutiny propagation — a shopfront lounge drags the flat
    /// above it into the same police report.
    /// </summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public float StreetVisibility
    {
        get => _streetVisibility;
        set => _streetVisibility = Mathf.Clamp(value, 0f, 100f);
    }
    private float _streetVisibility;

    // ── Furniture ──────────────────────────────────────────────────────

    /// <summary>
    /// Pieces placed in this room. Drives the Appointment score, which
    /// drives what clients will pay. Not exported — Godot cannot round-trip
    /// a generic list of sub-resources reliably, so the venue owns
    /// persistence via its save slice.
    /// </summary>
    public List<FurnitureItem> Furniture { get; } = new();

    /// <summary>Furniture tile budget, derived from footprint area.</summary>
    public int FurnitureCapacity => Mathf.Max(2, Area * FurnitureSlotsPerTile);

    /// <summary>Furniture slots granted per floor tile.</summary>
    public const int FurnitureSlotsPerTile = 2;

    /// <summary>Furniture tiles currently consumed.</summary>
    public int UsedFurnitureArea => Furniture.Sum(f => f?.Area ?? 0);

    /// <summary>Remaining furniture tile budget.</summary>
    public int FreeFurnitureArea => Mathf.Max(0, FurnitureCapacity - UsedFurnitureArea);

    // ── Runtime Modifiers (recomputed by RecalculateSynergies) ─────────

    public float HeatModifier { get; set; }
    public float SatisfactionModifier { get; set; }
    public float RevenueModifier { get; set; }

    /// <summary>Noise bleeding in from neighbours after soundproofing, 0–100.</summary>
    public float NoiseExposure { get; set; }

    /// <summary>Police attention inherited from the floor below, 0–100.</summary>
    public float ScrutinyExposure { get; set; }

    /// <summary>
    /// Extra soundproofing granted by adjacent Soundproofing cells.
    /// Recomputed each recalculation, before noise is evaluated.
    /// </summary>
    public float SoundproofingBonus { get; set; }

    /// <summary>Soundproofing including adjacency bonuses, clamped 0–100.</summary>
    public float EffectiveSoundproofing =>
        Mathf.Clamp(Soundproofing + SoundproofingBonus, 0f, 100f);

    /// <summary>Cached Appointment score, 0–100. Refreshed on recalculation.</summary>
    public float AppointmentScore { get; set; }

    // ── Derived ────────────────────────────────────────────────────────

    /// <summary>Floor index this room sits on.</summary>
    public int Floor => GridPosition.Z;

    /// <summary>Footprint area in tiles.</summary>
    public int Area => Mathf.Max(1, Size.X * Size.Y);

    /// <summary>Whether this room type generates noise that disturbs neighbours.</summary>
    public bool IsNoisy => Type is RoomType.Lounge or RoomType.Bar || NoiseLevel >= 40f;

    /// <summary>Whether this room type is ruined by noise.</summary>
    public bool IsNoiseSensitive => Type is RoomType.VIPSuite or RoomType.PrivateSuite;

    /// <summary>Rooms clients actually pay to occupy.</summary>
    public bool IsBookable => Type is RoomType.VIPSuite or RoomType.PrivateSuite;

    /// <summary>Every tile this room covers, including its origin.</summary>
    public IEnumerable<Vector3I> GetTiles()
    {
        for (int dx = 0; dx < Mathf.Max(1, Size.X); dx++)
        for (int dy = 0; dy < Mathf.Max(1, Size.Y); dy++)
            yield return new Vector3I(GridPosition.X + dx, GridPosition.Y + dy, GridPosition.Z);
    }

    /// <summary>True if this room covers the given tile.</summary>
    public bool Covers(Vector3I tile) =>
        tile.Z == GridPosition.Z &&
        tile.X >= GridPosition.X && tile.X < GridPosition.X + Mathf.Max(1, Size.X) &&
        tile.Y >= GridPosition.Y && tile.Y < GridPosition.Y + Mathf.Max(1, Size.Y);

    /// <summary>Total nightly maintenance for the furniture in this room.</summary>
    public double GetFurnitureUpkeep() => Furniture.Sum(f => f?.Upkeep ?? 0.0);

    /// <summary>Age every piece by one night of use.</summary>
    public void WearFurniture(float usage)
    {
        foreach (var item in Furniture)
            item?.Wear(usage);
    }

    public override string ToString() =>
        $"[{GridPosition.X},{GridPosition.Y}@F{GridPosition.Z}] {RoomName} ({Type}) " +
        $"{Size.X}×{Size.Y} Lux={LuxuryScore:F0} Sound={Soundproofing:F0} " +
        $"Disc={DiscretionRating:F0} Appt={AppointmentScore:F0} " +
        $"Furn={Furniture.Count}/{FurnitureCapacity}";
}

// ── VenueBuilding Node ─────────────────────────────────────────────────

/// <summary>
/// The venue as a multi-storey building rather than a flat plan.
///
/// Coordinates are <see cref="Vector3I"/> with Z as the floor index.
/// The player opens with three floors — basement (−1), ground (0), and one
/// upper (+1) — and can extend upward to <see cref="MaxPossibleFloor"/>.
///
/// Floors are not interchangeable:
///   • <b>Basement</b> earns nothing. It is storage, security, and things
///     you would rather nobody found; it only ever contributes liability.
///   • <b>Ground</b> is the public face — the lounge/bar where clients are
///     screened and upsold, and where the street can see in.
///   • <b>Upper</b> floors are private suites and carry a prestige premium
///     that grows with height.
///
/// The two vertical mechanics run in opposite directions:
///   • <b>Noise falls.</b> A lounge bleeds into the suite below it and into
///     the rooms beside it, mitigated by the quiet room's Soundproofing.
///   • <b>Scrutiny rises.</b> A street-visible ground room contaminates
///     whatever sits directly above it.
///
/// Usage: add as a Node named "VenueBuilding". Build via
///   <see cref="BuildRoom"/> (charges cash) or <see cref="PlaceRoom"/> (raw),
///   furnish via <see cref="AddFurniture"/>, and read the results through
///   <see cref="GetRoomAppointment"/> / <see cref="GetEffectiveLuxury"/>.
/// </summary>
public partial class VenueBuilding : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a room is placed. Floor is the Z coordinate.</summary>
    [Signal]
    public delegate void OnRoomPlacedEventHandler(
        int x, int y, int floor, int roomType, string roomName);

    /// <summary>Fired when a room is removed.</summary>
    [Signal]
    public delegate void OnRoomRemovedEventHandler(int x, int y, int floor, string roomName);

    /// <summary>Fired when a new floor is opened up.</summary>
    [Signal]
    public delegate void OnFloorAddedEventHandler(int floor, int totalFloors);

    /// <summary>Fired when adjacency/vertical synergies are recalculated.</summary>
    [Signal]
    public delegate void OnSynergiesUpdatedEventHandler(int synergyCount);

    /// <summary>Fired when a room's furniture changes, with its new Appointment.</summary>
    [Signal]
    public delegate void OnRoomAppointmentChangedEventHandler(
        int x, int y, int floor, string roomName, float appointment);

    /// <summary>Fired when a build or furnish attempt is rejected.</summary>
    [Signal]
    public delegate void OnBuildRejectedEventHandler(string reason);

    // ── Room Cost Table ────────────────────────────────────────────────

    /// <summary>
    /// Construction costs and baseline stats. The cash/luxury/discretion/noise
    /// figures for PrivateSuite, Lounge, Security, Soundproofing, Service and
    /// VIPEntrance are carried over verbatim from the retired
    /// <c>BlueprintBuildUI.RoomCosts</c> table — that file was the only place
    /// room pricing existed in the codebase.
    /// </summary>
    public static readonly IReadOnlyDictionary<RoomType, RoomBlueprint> RoomCosts =
        new Dictionary<RoomType, RoomBlueprint>
        {
            [RoomType.PrivateSuite] = new()
            {
                // 3×2 rather than 2×2: a two-tile suite could not hold its
                // furniture, a staff pawn, a label and an encounter cloud
                // without the furnishings becoming unreadable — and furniture
                // is what the room is actually selling. The wider footprint
                // also lifts its furniture capacity from 8 slots to 12.
                Cash = 1000, Luxury = 25, Discretion = 20, Noise = 5,
                Size = new Vector2I(3, 2), StreetVisibility = 10, Soundproofing = 25,
                DisplayName = "Private Suite"
            },
            [RoomType.Lounge] = new()
            {
                Cash = 500, Luxury = 15, Discretion = 5, Noise = 25,
                Size = new Vector2I(3, 2), StreetVisibility = 55, Soundproofing = 10,
                DisplayName = "Lounge"
            },
            [RoomType.Security] = new()
            {
                Cash = 1200, Luxury = 5, Discretion = 35, Noise = 0,
                Size = new Vector2I(2, 1), StreetVisibility = 5, Soundproofing = 30,
                DisplayName = "Security Hub"
            },
            [RoomType.Soundproofing] = new()
            {
                Cash = 300, Luxury = 0, Discretion = 15, Noise = -20,
                Size = new Vector2I(1, 1), StreetVisibility = 0, Soundproofing = 90,
                DisplayName = "Soundproofing"
            },
            [RoomType.Service] = new()
            {
                Cash = 200, Luxury = 0, Discretion = 5, Noise = 0,
                Size = new Vector2I(1, 2), StreetVisibility = 5, Soundproofing = 15,
                DisplayName = "Service Corridor"
            },
            [RoomType.VIPEntrance] = new()
            {
                Cash = 1500, Luxury = 30, Discretion = 15, Noise = 10,
                Size = new Vector2I(2, 1), StreetVisibility = 40, Soundproofing = 20,
                DisplayName = "VIP Entrance"
            },

            // ── Types that predate the blueprint table ──────────────────
            [RoomType.VIPSuite] = new()
            {
                Cash = 2200, Luxury = 45, Discretion = 35, Noise = 5,
                Size = new Vector2I(3, 2), StreetVisibility = 5, Soundproofing = 45,
                DisplayName = "VIP Suite"
            },
            [RoomType.Bar] = new()
            {
                Cash = 900, Luxury = 20, Discretion = 5, Noise = 35,
                Size = new Vector2I(3, 2), StreetVisibility = 60, Soundproofing = 10,
                DisplayName = "Bar"
            },
            [RoomType.Medical] = new()
            {
                Cash = 1000, Luxury = 10, Discretion = 25, Noise = 0,
                Size = new Vector2I(2, 2), StreetVisibility = 5, Soundproofing = 35,
                DisplayName = "Medical Room"
            },
            [RoomType.Storage] = new()
            {
                Cash = 150, Luxury = 0, Discretion = 10, Noise = 0,
                Size = new Vector2I(2, 1), StreetVisibility = 0, Soundproofing = 20,
                DisplayName = "Storage"
            },
            [RoomType.Empty] = new()
            {
                Cash = 0, Luxury = 0, Discretion = 0, Noise = 0,
                Size = Vector2I.One, StreetVisibility = 0, Soundproofing = 0,
                DisplayName = "Empty"
            }
        };

    // ── Building Configuration ─────────────────────────────────────────

    /// <summary>Floorplan width in tiles (X axis), identical on every floor.</summary>
    [Export] public int FloorWidth { get; set; } = 6;

    /// <summary>Floorplan depth in tiles (Y axis), identical on every floor.</summary>
    [Export] public int FloorDepth { get; set; } = 5;

    /// <summary>Deepest excavatable floor.</summary>
    public const int MinPossibleFloor = -1;

    /// <summary>Highest buildable floor. −1..4 gives the six-storey ceiling.</summary>
    public const int MaxPossibleFloor = 4;

    /// <summary>Base construction cost of opening one new floor.</summary>
    [Export] public double FloorConstructionCost { get; set; } = 4500.0;

    /// <summary>Fraction of build cost returned when a room is demolished.</summary>
    [Export] public float DemolitionRefundRate { get; set; } = 0.5f;

    // ── Storage ────────────────────────────────────────────────────────

    /// <summary>Rooms keyed by their origin tile.</summary>
    private readonly Dictionary<Vector3I, RoomModule> _rooms = new();

    /// <summary>Every covered tile → the room covering it.</summary>
    private readonly Dictionary<Vector3I, RoomModule> _occupancy = new();

    private readonly List<AdjacencySynergy> _synergies = new();
    private readonly SortedSet<int> _floors = new();

    /// <summary>Read-only view of all placed rooms, keyed by origin tile.</summary>
    public IReadOnlyDictionary<Vector3I, RoomModule> Rooms => _rooms;

    /// <summary>Current synergies across the whole building.</summary>
    public IReadOnlyList<AdjacencySynergy> ActiveSynergies => _synergies;

    /// <summary>Floor indices that currently exist, lowest first.</summary>
    public IReadOnlyCollection<int> Floors => _floors;

    /// <summary>Lowest existing floor index.</summary>
    public int LowestFloor => _floors.Count > 0 ? _floors.Min : 0;

    /// <summary>Highest existing floor index.</summary>
    public int HighestFloor => _floors.Count > 0 ? _floors.Max : 0;

    /// <summary>Number of floors currently built.</summary>
    public int FloorCount => _floors.Count;

    // ── Synergy Tuning ─────────────────────────────────────────────────

    /// <summary>Satisfaction penalty when a noisy room borders a quiet one.</summary>
    public float NoisyNeighbourPenalty { get; set; } = 0.20f;

    /// <summary>How much of a noise penalty survives a floor slab.</summary>
    public float VerticalNoiseFactor { get; set; } = 0.7f;

    /// <summary>Soundproofing mitigation rate: 1.0 means 100 proofing cancels fully.</summary>
    public float SoundproofingMitigation { get; set; } = 1.0f;

    /// <summary>Heat reduction a security room grants its neighbours.</summary>
    public float SecurityHeatReduction { get; set; } = 0.15f;

    /// <summary>Fraction of a room's street visibility that leaks to the floor above.</summary>
    public float ScrutinyPropagation { get; set; } = 0.5f;

    /// <summary>Prestige (and therefore price) bonus per floor above ground.</summary>
    public float PrestigeBonusPerFloor { get; set; } = 6f;

    /// <summary>How strongly Appointment moves a room's price, at Appointment 100.</summary>
    public float AppointmentPriceInfluence { get; set; } = 0.6f;

    /// <summary>Whether diagonal tiles count as adjacent.</summary>
    public bool UseDiagonalAdjacency { get; set; }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        if (_floors.Count == 0)
            ResetToStartingFloors();

        GD.Print($"[VenueBuilding] Initialized. {FloorWidth}×{FloorDepth} per floor, " +
                 $"floors {LowestFloor}..{HighestFloor}. Rooms: {_rooms.Count}");
    }

    /// <summary>Restore the campaign-start floor set: basement, ground, first upper.</summary>
    public void ResetToStartingFloors()
    {
        _floors.Clear();
        _floors.Add(-1);
        _floors.Add(0);
        _floors.Add(1);
    }

    // ── Floors ─────────────────────────────────────────────────────────

    /// <summary>Semantic role of a floor index.</summary>
    public static FloorKind GetFloorKind(int floor) =>
        floor < 0 ? FloorKind.Basement :
        floor == 0 ? FloorKind.Ground :
        FloorKind.Upper;

    /// <summary>Whether a floor index has been built.</summary>
    public bool HasFloor(int floor) => _floors.Contains(floor);

    /// <summary>Cost to open the next floor. Each storey is dearer than the last.</summary>
    public double GetFloorCost(int floor)
    {
        int distance = Mathf.Abs(floor);
        double kindMultiplier = floor < 0 ? 1.6 : 1.0;   // excavation costs more
        return Math.Round(FloorConstructionCost * kindMultiplier * (1.0 + 0.35 * distance), 2);
    }

    /// <summary>
    /// Open a floor without charging for it. Floors must be contiguous —
    /// you cannot build a fifth storey over open air.
    /// </summary>
    public bool AddFloor(int floor)
    {
        if (floor < MinPossibleFloor || floor > MaxPossibleFloor)
        {
            Reject($"Floor {floor} is outside the buildable range " +
                   $"({MinPossibleFloor}..{MaxPossibleFloor}).");
            return false;
        }

        if (_floors.Contains(floor))
        {
            Reject($"Floor {floor} already exists.");
            return false;
        }

        if (_floors.Count > 0 && !_floors.Contains(floor - 1) && !_floors.Contains(floor + 1))
        {
            Reject($"Floor {floor} is not adjacent to an existing floor.");
            return false;
        }

        _floors.Add(floor);
        EmitSignal(SignalName.OnFloorAdded, floor, _floors.Count);
        GD.Print($"[VenueBuilding] Floor {floor} ({GetFloorKind(floor)}) opened. " +
                 $"Building is now {LowestFloor}..{HighestFloor}.");
        return true;
    }

    /// <summary>Open a floor and charge the player for it.</summary>
    public bool BuyFloor(int floor)
    {
        double cost = GetFloorCost(floor);
        var gsm = GameStateManager.Instance;

        if (gsm != null && gsm.Cash < cost)
        {
            Reject($"Insufficient cash for floor {floor}: need ${cost:N0}, have ${gsm.Cash:N0}.");
            return false;
        }

        if (!AddFloor(floor)) return false;

        if (gsm != null) gsm.Cash -= cost;
        return true;
    }

    // ── Placement ──────────────────────────────────────────────────────

    /// <summary>Whether a single tile lies inside a built floor's plan.</summary>
    public bool IsInBounds(Vector3I tile) =>
        HasFloor(tile.Z) &&
        tile.X >= 0 && tile.X < FloorWidth &&
        tile.Y >= 0 && tile.Y < FloorDepth;

    /// <summary>Whether a tile is covered by any room.</summary>
    public bool IsOccupied(Vector3I tile) => _occupancy.ContainsKey(tile);

    /// <summary>The room covering a tile, or null. Works for any covered tile.</summary>
    public RoomModule GetRoom(Vector3I tile) =>
        _occupancy.TryGetValue(tile, out var room) ? room : null;

    /// <summary>The room whose <i>origin</i> is this tile, or null.</summary>
    public RoomModule GetRoomByOrigin(Vector3I origin) =>
        _rooms.TryGetValue(origin, out var room) ? room : null;

    /// <summary>All rooms on a floor.</summary>
    public List<RoomModule> GetRoomsOnFloor(int floor) =>
        _rooms.Values.Where(r => r.Floor == floor).ToList();

    /// <summary>
    /// Whether a room's whole footprint can legally go where it wants to.
    /// Every covered tile is checked, not just the origin.
    /// </summary>
    public bool CanPlaceRoom(RoomModule room, out string reason)
    {
        reason = "";
        if (room == null) { reason = "Room is null."; return false; }

        if (!HasFloor(room.GridPosition.Z))
        {
            reason = $"Floor {room.GridPosition.Z} has not been built.";
            return false;
        }

        foreach (var tile in room.GetTiles())
        {
            if (!IsInBounds(tile))
            {
                reason = $"Footprint tile {tile} falls outside the " +
                         $"{FloorWidth}×{FloorDepth} floorplan.";
                return false;
            }

            if (_occupancy.TryGetValue(tile, out var occupant) && occupant != room)
            {
                reason = $"Footprint tile {tile} is already occupied by '{occupant.RoomName}'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Place an already-built room. Does not charge cash — use
    /// <see cref="BuildRoom"/> for the transactional path.
    /// </summary>
    public bool PlaceRoom(RoomModule room)
    {
        if (!CanPlaceRoom(room, out string reason))
        {
            Reject(reason);
            return false;
        }

        _rooms[room.GridPosition] = room;
        foreach (var tile in room.GetTiles())
            _occupancy[tile] = room;

        RecalculateSynergies();

        EmitSignal(SignalName.OnRoomPlaced,
            room.GridPosition.X, room.GridPosition.Y, room.GridPosition.Z,
            (int)room.Type, room.RoomName);

        GD.Print($"[VenueBuilding] Placed {room}");
        return true;
    }

    /// <summary>
    /// Construct a room of the given type at the given tile, charging the
    /// player and applying the blueprint's baseline stats.
    /// </summary>
    public RoomModule BuildRoom(RoomType type, Vector3I position, string roomName = null)
    {
        var blueprint = GetRoomCost(type);
        var gsm = GameStateManager.Instance;

        if (gsm != null && gsm.Cash < blueprint.Cash)
        {
            Reject($"Insufficient cash: need ${blueprint.Cash:N0}, have ${gsm.Cash:N0}.");
            return null;
        }

        var room = CreateRoom(type, position, roomName);

        if (!CanPlaceRoom(room, out string reason))
        {
            Reject(reason);
            return null;
        }

        if (gsm != null) gsm.Cash -= blueprint.Cash;

        return PlaceRoom(room) ? room : null;
    }

    /// <summary>
    /// Build an unplaced room resource from the blueprint table. Useful for
    /// placement previews and tests.
    /// </summary>
    public static RoomModule CreateRoom(RoomType type, Vector3I position, string roomName = null)
    {
        var blueprint = GetRoomCost(type);

        return new RoomModule
        {
            RoomName = roomName ?? blueprint.DisplayName,
            Type = type,
            GridPosition = position,
            Size = blueprint.Size,
            LuxuryScore = blueprint.Luxury,
            DiscretionRating = blueprint.Discretion,
            NoiseLevel = Mathf.Max(0f, blueprint.Noise),
            Soundproofing = blueprint.Soundproofing,
            StreetVisibility = GetFloorKind(position.Z) == FloorKind.Ground
                ? blueprint.StreetVisibility
                : blueprint.StreetVisibility * 0.3f
        };
    }

    /// <summary>Blueprint entry for a room type, or the Empty entry as fallback.</summary>
    public static RoomBlueprint GetRoomCost(RoomType type) =>
        RoomCosts.TryGetValue(type, out var blueprint) ? blueprint : RoomCosts[RoomType.Empty];

    /// <summary>Demolish the room covering a tile, refunding part of its cost.</summary>
    public bool RemoveRoom(Vector3I tile)
    {
        var room = GetRoom(tile);
        if (room == null) return false;

        _rooms.Remove(room.GridPosition);
        foreach (var covered in room.GetTiles())
            _occupancy.Remove(covered);

        if (GameStateManager.Instance != null)
        {
            double refund = GetRoomCost(room.Type).Cash * DemolitionRefundRate;
            GameStateManager.Instance.Cash += refund;
        }

        RecalculateSynergies();

        EmitSignal(SignalName.OnRoomRemoved,
            room.GridPosition.X, room.GridPosition.Y, room.GridPosition.Z, room.RoomName);

        GD.Print($"[VenueBuilding] Demolished {room.RoomName} at {room.GridPosition}");
        return true;
    }

    /// <summary>Relocate a room, including across floors. Rolls back on failure.</summary>
    public bool MoveRoom(Vector3I from, Vector3I to)
    {
        var room = GetRoom(from);
        if (room == null) return false;

        var origin = room.GridPosition;

        // Lift the room out so it does not collide with itself.
        _rooms.Remove(origin);
        foreach (var tile in room.GetTiles())
            _occupancy.Remove(tile);

        room.GridPosition = to;

        if (!CanPlaceRoom(room, out string reason))
        {
            room.GridPosition = origin;
            _rooms[origin] = room;
            foreach (var tile in room.GetTiles())
                _occupancy[tile] = room;
            Reject(reason);
            return false;
        }

        _rooms[to] = room;
        foreach (var tile in room.GetTiles())
            _occupancy[tile] = room;

        RecalculateSynergies();
        GD.Print($"[VenueBuilding] Moved {room.RoomName} from {origin} to {to}");
        return true;
    }

    // ── Furniture ──────────────────────────────────────────────────────

    /// <summary>
    /// Place a piece of furniture in the room covering a tile. Rejected if
    /// the room's area-derived furniture budget is exhausted.
    /// </summary>
    public bool AddFurniture(Vector3I tile, FurnitureItem item)
    {
        if (item == null) { Reject("Furniture item is null."); return false; }

        var room = GetRoom(tile);
        if (room == null) { Reject($"No room at {tile}."); return false; }

        if (item.Area > room.FreeFurnitureArea)
        {
            Reject($"'{room.RoomName}' has {room.FreeFurnitureArea}/{room.FurnitureCapacity} " +
                   $"furniture slots free; '{item.ItemName}' needs {item.Area}.");
            return false;
        }

        room.Furniture.Add(item);
        RefreshAppointment(room);
        return true;
    }

    /// <summary>Remove a piece of furniture by index from the room at a tile.</summary>
    public bool RemoveFurniture(Vector3I tile, int index)
    {
        var room = GetRoom(tile);
        if (room == null) return false;
        if (index < 0 || index >= room.Furniture.Count) return false;

        room.Furniture.RemoveAt(index);
        RefreshAppointment(room);
        return true;
    }

    /// <summary>Strip a room back to bare walls.</summary>
    public bool ClearFurniture(Vector3I tile)
    {
        var room = GetRoom(tile);
        if (room == null) return false;

        room.Furniture.Clear();
        RefreshAppointment(room);
        return true;
    }

    /// <summary>Total nightly furniture upkeep across the whole building.</summary>
    public double GetNightlyUpkeep() => _rooms.Values.Sum(r => r.GetFurnitureUpkeep());

    /// <summary>
    /// Age the furniture in one room after a booking, then refresh its score.
    /// </summary>
    public void ApplyRoomUsage(Vector3I tile, float usage)
    {
        var room = GetRoom(tile);
        if (room == null) return;

        room.WearFurniture(usage);
        RefreshAppointment(room);
    }

    /// <summary>
    /// Refurbish every piece in the house by <paramref name="amount"/> points
    /// of condition. This is what the nightly maintenance charge actually
    /// buys — without it, wear is a one-way ratchet the player cannot answer
    /// and every house eventually decays to unusable.
    /// </summary>
    /// <returns>Number of pieces that were below full condition.</returns>
    public int MaintainAllFurniture(float amount)
    {
        if (amount <= 0f) return 0;

        var repaired = 0;

        foreach (var room in _rooms.Values)
        {
            var touched = false;

            foreach (var item in room.Furniture)
            {
                if (item == null || item.Condition >= 100f) continue;

                item.Repair(amount);
                repaired++;
                touched = true;
            }

            if (touched) RefreshAppointment(room);
        }

        return repaired;
    }

    private void RefreshAppointment(RoomModule room)
    {
        room.AppointmentScore = RoomAppointmentCalculator.Calculate(room);

        EmitSignal(SignalName.OnRoomAppointmentChanged,
            room.GridPosition.X, room.GridPosition.Y, room.GridPosition.Z,
            room.RoomName, room.AppointmentScore);
    }

    // ── Appointment Queries ────────────────────────────────────────────

    /// <summary>Appointment score, 0–100, of the room covering a tile.</summary>
    public float GetRoomAppointment(Vector3I tile)
    {
        var room = GetRoom(tile);
        return room == null ? 0f : RoomAppointmentCalculator.Calculate(room);
    }

    /// <summary>Component breakdown of a room's Appointment, for UI and debug.</summary>
    public AppointmentBreakdown GetAppointmentBreakdown(Vector3I tile) =>
        RoomAppointmentCalculator.GetBreakdown(GetRoom(tile));

    /// <summary>
    /// The dominant furniture style of the room covering a tile, or null if
    /// the room is unfurnished.
    /// </summary>
    public FurnitureStyle? GetRoomStyle(Vector3I tile)
    {
        var room = GetRoom(tile);
        if (room == null || room.Furniture.Count == 0) return null;
        return RoomAppointmentCalculator.GetBreakdown(room).DominantStyle;
    }

    /// <summary>Mean Appointment across every bookable room. 0 if there are none.</summary>
    public float GetAverageAppointment()
    {
        var bookable = _rooms.Values.Where(r => r.IsBookable).ToList();
        if (bookable.Count == 0) return 0f;
        return Mathf.Clamp(bookable.Average(r => RoomAppointmentCalculator.Calculate(r)), 0f, 100f);
    }

    /// <summary>
    /// Best room for a client, scored on Appointment against their
    /// expectation plus a preference bonus for the room type they wanted.
    /// Basement rooms are never offered — they earn nothing.
    /// </summary>
    public RoomModule GetBestRoomForClient(
        RoomType preferredType,
        float minAppointment = 0f,
        float minDiscretion = 0f)
    {
        RoomModule best = null;
        float bestScore = float.NegativeInfinity;

        foreach (var room in _rooms.Values)
        {
            if (!IsRevenueGenerating(room)) continue;

            float appointment = RoomAppointmentCalculator.Calculate(room);
            if (appointment < minAppointment) continue;
            if (GetEffectiveDiscretion(room.GridPosition) < minDiscretion) continue;

            float score = appointment
                        + (room.Type == preferredType ? 15f : 0f)
                        + GetFloorPrestigeBonus(room.Floor);

            if (score > bestScore)
            {
                bestScore = score;
                best = room;
            }
        }

        return best;
    }

    // ── Floor Economics ────────────────────────────────────────────────

    /// <summary>
    /// Whether a room can take paying clients. Basements never can — that is
    /// the whole point of the floor.
    /// </summary>
    public bool IsRevenueGenerating(RoomModule room) =>
        room != null && room.Floor >= 0 && room.IsBookable;

    /// <summary>Prestige premium for being upstairs. Zero at ground and below.</summary>
    public float GetFloorPrestigeBonus(int floor) =>
        floor <= 0 ? 0f : Mathf.Clamp(floor * PrestigeBonusPerFloor, 0f, 100f);

    /// <summary>
    /// Liability accumulated in the basement, 0–100. Everything down there
    /// is indiscreet by default, and none of it earns a cent — it only
    /// matters when somebody comes looking.
    /// </summary>
    public float GetBasementExposure()
    {
        var basement = _rooms.Values.Where(r => r.Floor < 0).ToList();
        if (basement.Count == 0) return 0f;

        float exposure = basement.Sum(r => (100f - r.DiscretionRating) * 0.15f);
        return Mathf.Clamp(exposure, 0f, 100f);
    }

    /// <summary>
    /// How well the ground floor screens and upsells arrivals, 0–100.
    /// A well-appointed public floor converts walk-ins into bigger tickets;
    /// an empty one leaves money at the door.
    /// </summary>
    public float GetScreeningStrength()
    {
        var publicRooms = _rooms.Values
            .Where(r => r.Floor == 0 &&
                        r.Type is RoomType.Lounge or RoomType.Bar or RoomType.VIPEntrance)
            .ToList();

        if (publicRooms.Count == 0) return 0f;

        float appointment = publicRooms.Average(r => RoomAppointmentCalculator.Calculate(r));
        float breadth = Mathf.Min(publicRooms.Count, 3) / 3f;

        return Mathf.Clamp(appointment * (0.6f + 0.4f * breadth), 0f, 100f);
    }

    /// <summary>Ticket multiplier the ground floor earns through upselling.</summary>
    public float GetUpsellMultiplier() =>
        1f + GetScreeningStrength() / 100f * 0.25f;

    /// <summary>
    /// Suggested nightly price for a room: base price scaled by Appointment
    /// against the neutral midpoint, plus the height premium. This is the
    /// furniture → luxury → price spine in one line.
    /// </summary>
    public double GetSuggestedPrice(RoomModule room, double basePrice)
    {
        if (room == null) return basePrice;

        float appointment = RoomAppointmentCalculator.Calculate(room);
        float appointmentTerm = (appointment - 50f) / 50f * AppointmentPriceInfluence;
        float floorTerm = GetFloorPrestigeBonus(room.Floor) / 100f;
        float luxuryTerm = (GetEffectiveLuxury(room.GridPosition) - 50f) / 100f * 0.2f;

        double multiplier = Math.Max(0.25, 1.0 + appointmentTerm + floorTerm + luxuryTerm);
        return Math.Round(basePrice * multiplier * GetUpsellMultiplier(), 2);
    }

    // ── Adjacency ──────────────────────────────────────────────────────

    /// <summary>In-bounds tiles orthogonally (and optionally diagonally) adjacent.</summary>
    public List<Vector3I> GetNeighbourTiles(Vector3I tile)
    {
        var result = new List<Vector3I>(8);

        var offsets = new List<Vector2I>
        {
            new(0, -1), new(0, 1), new(1, 0), new(-1, 0)
        };

        if (UseDiagonalAdjacency)
        {
            offsets.Add(new Vector2I(-1, -1));
            offsets.Add(new Vector2I(1, -1));
            offsets.Add(new Vector2I(-1, 1));
            offsets.Add(new Vector2I(1, 1));
        }

        foreach (var offset in offsets)
        {
            var neighbour = new Vector3I(tile.X + offset.X, tile.Y + offset.Y, tile.Z);
            if (IsInBounds(neighbour)) result.Add(neighbour);
        }

        return result;
    }

    /// <summary>Distinct rooms sharing a wall with this one on the same floor.</summary>
    public List<RoomModule> GetHorizontalNeighbours(RoomModule room)
    {
        var found = new List<RoomModule>();
        if (room == null) return found;

        foreach (var tile in room.GetTiles())
        foreach (var neighbourTile in GetNeighbourTiles(tile))
        {
            if (!_occupancy.TryGetValue(neighbourTile, out var other)) continue;
            if (ReferenceEquals(other, room)) continue;
            if (!found.Contains(other)) found.Add(other);
        }

        return found;
    }

    /// <summary>Distinct rooms directly underneath this one's footprint.</summary>
    public List<RoomModule> GetRoomsBelow(RoomModule room) => GetVerticalOverlap(room, -1);

    /// <summary>Distinct rooms directly above this one's footprint.</summary>
    public List<RoomModule> GetRoomsAbove(RoomModule room) => GetVerticalOverlap(room, +1);

    private List<RoomModule> GetVerticalOverlap(RoomModule room, int floorDelta)
    {
        var found = new List<RoomModule>();
        if (room == null) return found;

        foreach (var tile in room.GetTiles())
        {
            var probe = new Vector3I(tile.X, tile.Y, tile.Z + floorDelta);
            if (!_occupancy.TryGetValue(probe, out var other)) continue;
            if (ReferenceEquals(other, room)) continue;
            if (!found.Contains(other)) found.Add(other);
        }

        return found;
    }

    /// <summary>Occupied neighbours of a tile's room, same floor only.</summary>
    public List<RoomModule> GetOccupiedNeighbors(Vector3I tile) =>
        GetHorizontalNeighbours(GetRoom(tile));

    // ── Synergy Calculation ────────────────────────────────────────────

    /// <summary>
    /// Recalculate every spatial interaction in the building: horizontal
    /// adjacency, downward noise bleed, and upward scrutiny leak. Also
    /// refreshes each room's cached Appointment score.
    ///
    /// This is the entry point <c>MasterGameLoop</c> calls at the top of the
    /// daily tick, before any system reads post-synergy room stats.
    /// </summary>
    public void RecalculateSynergies()
    {
        foreach (var room in _rooms.Values)
        {
            room.HeatModifier = 0f;
            room.SatisfactionModifier = 0f;
            room.RevenueModifier = 0f;
            room.NoiseExposure = 0f;
            room.ScrutinyExposure = 0f;
            room.SoundproofingBonus = 0f;
            room.AppointmentScore = RoomAppointmentCalculator.Calculate(room);
        }

        _synergies.Clear();

        // Soundproofing cells must be resolved before any noise is applied,
        // otherwise whether proofing works depends on dictionary iteration order.
        ApplySoundproofingCells();

        foreach (var room in _rooms.Values)
        {
            ApplyFloorIdentity(room);
            EvaluateHorizontal(room);
            EvaluateNoiseDownward(room);
            EvaluateScrutinyUpward(room);
        }

        EmitSignal(SignalName.OnSynergiesUpdated, _synergies.Count);

        GD.Print($"[VenueBuilding] Synergies recalculated: {_synergies.Count} across " +
                 $"{_rooms.Count} rooms on {_floors.Count} floors.");
    }

    /// <summary>
    /// Structural pre-pass: every Soundproofing cell lifts the effective
    /// soundproofing of the rooms it touches, on its own floor and directly
    /// above and below it. Buying proofing is the direct counter to a badly
    /// stacked floorplan.
    /// </summary>
    private void ApplySoundproofingCells()
    {
        foreach (var cell in _rooms.Values.Where(r => r.Type == RoomType.Soundproofing))
        {
            foreach (var neighbour in GetHorizontalNeighbours(cell))
                neighbour.SoundproofingBonus += SoundproofingCellBonus;

            foreach (var neighbour in GetRoomsAbove(cell))
                neighbour.SoundproofingBonus += SoundproofingCellBonus * 0.5f;

            foreach (var neighbour in GetRoomsBelow(cell))
                neighbour.SoundproofingBonus += SoundproofingCellBonus * 0.5f;
        }
    }

    /// <summary>Soundproofing points a proofing cell grants each neighbour.</summary>
    public float SoundproofingCellBonus { get; set; } = 35f;

    /// <summary>Apply the flat consequences of which floor a room sits on.</summary>
    private void ApplyFloorIdentity(RoomModule room)
    {
        switch (GetFloorKind(room.Floor))
        {
            case FloorKind.Basement:
                // Earns nothing, and what is down there is a liability.
                room.RevenueModifier = -1f;
                room.HeatModifier += (100f - room.DiscretionRating) / 100f * 0.1f;
                break;

            case FloorKind.Ground:
                // The public face: seen from the street, but where upselling happens.
                room.HeatModifier += room.StreetVisibility / 100f * 0.12f;
                break;

            case FloorKind.Upper:
                float bonus = GetFloorPrestigeBonus(room.Floor);
                room.RevenueModifier += bonus / 100f;

                _synergies.Add(new AdjacencySynergy
                {
                    RoomA = room.GridPosition,
                    RoomB = room.GridPosition,
                    Kind = SynergyKind.FloorPrestige,
                    Description = $"{room.RoomName} sits on floor {room.Floor} — height premium",
                    EffectValue = bonus / 100f
                });
                break;
        }
    }

    /// <summary>Same-floor interactions: noise bleed, security cover, ambiance.</summary>
    private void EvaluateHorizontal(RoomModule room)
    {
        foreach (var neighbour in GetHorizontalNeighbours(room))
        {
            if (room.IsNoisy && neighbour.IsNoiseSensitive)
                ApplyNoise(room, neighbour, 1f, "beside");

            if (room.Type == RoomType.Security &&
                neighbour.Type != RoomType.Security &&
                neighbour.Type != RoomType.Empty)
                ApplySecurityCover(room, neighbour, 1f);
        }
    }

    /// <summary>
    /// Noise falls. A noisy room bleeds into every quiet room directly
    /// beneath its footprint, attenuated by the slab and by the quiet room's
    /// own soundproofing.
    /// </summary>
    private void EvaluateNoiseDownward(RoomModule room)
    {
        if (!room.IsNoisy) return;

        foreach (var below in GetRoomsBelow(room))
        {
            if (!below.IsNoiseSensitive) continue;
            ApplyNoise(room, below, VerticalNoiseFactor, "above");
        }
    }

    /// <summary>
    /// Scrutiny rises. Whatever the street can see on one floor drags the
    /// floor above it into the same investigation.
    /// </summary>
    private void EvaluateScrutinyUpward(RoomModule room)
    {
        if (room.StreetVisibility <= 0f) return;

        foreach (var above in GetRoomsAbove(room))
        {
            float leak = room.StreetVisibility * ScrutinyPropagation;

            above.ScrutinyExposure = Mathf.Clamp(above.ScrutinyExposure + leak, 0f, 100f);
            above.HeatModifier += leak / 100f * 0.2f;

            _synergies.Add(new AdjacencySynergy
            {
                RoomA = room.GridPosition,
                RoomB = above.GridPosition,
                Kind = SynergyKind.Scrutiny,
                Description = $"{room.RoomName} is visible from the street — " +
                              $"scrutiny rises into {above.RoomName}",
                EffectValue = leak / 100f * 0.2f
            });
        }
    }

    /// <summary>
    /// Apply a noise penalty from a loud room to a quiet one. The quiet
    /// room's Soundproofing is what mitigates it, so proofing spend is the
    /// direct counter to a badly stacked floorplan.
    /// </summary>
    private void ApplyNoise(RoomModule noisy, RoomModule quiet, float transmission, string relation)
    {
        float noiseScale = Mathf.Clamp(
            Mathf.Max(noisy.NoiseLevel, 40f) / 100f, 0f, 1f);

        float rawPenalty = NoisyNeighbourPenalty * transmission * (0.5f + noiseScale);
        float mitigation = Mathf.Clamp(
            (quiet.EffectiveSoundproofing / 100f) * SoundproofingMitigation, 0f, 1f);
        float effectivePenalty = rawPenalty * (1f - mitigation);

        quiet.SatisfactionModifier -= effectivePenalty;
        quiet.NoiseExposure = Mathf.Clamp(
            quiet.NoiseExposure + effectivePenalty * 100f, 0f, 100f);

        _synergies.Add(new AdjacencySynergy
        {
            RoomA = noisy.GridPosition,
            RoomB = quiet.GridPosition,
            Kind = SynergyKind.Noise,
            Description = $"{noisy.RoomName} ({noisy.Type}) {relation} disturbs " +
                          $"{quiet.RoomName} ({quiet.Type})",
            EffectValue = -effectivePenalty
        });
    }

    private void ApplySecurityCover(RoomModule security, RoomModule other, float strength)
    {
        float reduction = SecurityHeatReduction * strength;
        other.HeatModifier -= reduction;

        _synergies.Add(new AdjacencySynergy
        {
            RoomA = security.GridPosition,
            RoomB = other.GridPosition,
            Kind = SynergyKind.Security,
            Description = $"{security.RoomName} (Security) covers {other.RoomName} ({other.Type})",
            EffectValue = -reduction
        });
    }

    // ── Synergy Queries ────────────────────────────────────────────────

    /// <summary>All synergies touching a given room origin.</summary>
    public List<AdjacencySynergy> GetSynergiesForRoom(Vector3I origin) =>
        _synergies.Where(s => s.RoomA == origin || s.RoomB == origin).ToList();

    /// <summary>
    /// Effective luxury of the room covering a tile, including the ambiance
    /// lift from luxurious neighbours, its Appointment, and floor prestige.
    /// </summary>
    public float GetEffectiveLuxury(Vector3I tile)
    {
        var room = GetRoom(tile);
        if (room == null) return 0f;

        float effective = room.LuxuryScore;

        foreach (var neighbour in GetHorizontalNeighbours(room))
            if (neighbour.LuxuryScore > 50f)
                effective += neighbour.LuxuryScore * 0.05f;

        // Furnishing is the dominant lever — a bare shell is not luxurious
        // no matter what the blueprint claimed.
        effective += (RoomAppointmentCalculator.Calculate(room) - 50f) * 0.3f;
        effective += GetFloorPrestigeBonus(room.Floor) * 0.5f;

        return Mathf.Clamp(effective, 0f, 100f);
    }

    /// <summary>
    /// Effective discretion of the room covering a tile. Security cover and
    /// depth help; street visibility and inherited scrutiny hurt.
    /// </summary>
    public float GetEffectiveDiscretion(Vector3I tile)
    {
        var room = GetRoom(tile);
        if (room == null) return 0f;

        float effective = room.DiscretionRating;

        foreach (var neighbour in GetHorizontalNeighbours(room))
            if (neighbour.Type == RoomType.Security)
                effective += 10f;

        effective -= room.ScrutinyExposure * 0.25f;
        effective -= room.StreetVisibility * 0.15f;

        // Height and depth both buy privacy from the pavement.
        if (room.Floor > 0) effective += room.Floor * 4f;
        if (room.Floor < 0) effective += 8f;

        return Mathf.Clamp(effective, 0f, 100f);
    }

    // ── Layout Helpers ─────────────────────────────────────────────────

    /// <summary>Unoccupied tiles on a floor.</summary>
    public List<Vector3I> GetEmptyTiles(int floor)
    {
        var empty = new List<Vector3I>();
        if (!HasFloor(floor)) return empty;

        for (int x = 0; x < FloorWidth; x++)
        for (int y = 0; y < FloorDepth; y++)
        {
            var tile = new Vector3I(x, y, floor);
            if (!_occupancy.ContainsKey(tile)) empty.Add(tile);
        }

        return empty;
    }

    /// <summary>Room counts by type across the whole building.</summary>
    public Dictionary<RoomType, int> GetRoomTypeCounts()
    {
        var counts = new Dictionary<RoomType, int>();
        foreach (var room in _rooms.Values)
        {
            counts.TryGetValue(room.Type, out int count);
            counts[room.Type] = count + 1;
        }
        return counts;
    }

    /// <summary>Number of rooms that can currently take paying clients.</summary>
    public int GetBookableRoomCount() => _rooms.Values.Count(IsRevenueGenerating);

    /// <summary>Console rendering of one floor's plan.</summary>
    public string GetFloorDebugString(int floor)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Floor {floor} ({GetFloorKind(floor)}) — {FloorWidth}×{FloorDepth}:");

        if (!HasFloor(floor))
        {
            sb.AppendLine("  (not built)");
            return sb.ToString();
        }

        for (int y = 0; y < FloorDepth; y++)
        {
            for (int x = 0; x < FloorWidth; x++)
            {
                var room = GetRoom(new Vector3I(x, y, floor));
                sb.Append(room == null ? "[ . ]" : $"[{ShortLabel(room.Type)}]");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Console rendering of every built floor, top down.</summary>
    public string GetBuildingDebugString()
    {
        var sb = new System.Text.StringBuilder();
        foreach (int floor in _floors.Reverse())
            sb.Append(GetFloorDebugString(floor));
        return sb.ToString();
    }

    private static string ShortLabel(RoomType type) => type switch
    {
        RoomType.Lounge => "LNG",
        RoomType.VIPSuite => "VIP",
        RoomType.PrivateSuite => "SUI",
        RoomType.Security => "SEC",
        RoomType.Service => "SRV",
        RoomType.Bar => "BAR",
        RoomType.Medical => "MED",
        RoomType.Storage => "STO",
        RoomType.VIPEntrance => "ENT",
        RoomType.Soundproofing => "SND",
        _ => "???"
    };

    private void Reject(string reason)
    {
        GD.PrintErr($"[VenueBuilding] {reason}");
        EmitSignal(SignalName.OnBuildRejected, reason);
    }

    // ── Save / Load ────────────────────────────────────────────────────

    public string SaveKey => "venue";

    /// <summary>
    /// Capture the whole floorplan — floors, rooms, footprints, and every
    /// piece of furniture with its wear state.
    /// </summary>
    public JsonObject CaptureState()
    {
        var floors = new JsonArray();
        foreach (int floor in _floors)
            floors.Add(JsonValue.Create(floor));

        var rooms = new JsonArray();
        foreach (var room in _rooms.Values)
            rooms.Add(SerializeRoom(room));

        return new JsonObject
        {
            ["floorWidth"] = JsonValue.Create(FloorWidth),
            ["floorDepth"] = JsonValue.Create(FloorDepth),
            ["floors"] = floors,
            ["rooms"] = rooms
        };
    }

    private static JsonObject SerializeRoom(RoomModule room)
    {
        var furniture = new JsonArray();
        foreach (var item in room.Furniture)
        {
            if (item == null) continue;
            furniture.Add(new JsonObject
            {
                ["name"] = JsonValue.Create(item.ItemName),
                ["category"] = JsonValue.Create((int)item.Category),
                ["style"] = JsonValue.Create((int)item.StyleTag),
                ["tier"] = JsonValue.Create(item.Tier),
                ["comfort"] = JsonValue.Create(item.Comfort),
                ["prestige"] = JsonValue.Create(item.Prestige),
                ["upkeep"] = JsonValue.Create(item.Upkeep),
                ["price"] = JsonValue.Create(item.PurchasePrice),
                ["condition"] = JsonValue.Create(item.Condition),
                ["footprintX"] = JsonValue.Create(item.Footprint.X),
                ["footprintY"] = JsonValue.Create(item.Footprint.Y)
            });
        }

        return new JsonObject
        {
            ["name"] = JsonValue.Create(room.RoomName),
            ["type"] = JsonValue.Create((int)room.Type),
            ["x"] = JsonValue.Create(room.GridPosition.X),
            ["y"] = JsonValue.Create(room.GridPosition.Y),
            ["floor"] = JsonValue.Create(room.GridPosition.Z),
            ["sizeX"] = JsonValue.Create(room.Size.X),
            ["sizeY"] = JsonValue.Create(room.Size.Y),
            ["luxury"] = JsonValue.Create(room.LuxuryScore),
            ["soundproofing"] = JsonValue.Create(room.Soundproofing),
            ["discretion"] = JsonValue.Create(room.DiscretionRating),
            ["noise"] = JsonValue.Create(room.NoiseLevel),
            ["streetVisibility"] = JsonValue.Create(room.StreetVisibility),
            ["furniture"] = furniture
        };
    }

    /// <summary>
    /// Restore a floorplan. Tolerates missing keys throughout — a save from
    /// a build that predated furniture simply restores unfurnished rooms.
    /// </summary>
    public void RestoreState(JsonObject state)
    {
        if (state == null) return;

        _rooms.Clear();
        _occupancy.Clear();
        _synergies.Clear();

        FloorWidth = Mathf.Max(1, ReadInt(state, "floorWidth", FloorWidth));
        FloorDepth = Mathf.Max(1, ReadInt(state, "floorDepth", FloorDepth));

        _floors.Clear();
        if (state["floors"] is JsonArray floorArray)
        {
            foreach (var node in floorArray)
            {
                int floor = ToInt(node, int.MinValue);
                if (floor >= MinPossibleFloor && floor <= MaxPossibleFloor)
                    _floors.Add(floor);
            }
        }

        if (_floors.Count == 0) ResetToStartingFloors();

        int restored = 0;
        if (state["rooms"] is JsonArray roomArray)
        {
            foreach (var node in roomArray)
            {
                if (node is not JsonObject roomObject) continue;
                var room = DeserializeRoom(roomObject);
                if (room == null) continue;

                // Ensure the floor exists even if the floors array was stale.
                _floors.Add(Mathf.Clamp(room.GridPosition.Z, MinPossibleFloor, MaxPossibleFloor));

                if (!CanPlaceRoom(room, out string reason))
                {
                    GD.PrintErr($"[VenueBuilding] Skipped '{room.RoomName}' on load: {reason}");
                    continue;
                }

                _rooms[room.GridPosition] = room;
                foreach (var tile in room.GetTiles())
                    _occupancy[tile] = room;
                restored++;
            }
        }

        RecalculateSynergies();

        GD.Print($"[VenueBuilding] Restored: {restored} rooms across {_floors.Count} floors " +
                 $"({LowestFloor}..{HighestFloor}), upkeep ${GetNightlyUpkeep():F2}/night.");
    }

    private static RoomModule DeserializeRoom(JsonObject o)
    {
        var room = new RoomModule
        {
            RoomName = ReadString(o, "name", "Room"),
            Type = (RoomType)Mathf.Clamp(
                ReadInt(o, "type", (int)RoomType.Empty),
                0, Enum.GetValues<RoomType>().Length - 1),
            GridPosition = new Vector3I(
                ReadInt(o, "x", 0),
                ReadInt(o, "y", 0),
                Mathf.Clamp(ReadInt(o, "floor", 0), MinPossibleFloor, MaxPossibleFloor)),
            Size = new Vector2I(
                Mathf.Max(1, ReadInt(o, "sizeX", 1)),
                Mathf.Max(1, ReadInt(o, "sizeY", 1)))
        };

        var fallback = GetRoomCost(room.Type);
        room.LuxuryScore = ReadFloat(o, "luxury", fallback.Luxury);
        room.Soundproofing = ReadFloat(o, "soundproofing", fallback.Soundproofing);
        room.DiscretionRating = ReadFloat(o, "discretion", fallback.Discretion);
        room.NoiseLevel = ReadFloat(o, "noise", Mathf.Max(0f, fallback.Noise));
        room.StreetVisibility = ReadFloat(o, "streetVisibility", fallback.StreetVisibility);

        if (o["furniture"] is JsonArray furnitureArray)
        {
            foreach (var node in furnitureArray)
            {
                if (node is not JsonObject f) continue;
                room.Furniture.Add(DeserializeFurniture(f));
            }
        }

        room.AppointmentScore = RoomAppointmentCalculator.Calculate(room);
        return room;
    }

    private static FurnitureItem DeserializeFurniture(JsonObject o)
    {
        int categoryCount = Enum.GetValues<FurnitureCategory>().Length;
        int styleCount = Enum.GetValues<FurnitureStyle>().Length;

        return new FurnitureItem
        {
            ItemName = ReadString(o, "name", "Furnishing"),
            Category = (FurnitureCategory)Mathf.Clamp(ReadInt(o, "category", 0), 0, categoryCount - 1),
            StyleTag = (FurnitureStyle)Mathf.Clamp(ReadInt(o, "style", 0), 0, styleCount - 1),
            Tier = ReadInt(o, "tier", 1),
            Comfort = ReadFloat(o, "comfort", 30f),
            Prestige = ReadFloat(o, "prestige", 30f),
            Upkeep = ReadDouble(o, "upkeep", 1.0),
            PurchasePrice = ReadDouble(o, "price", 0.0),
            Condition = ReadFloat(o, "condition", 100f),
            Footprint = new Vector2I(
                Mathf.Max(1, ReadInt(o, "footprintX", 1)),
                Mathf.Max(1, ReadInt(o, "footprintY", 1)))
        };
    }

    // ── Tolerant JSON readers ──────────────────────────────────────────
    // A save written by an older build is a normal case, not an error.

    private static int ToInt(JsonNode node, int fallback)
    {
        if (node == null) return fallback;
        try { return (int?)node ?? fallback; }
        catch (Exception) { return fallback; }
    }

    private static int ReadInt(JsonObject o, string key, int fallback) =>
        ToInt(o?[key], fallback);

    private static float ReadFloat(JsonObject o, string key, float fallback)
    {
        var node = o?[key];
        if (node == null) return fallback;
        try { return (float?)node ?? fallback; }
        catch (Exception) { return fallback; }
    }

    private static double ReadDouble(JsonObject o, string key, double fallback)
    {
        var node = o?[key];
        if (node == null) return fallback;
        try { return (double?)node ?? fallback; }
        catch (Exception) { return fallback; }
    }

    private static string ReadString(JsonObject o, string key, string fallback)
    {
        var node = o?[key];
        if (node == null) return fallback;
        try { return (string)node ?? fallback; }
        catch (Exception) { return fallback; }
    }

    public override string ToString() =>
        $"[VenueBuilding] {_rooms.Count} rooms across {_floors.Count} floors " +
        $"({LowestFloor}..{HighestFloor}), {_synergies.Count} synergies, " +
        $"avg appointment {GetAverageAppointment():F0}";
}
