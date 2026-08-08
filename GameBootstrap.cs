using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Instantiates and names every gameplay system, then seeds a starting house.
///
/// This exists because nothing in the project was ever assembled: the scene
/// tree held two autoloads and a bare test scene, so ~90 well-written systems
/// sat inert with no instance and no wiring. Everything here reaches its
/// collaborators through <c>FindChild(name)</c>, so the node *names* below
/// are load-bearing — they must match the strings the systems look up.
///
/// Add as the root script of the main scene, or instance it anywhere in the
/// tree. Autoloads (GameStateManager, StaffRoster) are not created here.
/// </summary>
public partial class GameBootstrap : Node
{
    /// <summary>Fired once every system is constructed and the house is seeded.</summary>
    [Signal]
    public delegate void OnWorldReadyEventHandler();

    /// <summary>Seed a starter venue and roster. Disable to boot into an empty world.</summary>
    [Export] public bool SeedNewGame { get; set; } = true;

    /// <summary>
    /// Seed the whole five-room house instead of just the entrance.
    ///
    /// A new campaign starts with the entrance only and the player builds the
    /// rest — that is the game. The harness and the smoke test are measuring
    /// the economy and the wiring, not the opening, and neither of them ever
    /// builds a room, so they would run a house with nothing to sell. They
    /// ask for the established house explicitly.
    /// </summary>
    [Export] public bool SeedEstablishedHouse { get; set; }

    /// <summary>
    /// Let GameStateManager's legacy 60-second timer keep running. Off by
    /// default: NightDirector advances the day when the Ledger is closed, so
    /// a wall-clock timer would double-tick the world.
    /// </summary>
    [Export] public bool UseLegacyDayTimer { get; set; }

    /// <summary>Deterministic seed for the starting house. Zero randomizes.</summary>
    [Export] public ulong WorldSeed { get; set; }

    // ── System handles ─────────────────────────────────────────────────

    public VenueBuilding Venue { get; private set; }
    public NightDirector Night { get; private set; }
    public FinancialLedger Ledger { get; private set; }
    public HeatSystem Heat { get; private set; }
    public MasterGameLoop Loop { get; private set; }
    public RecruitmentService Recruitment { get; private set; }
    public SaveLoadSystem SaveLoad { get; private set; }
    public PsychologicalBreakSystem BreakSystem { get; private set; }
    public PolicyTreeManager Policies { get; private set; }
    public MacroEconomyEngine Macro { get; private set; }
    public FurnitureCatalog Catalog { get; private set; }
    public CharacterLibrary Characters { get; private set; }
    public RegularsRegistry Regulars { get; private set; }
    public FacilityLicences Licences { get; private set; }
    public UnionizationManager Union { get; private set; }
    public CrisisNarrativeDirector Crises { get; private set; }

    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        // Before BuildSystems, because each system seeds itself in its own
        // _Ready and AddChild runs that immediately. Setting the seed after
        // construction would leave every system already randomized.
        WorldRandom.Initialize(WorldSeed);
        WorldRandom.Seed(_rng, nameof(GameBootstrap));

        BuildSystems();

        // Deferred so every system's _Ready has run and its own lookups have
        // resolved before we start placing rooms and hiring people.
        CallDeferred(nameof(SeedWorld));
    }

    /// <summary>
    /// Construct each system as a named child. Order is not significant —
    /// systems resolve each other lazily via FindChild — but salaries and
    /// heat both read the ledger, so it is created early for clarity.
    /// </summary>
    private void BuildSystems()
    {
        Ledger = Add<FinancialLedger>("FinancialLedger");
        Macro = Add<MacroEconomyEngine>("MacroEconomyEngine");
        Policies = Add<PolicyTreeManager>("PolicyTreeManager");
        BreakSystem = Add<PsychologicalBreakSystem>("PsychologicalBreakSystem");
        Heat = Add<HeatSystem>("HeatSystem");

        Add<PoliticalInfluenceSystem>("PoliticalInfluenceSystem");
        Union = Add<UnionizationManager>("UnionizationManager");
        Add<BlackmailNetwork>("BlackmailNetwork");
        Add<SyndicateRivalAI>("SyndicateRivalAI");
        Add<NarrativeArcTracker>("NarrativeArcTracker");
        Crises = Add<CrisisNarrativeDirector>("CrisisNarrativeDirector");
        Regulars = Add<RegularsRegistry>("RegularsRegistry");
        Licences = Add<FacilityLicences>("FacilityLicences");
        Add<RealEstateMarket>("RealEstateMarket");

        Venue = Add<VenueBuilding>("VenueBuilding");
        Catalog = Add<FurnitureCatalog>("FurnitureCatalog");

        // Owns the parsed character models. Without it in the tree every pawn
        // silently falls back to a capsule, since the library is reached
        // through its singleton.
        Characters = Add<CharacterLibrary>("CharacterLibrary");
        Recruitment = Add<RecruitmentService>("RecruitmentService");
        Night = Add<NightDirector>("NightDirector");
        Loop = Add<MasterGameLoop>("MasterGameLoop");
        SaveLoad = Add<SaveLoadSystem>("SaveLoadSystem");

        if (!UseLegacyDayTimer)
        {
            // NightDirector.ConcludeNight() is the only thing that should
            // advance the day now.
            GameStateManager.Instance?.PauseTick();
        }

        GD.Print($"[Bootstrap] {GetChildCount()} systems constructed.");
    }

    private T Add<T>(string nodeName) where T : Node, new()
    {
        var node = new T { Name = nodeName };
        AddChild(node);
        return node;
    }

    // ── Seeding ────────────────────────────────────────────────────────

    private void SeedWorld()
    {
        if (SeedNewGame)
        {
            SeedVenue();
            SeedRoster();
            SeedDistricts();
        }

        Loop?.EnterManagement();
        EmitSignal(SignalName.OnWorldReady);

        GD.Print("[Bootstrap] World ready.\n" + GetWorldSummary());
    }

    /// <summary>
    /// Lay out a small starting house: a public ground floor, two furnished
    /// suites upstairs, and a security office in the basement. Deliberately
    /// modest — the player should feel the ceiling immediately.
    /// </summary>
    private void SeedVenue()
    {
        if (Venue == null) return;

        if (!SeedEstablishedHouse)
        {
            SeedEntrance();
            return;
        }

        // The founding house is inherited, not bought. BuildRoom charges the
        // blueprint cost and would reject most of this against the $1,000
        // opening balance — that money is working capital for the first few
        // nights, not a construction budget. Grant the rooms directly.
        // Floors are 6 wide by 5 deep and rooms have real footprints, so
        // these origins are chosen to tile without overlapping:
        // Lounge and Bar are both 3×2, suites are 2×2, Security is 2×1.

        // Ground floor — screening and upsell. Lounge x0–2, Bar x3–5.
        var lounge = GrantRoom(RoomType.Lounge, new Vector3I(0, 0, 0), "Front Lounge");
        var bar = GrantRoom(RoomType.Bar, new Vector3I(3, 0, 0), "The Long Bar");

        // First floor — where the money is made. Suites are 3×2, so Rose
        // takes x0–2 and Jade x3–5; at the old 2×2 footprint Jade sat at x2
        // and would now overlap.
        var rose = GrantRoom(RoomType.PrivateSuite, new Vector3I(0, 0, 1), "The Rose Room");
        var jade = GrantRoom(RoomType.PrivateSuite, new Vector3I(3, 0, 1), "The Jade Room");

        // Basement — pure liability, earns nothing.
        GrantRoom(RoomType.Security, new Vector3I(0, 0, -1), "Watch Office");

        // Furnish the two suites in *different* coherent styles. This is the
        // design in miniature: each room reads as deliberate and scores the
        // coherence bonus, and between them they cover two client tastes.
        FurnishSuite(rose, FurnitureStyle.Baroque, tier: 2);
        FurnishSuite(jade, FurnitureStyle.Oriental, tier: 2);

        // The public floor earns nothing directly but sets what every client
        // will spend upstairs, via VenueBuilding.GetScreeningStrength and
        // GetUpsellMultiplier. Leaving it bare wastes that multiplier.
        FurnishPublicRoom(lounge, FurnitureStyle.Baroque, tier: 2);
        FurnishPublicRoom(bar, FurnitureStyle.ArtDeco, tier: 2);

        Venue.RecalculateSynergies();
    }

    /// <summary>
    /// A new campaign: the front door and nothing else.
    ///
    /// The house is a lease on a ground floor with a reception and a bar
    /// already in it. Everything that earns money — every suite, every
    /// upstairs floor — the player builds. That is the difference between
    /// inheriting a business and starting one, and it is what makes the
    /// opening decisions matter: the first suite is a real choice about where
    /// the money goes, not a room that was already there.
    ///
    /// The reception is what a client meets first. It sets screening and the
    /// upsell multiplier for everything that happens upstairs, so it is
    /// furnished rather than bare — the player is not asked to fix the front
    /// of house before they can trade at all.
    /// </summary>
    private void SeedEntrance()
    {
        var reception = GrantRoom(RoomType.Lounge, new Vector3I(0, 0, 0), "The Reception");
        var bar = GrantRoom(RoomType.Bar, new Vector3I(3, 0, 0), "The Long Bar");

        FurnishPublicRoom(reception, FurnitureStyle.Baroque, tier: 1);
        FurnishPublicRoom(bar, FurnitureStyle.Baroque, tier: 1);

        Venue.RecalculateSynergies();

        GD.Print("[Bootstrap] New house: reception and bar. " +
                 "Everything upstairs is the player's to build.");
    }

    /// <summary>Seating, light and decor for a client-facing ground-floor room.</summary>
    private void FurnishPublicRoom(RoomModule room, FurnitureStyle style, int tier)
    {
        if (room == null) return;

        var pieces = new (string Name, FurnitureCategory Category)[]
        {
            ($"{style} Settee", FurnitureCategory.Seating),
            ($"{style} Armchair", FurnitureCategory.Seating),
            ($"{style} Chandelier", FurnitureCategory.Lighting),
            ($"{style} Carpet", FurnitureCategory.Rug),
            ($"{style} Portrait", FurnitureCategory.Decor),
            (room.Type == RoomType.Bar ? "Zinc Counter" : $"{style} Console", FurnitureCategory.Bar)
        };

        foreach (var (name, category) in pieces)
            Venue.AddFurniture(room.GridPosition, FurnitureItem.Create(name, category, style, tier));
    }

    /// <summary>
    /// Register the city's districts with the property market.
    ///
    /// RealEstateMarket.RegisterDistrict had no callers anywhere, so the
    /// market was permanently empty and OwnedCount was always zero. That in
    /// turn made the syndicate war's "Underworld Dominance" ending — which
    /// requires holding three districts — unreachable in any real campaign.
    ///
    /// The first three names match the territories SyndicateRivalAI already
    /// assigns to its factions, so buying property is buying into somebody's
    /// turf rather than an unrelated menu.
    /// </summary>
    private void SeedDistricts()
    {
        var market = GetNodeOrNull<RealEstateMarket>("RealEstateMarket");
        if (market == null) return;

        var heat = GameStateManager.Instance?.Heat ?? 0f;

        market.RegisterDistrict("Harbor Wharfs", 9000, heat);
        market.RegisterDistrict("Gilded Heights", 22000, heat);
        market.RegisterDistrict("The Warrens", 6000, heat);
        market.RegisterDistrict("Lamplight Row", 12000, heat);
        market.RegisterDistrict("Old Cathedral Quarter", 16000, heat);

        GD.Print($"[Bootstrap] {market.Properties.Count} districts registered.");
    }

    /// <summary>
    /// Place a room without charging for it. Used only for the inherited
    /// starting house — every room the player adds afterward goes through
    /// <see cref="VenueBuilding.BuildRoom"/> and costs real money.
    /// </summary>
    private RoomModule GrantRoom(RoomType type, Vector3I position, string roomName)
    {
        var room = VenueBuilding.CreateRoom(type, position, roomName);

        if (!Venue.PlaceRoom(room))
        {
            GD.PrintErr($"[Bootstrap] Could not place {roomName} at {position}.");
            return null;
        }

        return room;
    }

    private void FurnishSuite(RoomModule room, FurnitureStyle style, int tier)
    {
        if (room == null) return;

        var pieces = new (string Name, FurnitureCategory Category)[]
        {
            ($"{style} Bed", FurnitureCategory.Bed),
            ($"{style} Armchair", FurnitureCategory.Seating),
            ($"{style} Lamp", FurnitureCategory.Lighting),
            ($"{style} Rug", FurnitureCategory.Rug),
            ($"{style} Mirror", FurnitureCategory.Vanity)
        };

        foreach (var (name, category) in pieces)
        {
            var item = FurnitureItem.Create(name, category, style, tier);
            Venue.AddFurniture(room.GridPosition, item);
        }
    }

    /// <summary>
    /// Hire the opening roster directly, bypassing RecruitmentService's cost
    /// and wait — these are the people the player starts the campaign owing
    /// nothing for. Every later hire goes through the real channels.
    /// </summary>
    private void SeedRoster()
    {
        var roster = StaffRoster.Instance;
        if (roster == null) return;

        AddFoundingStaff(roster, "Mireille Vance", StaffOrigin.OpenCall, StaffAmbition.Status,
            charisma: 58f, negotiation: 41f, discretion: 46f, loyalty: 55f,
            backstory: "Came up through the harbour houses. Wants her name on the door.");

        AddFoundingStaff(roster, "Odette Rousseau", StaffOrigin.Rescue, StaffAmbition.Freedom,
            charisma: 44f, negotiation: 33f, discretion: 61f, loyalty: 82f,
            trauma: 46f,
            backstory: "You opened a door for her once. She has not forgotten it.");

        AddFoundingStaff(roster, "Sable Kovač", StaffOrigin.Poached, StaffAmbition.Money,
            charisma: 62f, negotiation: 57f, discretion: 38f, loyalty: 28f,
            backstory: "Bought away from the Iron Circle. They noticed.");

        // Founding staff know each other — one bond, one rivalry, so the
        // social stress modifier has something to say from night one.
        var all = new List<StaffMember>(roster.GetAll());
        if (all.Count >= 3)
        {
            roster.AdjustRelationship(all[0].Id, all[1].Id, 55f);   // bonded
            roster.AdjustRelationship(all[0].Id, all[2].Id, -50f);  // rivals
        }
    }

    private void AddFoundingStaff(
        StaffRoster roster, string name, StaffOrigin origin, StaffAmbition ambition,
        float charisma, float negotiation, float discretion, float loyalty,
        string backstory, float trauma = 0f)
    {
        var staff = new StaffMember
        {
            StaffName = name,
            Role = "Companion",
            Backstory = backstory,
            Origin = origin,
            Ambition = ambition,
            Charisma = charisma,
            Negotiation = negotiation,
            Discretion = discretion,

            // Monthly retainer, amortized daily by the ledger and paid on top
            // of each night's commission. Set high enough that an idle roster
            // is a real drain — keeping people on the books has to cost
            // something, or there is no pressure to actually use them.
            Salary = 450
        };

        if (trauma > 0f) staff.Trauma = trauma;
        staff.RestoreLoyalty(loyalty);

        if (origin == StaffOrigin.Poached) staff.AssociatedFaction = "The Iron Circle";

        roster.Add(staff);
    }

    // ── Convenience ────────────────────────────────────────────────────

    /// <summary>
    /// Post every available staff member to a bookable room. Used by the
    /// headless smoke test and as the UI's "auto-assign" button.
    /// </summary>
    public int AutoAssignStaff()
    {
        var roster = StaffRoster.Instance;
        if (roster == null || Venue == null || Night == null) return 0;

        var rooms = new List<RoomModule>();
        foreach (var room in Venue.Rooms.Values)
            if (Venue.IsRevenueGenerating(room)) rooms.Add(room);

        var assigned = 0;
        var index = 0;

        foreach (var staff in roster.GetAll())
        {
            if (index >= rooms.Count) break;
            if (Night.AssignStaff(staff.Id, rooms[index].GridPosition)) assigned++;
            index++;
        }

        return assigned;
    }

    public string GetWorldSummary()
    {
        var roster = StaffRoster.Instance;
        var gsm = GameStateManager.Instance;

        return $"  Cash ${gsm?.Cash ?? 0:F0} | Rep {gsm?.Reputation ?? 0:F0} | " +
               $"Heat {gsm?.Heat ?? 0:F0} | Day {gsm?.DayCount ?? 0}\n" +
               $"  Rooms {Venue?.Rooms.Count ?? 0} across {Venue?.FloorCount ?? 0} floors, " +
               $"{Venue?.GetBookableRoomCount() ?? 0} bookable\n" +
               $"  Avg appointment {Venue?.GetAverageAppointment() ?? 0:F1} | " +
               $"nightly upkeep ${Venue?.GetNightlyUpkeep() ?? 0:F0}\n" +
               $"  Staff {roster?.Count ?? 0} | avg loyalty {roster?.GetAverageLoyalty() ?? 0:F0}";
    }
}
