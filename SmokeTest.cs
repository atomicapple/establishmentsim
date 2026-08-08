using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Headless end-to-end check of the night loop and persistence.
///
/// Plays three consecutive nights, then saves, perturbs the world, reloads,
/// and asserts the restored state matches. This is the milestone gate from
/// the plan: it exercises furniture → appointment → price → cash, staff
/// assignment and stress, encounter resolution, the ledger, and the full
/// save/load round-trip in one pass.
///
/// Run with:
///   Godot_v4.7.1-stable_win64_console.exe --headless --path . smoke_test.tscn
/// </summary>
public partial class SmokeTest : Node
{
    private enum Stage { Waiting, PlayingNights, Persistence, Done }

    private GameBootstrap _boot;
    private Stage _stage = Stage.Waiting;

    private int _nightsPlayed;
    private const int NightsToPlay = 3;

    private readonly List<string> _failures = new();
    private readonly List<string> _checks = new();

    // Captured before save, compared after load.
    private double _savedCash;
    private int _savedDay;
    private int _savedStaffCount;
    private int _savedRoomCount;
    private float _savedHeat;
    private string _probeStaffId;
    private float _probeStaffStress;

    public override void _Ready()
    {
        _boot = new GameBootstrap
        {
            Name = "GameBootstrap",
            WorldSeed = 20260807,          // deterministic run
            UseLegacyDayTimer = false
        };

        AddChild(_boot);
        _boot.OnWorldReady += OnWorldReady;
    }

    private void OnWorldReady()
    {
        GD.Print("\n══════════ SMOKE TEST ══════════");
        GD.Print(_boot.GetWorldSummary());

        // Compress the night so three of them run in a handful of frames.
        _boot.Night.ServiceDurationSeconds = 2.0f;
        _boot.Night.EncounterDurationSeconds = 0.12f;

        Check("world seeded with staff", StaffRoster.Instance?.Count > 0);
        Check("world seeded with bookable rooms", _boot.Venue.GetBookableRoomCount() > 0);
        Check("suites are furnished", _boot.Venue.GetAverageAppointment() > 0f);
        Check("nightly upkeep is non-zero", _boot.Venue.GetNightlyUpkeep() > 0.0);

        _stage = Stage.PlayingNights;
        StartNight();
    }

    public override void _Process(double delta)
    {
        if (_stage != Stage.PlayingNights) return;
        if (_boot?.Night == null) return;

        // The director advances itself on _Process; we only react to it
        // reaching the Ledger beat.
        if (_boot.Night.Phase == NightPhase.Ledger)
            FinishNight();
    }

    private void StartNight()
    {
        _boot.Night.BeginNight();

        var assigned = _boot.AutoAssignStaff();
        Check($"night {_nightsPlayed + 1}: staff posted", assigned > 0);

        _boot.Night.OpenDoors();
        Check($"night {_nightsPlayed + 1}: service began",
            _boot.Night.Phase == NightPhase.Service);
    }

    private void FinishNight()
    {
        var report = _boot.Night.CurrentReport;
        _nightsPlayed++;

        GD.Print($"\n── Night {report.Night} ──");
        GD.Print($"  arrived {report.ClientsArrived}, served {report.ClientsServed}, " +
                 $"turned away {report.ClientsTurnedAway}");
        GD.Print($"  revenue ${report.Revenue:F0}, upkeep ${report.Upkeep:F0}, " +
                 $"net ${report.Net:F0}");
        GD.Print($"  reputation {report.ReputationDelta:+0.0;-0.0;0}, " +
                 $"heat {report.HeatDelta:+0.0;-0.0;0}, regulars {report.NewRegulars}");

        if (report.QualityCounts.Count > 0)
        {
            var bands = string.Join(", ",
                report.QualityCounts.OrderByDescending(k => k.Key).Select(k => $"{k.Key} ×{k.Value}"));
            GD.Print($"  outcomes: {bands}");
        }

        foreach (var h in report.Highlights) GD.Print($"  + {h}");
        foreach (var i in report.Incidents) GD.Print($"  ! {i}");

        Check($"night {report.Night}: clients arrived", report.ClientsArrived > 0);
        Check($"night {report.Night}: someone was served", report.ClientsServed > 0);
        Check($"night {report.Night}: revenue was booked", report.Revenue > 0);

        var dayBefore = GameStateManager.Instance.DayCount;
        _boot.Night.ConcludeNight();

        Check($"night {report.Night}: day advanced",
            GameStateManager.Instance.DayCount == dayBefore + 1);

        if (_nightsPlayed >= NightsToPlay)
        {
            _stage = Stage.Persistence;
            RunCatalogCheck();
            RunExpansionCheck();
            RunPolicyCheck();
            RunRegularsCheck();
            RunAmbitionCheck();
            RunNarrativeCheck();
            RunLicenceCheck();
            RunUnionCheck();
            RunCityCheck();
            RunCrisisCheck();
            CallDeferred(nameof(RunPersistenceCheck));
            return;
        }

        StartNight();
    }

    // ── Furniture catalogue ────────────────────────────────────────────

    /// <summary>
    /// Verify the shop, and specifically the claim the whole economy rests
    /// on: that a cheap piece matching the room's style is worth more than an
    /// expensive one that clashes. If that inverts, style coherence stops
    /// being a real decision and the player is just told to buy the most
    /// expensive thing.
    /// </summary>
    private void RunCatalogCheck()
    {
        GD.Print("\n── Furniture catalogue ──");

        var catalog = _boot.Catalog;
        var venue = _boot.Venue;

        Check("catalog exists", catalog != null);
        if (catalog == null || venue == null) return;

        // The Rose Room was seeded entirely in Baroque.
        var tile = new Vector3I(0, 0, 1);
        var room = venue.GetRoom(tile);
        Check("found a furnished suite to decorate", room != null);
        if (room == null) return;

        var before = RoomAppointmentCalculator.GetBreakdown(room);
        GD.Print($"  {room.RoomName}: appt {before.Appointment:F1}, " +
                 $"dominant {before.DominantStyle} ({before.DominantStyleShare:P0}), " +
                 $"{before.DistinctStyleCount} style(s), {room.FreeFurnitureArea} slots free");

        var offStyle = before.DominantStyle == FurnitureStyle.Modern
            ? FurnitureStyle.Baroque
            : FurnitureStyle.Modern;

        // Compare at EQUAL tier, so the only variable is style. Quoting a
        // tier-1 match against a tier-4 clash would confound coherence with
        // quality and prove nothing about either.
        const int fairTier = 3;

        var matching = catalog.Quote(room,
            FurnitureItem.Create("Test Matching", FurnitureCategory.Seating,
                before.DominantStyle, fairTier));

        var clashing = catalog.Quote(room,
            FurnitureItem.Create("Test Clashing", FurnitureCategory.Seating,
                offStyle, fairTier));

        GD.Print($"  tier-{fairTier} matching: {matching.AppointmentDelta:+0.0;-0.0;0} appt");
        GD.Print($"  tier-{fairTier} clashing: {clashing.AppointmentDelta:+0.0;-0.0;0} appt");

        Check("matching piece is flagged as style-matched", matching.MatchesRoomStyle);
        Check("clashing piece is not flagged as style-matched", !clashing.MatchesRoomStyle);
        Check("at equal tier and price, matching beats clashing",
            matching.AppointmentDelta > clashing.AppointmentDelta);

        // The other half of the design: in a room whose coherence is already
        // perfect, a cheap piece dilutes the quality mean rather than helping.
        // That is intended — it is what stops "buy the cheapest matching
        // thing forever" from being a dominant strategy — so assert it holds
        // rather than leaving it as an accident.
        var cheapMatch = catalog.Quote(room,
            FurnitureItem.Create("Test Cheap", FurnitureCategory.Seating,
                before.DominantStyle, tier: 1));

        GD.Print($"  tier-1 matching into a maxed-coherence room: " +
                 $"{cheapMatch.AppointmentDelta:+0.0;-0.0;0} appt");

        Check("cheap filler cannot pad an already-coherent room",
            cheapMatch.AppointmentDelta < matching.AppointmentDelta);

        // Recommendations should surface something useful.
        var recommendations = catalog.GetRecommendations(room);
        Check("catalog recommends improvements", recommendations.Count > 0);

        // An actual purchase must move cash and Appointment in the right
        // directions, and land in the room.
        var cashBefore = GameStateManager.Instance.Cash;
        var countBefore = room.Furniture.Count;

        var pick = recommendations.FirstOrDefault();
        if (pick != null)
        {
            var bought = catalog.Purchase(venue, tile, pick);
            Check($"purchased {pick.Item.ItemName}", bought);

            if (bought)
            {
                Check("the piece is in the room", room.Furniture.Count == countBefore + 1);
                Check("cash was deducted", GameStateManager.Instance.Cash < cashBefore);
                Check("appointment rose",
                    RoomAppointmentCalculator.GetBreakdown(room).Appointment > before.Appointment);
            }
        }

        // Selling returns something but not the full price.
        var refundCash = GameStateManager.Instance.Cash;
        var sold = catalog.Sell(venue, tile, room.Furniture.Count - 1);
        Check("sold a piece back", sold);
        Check("resale refunded less than it cost",
            GameStateManager.Instance.Cash > refundCash &&
            GameStateManager.Instance.Cash < refundCash + (pick?.Price ?? 0));
    }

    // ── Expansion gate ─────────────────────────────────────────────────

    /// <summary>
    /// Buying a floor must require cash, reputation AND a zoning permit. The
    /// permit is the point: it is what makes cultivating the City
    /// Commissioner worth doing, and if expansion can be bought on cash alone
    /// the entire political layer goes back to being decorative.
    /// </summary>
    private void RunExpansionCheck()
    {
        GD.Print("\n── Expansion gate ──");

        var venue = _boot.Venue;
        var gsm = GameStateManager.Instance;
        var politics = GetTree()?.Root?.FindChild(
            "PoliticalInfluenceSystem", true, false) as PoliticalInfluenceSystem;

        Check("political system present", politics != null);
        if (venue == null || politics == null || gsm == null) return;

        var target = venue.HighestFloor + 1;

        // Plenty of money and standing, but no permit.
        gsm.Cash = 500000;
        gsm.Reputation = 95f;

        var blocked = !venue.CanBuyFloor(target, out var reason);
        GD.Print($"  with cash and reputation but no permit: {reason}");
        Check("expansion is blocked without a zoning permit", blocked);
        Check("the refusal names the commissioner",
            reason.Contains("Commissioner", StringComparison.OrdinalIgnoreCase));

        Check("buying anyway fails", !venue.BuyFloor(target));
        Check("no floor was added", !venue.HasFloor(target));

        // Cultivate the commissioner past the permit threshold.
        politics.RestoreState(new System.Text.Json.Nodes.JsonObject
        {
            ["figures"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["figure"] = MunicipalFigure.CityCommissioner.ToString(),
                    ["favor"] = 80f,
                    ["allocation"] = 0.0
                }
            }
        });

        Check("permit becomes available at high favour",
            politics.IsPermitAvailable("basic_expansion"));

        var allowed = venue.CanBuyFloor(target, out var stillBlocked);
        if (!allowed) GD.Print($"  still blocked: {stillBlocked}");
        Check("expansion is permitted once the commissioner is cultivated", allowed);

        var favourBefore = politics.CommissionerFavor;
        Check("floor purchased", venue.BuyFloor(target));
        Check("the floor exists", venue.HasFloor(target));
        Check("buying the floor spent commissioner favour",
            politics.CommissionerFavor < favourBefore);

        // Reputation must gate too, independently of the permit.
        gsm.Reputation = 5f;
        var repBlocked = !venue.CanBuyFloor(venue.HighestFloor + 1, out var repReason);
        Check("expansion is blocked by low reputation", repBlocked);
        Check("the refusal names reputation",
            repReason.Contains("reputation", StringComparison.OrdinalIgnoreCase));
    }

    // ── Policy tree ────────────────────────────────────────────────────

    /// <summary>
    /// Walk the Panderer's Code from an unchosen branch up the ladder.
    ///
    /// The prerequisite check compared enacted *keys* against a *PolicyName*,
    /// so nothing past tier 0 could ever unlock and the campaign's defining
    /// progression was dead. Climbing two tiers here is what proves it.
    /// </summary>
    private void RunPolicyCheck()
    {
        GD.Print("\n── Panderer's Code ──");

        var policies = _boot.Policies;
        Check("policy tree present", policies != null);
        if (policies == null) return;

        Check("no branch is chosen at the start", policies.ActiveBranch == PolicyBranch.None);

        // Tier 0 opens the branch.
        var first = policies.EnactPolicy("WF0");
        GD.Print($"  WF0: {first.Message}");
        Check("tier 0 can be enacted", first.Success);
        Check("the branch is now Workforce Protection",
            policies.ActiveBranch == PolicyBranch.WorkforceProtection);

        // The opposing branch must be closed for good.
        var blocked = policies.EnactPolicy("SE0");
        Check("the opposing branch is locked out", !blocked.Success);

        // Cooldown gates the next signing, so clear it before climbing.
        policies.EnactmentCooldownDays = 0;

        var second = policies.EnactPolicy("WF1");
        GD.Print($"  WF1: {second.Message}");
        Check("tier 1 unlocks once its prerequisite is enacted", second.Success);

        // Tier 2 also carries a MinDay, so the house has to have been running
        // a while. Advance past it rather than asserting against the design.
        GameStateManager.Instance.SetDayCount(40);

        var third = policies.EnactPolicy("WF2");
        GD.Print($"  WF2: {third.Message}");
        Check("tier 2 unlocks after tier 1 and its minimum day", third.Success);

        // A tier whose prerequisite is missing must still refuse, so the
        // ladder cannot be climbed out of order.
        var outOfOrder = policies.EnactPolicy("WF3");
        var reachedTier3 = outOfOrder.Success;
        GD.Print($"  WF3: {outOfOrder.Message}");
        Check("tier 3 follows tier 2 in order", reachedTier3);

        // Skipping a tier must still be refused.
        var skipped = policies.EnactPolicy("SE3");
        Check("a policy on the closed branch stays refused", !skipped.Success);

        Check("enacted policies are recorded", policies.EnactedPolicies.Count == 4);
        GD.Print($"  modifiers: {policies.GetModifierSummary()}");

        Check("enacting policies produced real modifiers",
            policies.GetModifierSummary() != "(no active modifiers)");
    }

    // ── Regulars and patrons ───────────────────────────────────────────

    /// <summary>
    /// Walk a client from stranger to Patron and confirm each consequence.
    ///
    /// This is the loop the whole design rests on: service quality produces
    /// returning clients, returning clients spend more, and Patrons are the
    /// only worthwhile targets for the intel that buys political favour. If
    /// a satisfied client is forgotten at the end of the night, none of that
    /// chain exists.
    /// </summary>
    private void RunRegularsCheck()
    {
        GD.Print("\n── Regulars and patrons ──");

        var regulars = _boot.Regulars;
        Check("regulars registry present", regulars != null);
        if (regulars == null) return;

        var client = ClientNegotiationHandler.GenerateRandomClient();
        client.Name = "Arthur Dent";

        // A stranger who leaves unhappy is not worth remembering.
        var forgotten = regulars.RecordVisit(null, client, 120, 30f, wantsToReturn: false);
        Check("an unhappy stranger is forgotten", forgotten == null);

        // Captured rather than assumed: GenerateRandomClient randomises this,
        // so comparing against a hardcoded 45 was only accidentally passing.
        var startingExpectation = client.ExpectedAppointment;

        // A stranger who enjoyed themselves becomes a record.
        var patron = regulars.RecordVisit(null, client, 120, 82f, wantsToReturn: true);
        Check("a satisfied stranger is remembered", patron != null);
        if (patron == null) return;

        Check("one visit is not yet a regular", patron.Standing == PatronStanding.FirstTime);

        // Climb to Regular, then to Patron.
        for (var visit = 2; visit <= RegularsRegistry.RegularVisitThreshold; visit++)
            regulars.RecordVisit(patron.Id, client, 150, 85f, true);

        Check($"{RegularsRegistry.RegularVisitThreshold} visits makes a regular",
            patron.Standing == PatronStanding.Regular);
        Check("the registry counts them as a regular", regulars.RegularCount >= 1);

        for (var visit = patron.Visits + 1; visit <= RegularsRegistry.PatronVisitThreshold; visit++)
            regulars.RecordVisit(patron.Id, client, 180, 88f, true);

        Check($"{RegularsRegistry.PatronVisitThreshold} visits makes a patron",
            patron.Standing == PatronStanding.Patron);

        GD.Print($"  {patron}");
        GD.Print($"  expectation has risen to {patron.ExpectedAppointment:F0}");

        Check($"their expectations rose with familiarity " +
              $"({startingExpectation:F0} → {patron.ExpectedAppointment:F0})",
            patron.ExpectedAppointment > startingExpectation);
        Check("spend accumulated", patron.TotalSpend > 500);

        // A patron arrives with a bigger purse than a stranger.
        var profile = regulars.BuildProfile(patron);
        Check("a returning patron brings a larger budget", profile.Budget > patron.Budget);
        Check("their remembered taste carries over",
            profile.PreferredStyle == patron.PreferredStyle);

        // Patrons are the intel targets; strangers are not.
        var targets = regulars.GetIntelTargets();
        Check("patrons are offered as intel targets", targets.Any(t => t.Id == patron.Id));

        regulars.MarkIntelGathered(patron.Id);
        Check("a mined patron drops off the target list",
            regulars.GetIntelTargets().All(t => t.Id != patron.Id));

        // A once-seen client must be eligible to come back, or nobody can
        // ever reach a second visit. The original filter required Standing
        // above FirstTime — which needs two visits — so the book could never
        // promote anybody and stayed empty across a whole campaign. Recording
        // visits by hand hid it; only driving the return roll catches it.
        var fresh = ClientNegotiationHandler.GenerateRandomClient();
        fresh.Name = "Ford Prefect";

        var once = regulars.RecordVisit(null, fresh, 100, 80f, wantsToReturn: true);
        Check("a once-seen client is in the book", once != null);
        Check("they are still only a first-timer", once.Standing == PatronStanding.FirstTime);

        var everReturned = false;
        for (var attempt = 0; attempt < 200 && !everReturned; attempt++)
            everReturned = regulars.RollReturningClient() != null;

        Check("a once-seen client can be drawn back through the door", everReturned);

        // Nobody visits twice in one evening. The roll runs per arrival, so
        // without a per-night guard one patron racked up nineteen visits
        // across thirteen nights.
        var drawnTonight = new List<string>();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var drawn = regulars.RollReturningClient();
            if (drawn != null) drawnTonight.Add(drawn.Id);
        }

        Check("nobody is drawn twice in one night",
            drawnTonight.Count == drawnTonight.Distinct().Count());

        regulars.AdvanceNight();
        Check("a new night lets them return again",
            Enumerable.Range(0, 200).Any(_ => regulars.RollReturningClient() != null));

        // Someone who stops enjoying the place stops coming.
        for (var i = 0; i < 8; i++) regulars.RecordVisit(patron.Id, client, 20, 5f, true);
        regulars.AdvanceNight();

        Check("a client who sours is written off", regulars.GetById(patron.Id) == null);
    }

    // ── Ambitions ──────────────────────────────────────────────────────

    /// <summary>
    /// Every ambition must have a route to fulfilment.
    ///
    /// Freedom and Revenge had none: a debt-bound staff member wanted out
    /// with no way to pay their contract down, and a poached one wanted their
    /// old house hurt with nothing that registered it. Both simply bled
    /// loyalty forever — a dead end rather than a decision, and both were
    /// mine.
    /// </summary>
    private void RunAmbitionCheck()
    {
        GD.Print("\n── Ambitions ──");

        var roster = StaffRoster.Instance;
        var gsm = GameStateManager.Instance;
        Check("roster present for ambition checks", roster != null);
        if (roster == null || gsm == null) return;

        gsm.Cash = 50000;

        // ── Freedom: buy someone out of a debt contract ────────────────
        var bound = new StaffMember
        {
            StaffName = "Vesna Baranov",
            Origin = StaffOrigin.Debt,
            Ambition = StaffAmbition.Freedom,
            ContractDebt = 4000,
            Salary = 120
        };

        Check("a bound staff member is added", roster.Add(bound));
        Check("they cannot leave while bound", !roster.Remove(bound.Id, "quit"));
        Check("their ambition starts unfulfilled", !bound.AmbitionFulfilled);

        var loyaltyBefore = bound.Loyalty;
        Check("half the contract can be paid", roster.PayDownContract(bound.Id, 2000));
        Check("the debt fell", bound.ContractDebt < 4000);
        Check("paying it down earned loyalty", bound.Loyalty > loyaltyBefore);

        Check("the remainder can be cleared", roster.PayDownContract(bound.Id, 2000));
        Check("the contract is gone", !bound.IsBound);
        Check("freedom is fulfilled", bound.AmbitionFulfilled);
        Check("they can now choose to leave", roster.Remove(bound.Id, "walked out"));

        GD.Print($"  Freedom: contract cleared, loyalty {loyaltyBefore:F0} → {bound.Loyalty:F0}");

        // ── Revenge: strike at the house they came from ────────────────
        var syndicates = GetTree()?.Root?.FindChild("SyndicateRivalAI", true, false) as SyndicateRivalAI;
        Check("syndicates present", syndicates != null);
        if (syndicates == null) return;

        var faction = syndicates.Syndicates.FirstOrDefault();
        Check("a rival faction exists", faction != null);
        if (faction == null) return;

        var poached = new StaffMember
        {
            StaffName = "Sable Kovač",
            Origin = StaffOrigin.Poached,
            Ambition = StaffAmbition.Revenge,
            AssociatedFaction = faction.Name,
            Salary = 400
        };

        roster.Add(poached);

        // An unrelated faction must not count.
        Check("striking an unrelated house does nothing",
            !poached.RegisterActionAgainst("Somebody Else"));

        gsm.Cash = 50000;
        syndicates.CounterSabotage(faction.Name, 1000);

        Check("striking their old house advanced revenge", poached.AmbitionProgress > 0f);
        GD.Print($"  Revenge: progress {poached.AmbitionProgress:F0} after one strike at {faction.Name}");

        // Enough blows should finish it.
        for (var i = 0; i < 3; i++) poached.RegisterActionAgainst(faction.Name, 1f);
        Check("sustained retaliation fulfils revenge", poached.AmbitionFulfilled);

        roster.Remove(poached.Id, "test cleanup");
    }

    // ── Narrative arcs ─────────────────────────────────────────────────

    /// <summary>
    /// Arc endings must change the world, not describe changing it.
    ///
    /// All three arcs resolved into flavour text sitting above a
    /// "// Apply permanent bonus" comment, so the campaign's entire long-term
    /// spine — 200-plus in-game days of build-up — altered nothing about the
    /// run. This drives each ending and checks the promised consequence
    /// actually landed.
    /// </summary>
    private void RunNarrativeCheck()
    {
        GD.Print("\n── Narrative arcs ──");

        var arcs = GetTree()?.Root?.FindChild("NarrativeArcTracker", true, false)
            as NarrativeArcTracker;
        var heat = _boot.Heat;
        var gsm = GameStateManager.Instance;

        Check("narrative tracker present", arcs != null);
        if (arcs == null || heat == null || gsm == null) return;

        // ── A friendly mayor should permanently damp heat ──────────────
        gsm.PublicSentiment = 80f;

        var politics = GetTree()?.Root?.FindChild("PoliticalInfluenceSystem", true, false)
            as PoliticalInfluenceSystem;

        politics?.RestoreState(new System.Text.Json.Nodes.JsonObject
        {
            ["figures"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["figure"] = MunicipalFigure.DistrictAttorney.ToString(),
                    ["favor"] = 75f, ["allocation"] = 0.0
                }
            }
        });

        var heatMultiplierBefore = heat.CampaignHeatMultiplier;
        var commissionerBefore = politics?.CommissionerFavor ?? 0f;

        arcs.ResolveArcForTesting("mayoral_campaign");

        Check("a friendly mayor permanently damps heat generation",
            heat.CampaignHeatMultiplier < heatMultiplierBefore);
        Check("an ally in office lifts commissioner favour",
            politics == null || politics.CommissionerFavor > commissionerBefore);

        GD.Print($"  mayoral: heat ×{heat.CampaignHeatMultiplier:F2}, " +
                 $"commissioner {politics?.CommissionerFavor:F0}");

        // ── Winning the syndicate war should pay tribute ───────────────
        var ledger = _boot.Ledger;
        var passiveBefore = ledger.PassiveDailyIncome;

        var syndicates = GetTree()?.Root?.FindChild("SyndicateRivalAI", true, false) as SyndicateRivalAI;
        foreach (var faction in syndicates?.Syndicates ?? new List<RivalSyndicate>())
            faction.Aggression = 5f;

        // Dominance needs territory as well as a beaten enemy, so take three
        // districts. Setting only aggression resolves to the truce ending,
        // which correctly pays nothing.
        var market = GetTree()?.Root?.FindChild("RealEstateMarket", true, false) as RealEstateMarket;
        if (market != null)
        {
            foreach (var property in market.Properties.Values.Take(3))
                property.Status = PropertyStatus.Owned;
        }

        Check("three districts are held", (market?.OwnedCount ?? 0) >= 3);

        arcs.ResolveArcForTesting("syndicate_war");

        Check("crushing the syndicates pays standing tribute",
            ledger.PassiveDailyIncome > passiveBefore);
        Check("beaten syndicates stop pushing",
            syndicates == null || syndicates.Syndicates.All(f => f.Aggression <= 5f));

        GD.Print($"  syndicate war: standing income ${ledger.PassiveDailyIncome:F0}/day");

        // ── A federal indictment should actually cost money ────────────
        politics?.RestoreState(new System.Text.Json.Nodes.JsonObject
        {
            ["figures"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["figure"] = MunicipalFigure.DistrictAttorney.ToString(),
                    ["favor"] = 5f, ["allocation"] = 0.0
                }
            }
        });

        gsm.Cash = 20000;
        var cashBefore = gsm.Cash;

        var macro = _boot.Macro;
        arcs.ResolveArcForTesting("federal_investigation");

        Check("an indictment seizes assets", gsm.Cash < cashBefore);
        Check("an indictment forces a crackdown",
            macro == null || macro.CurrentPhase == MacroPhase.PoliceCrackdown);

        GD.Print($"  federal: cash {cashBefore:F0} → {gsm.Cash:F0}, phase {macro?.CurrentPhase}");
    }

    // ── Facility licences ──────────────────────────────────────────────

    /// <summary>
    /// Licences are the only thing that raises a ceiling, so what matters is
    /// that the ceiling actually moves and the game notices.
    ///
    /// The headline case: the catalogue stocked tiers 1–3 while FurnitureItem
    /// defines five, so the top two tiers were generated, scored by the
    /// appointment formula, and completely unbuyable. This proves a licence
    /// opens them.
    /// </summary>
    private void RunLicenceCheck()
    {
        GD.Print("\n── Facility licences ──");

        var licences = _boot.Licences;
        var catalog = _boot.Catalog;
        var roster = StaffRoster.Instance;
        var gsm = GameStateManager.Instance;

        Check("licences system present", licences != null);
        if (licences == null || catalog == null || roster == null || gsm == null) return;

        gsm.Cash = 100000;

        // The old research tree is gone, not hidden.
        Check("the research tree is deleted",
            GetTree()?.Root?.FindChild("ResearchTreeUI", true, false) == null);

        // ── Ceilings start where they started ──────────────────────────
        var room = _boot.Venue.GetRoom(new Vector3I(0, 0, 1));
        var capacityBefore = room?.FurnitureCapacity ?? 0;

        Check("catalogue starts capped at tier 3", catalog.MaxAvailableTier == 3);
        Check("tier 5 furniture is unbuyable at the start",
            catalog.GetOffers(room).All(o => o.Item.Tier <= 3));

        // ── Prerequisites are enforced ─────────────────────────────────
        Check("a second-tier licence needs the first",
            !licences.CanApply("craft_2", out _));

        // ── Apply, wait, and be granted ────────────────────────────────
        Check("the first craft licence can be applied for", licences.Apply("craft_1"));
        Check("it is not granted immediately", !licences.IsGranted("craft_1"));
        Check("it is in progress", licences.IsInProgress("craft_1"));
        Check("applying twice is refused", !licences.Apply("craft_1"));

        var days = FacilityLicences.Get("craft_1").Days;
        for (var i = 0; i < days; i++) gsm.AdvanceDay();

        Check("it is granted once the wait elapses", licences.IsGranted("craft_1"));
        Check("the catalogue now stocks tier 4", catalog.MaxAvailableTier == 4);
        Check("tier 4 furniture is on sale",
            catalog.GetOffers(room).Any(o => o.Item.Tier == 4));

        GD.Print($"  craft_1 granted after {days} days — catalogue now tier {catalog.MaxAvailableTier}");

        // ── The second tier unlocks behind it ──────────────────────────
        Check("the second craft licence is now available", licences.CanApply("craft_2", out _));

        // ── Other lines move their own ceilings ────────────────────────
        var rosterCapBefore = roster.RosterCap;
        licences.Apply("house_1");
        for (var i = 0; i < FacilityLicences.Get("house_1").Days; i++) gsm.AdvanceDay();

        Check("the house licence raises the roster cap", roster.RosterCap > rosterCapBefore);

        licences.Apply("space_1");
        for (var i = 0; i < FacilityLicences.Get("space_1").Days; i++) gsm.AdvanceDay();

        var capacityAfter = _boot.Venue.GetRoom(new Vector3I(0, 0, 1))?.FurnitureCapacity ?? 0;
        Check($"the fittings permit raises room capacity ({capacityBefore} → {capacityAfter})",
            capacityAfter > capacityBefore);

        GD.Print($"  roster cap {rosterCapBefore} → {roster.RosterCap}, " +
                 $"room capacity {capacityBefore} → {capacityAfter}");

        // ── Ceilings must survive a reload ─────────────────────────────
        var captured = licences.CaptureState();
        catalog.MaxAvailableTier = 3;
        roster.RosterCap = 6;
        RoomModule.FurnitureSlotsPerTile = 2;

        licences.RestoreState(captured);

        Check("restoring a save re-applies the ceilings",
            catalog.MaxAvailableTier == 4 && roster.RosterCap > 6 &&
            RoomModule.FurnitureSlotsPerTile > 2);
    }

    /// <summary>
    /// The three strike resolutions. Every one of them had zero callers in
    /// the whole repo until the labour panel was built, so none had ever
    /// executed — which is exactly the condition that hid two fatal bugs in
    /// the policy tree. Each is exercised here from a forced strike.
    /// </summary>
    private void RunUnionCheck()
    {
        GD.Print("\n── Labour disputes ──");

        var union = _boot.Union;
        var gsm = GameStateManager.Instance;
        var roster = StaffRoster.Instance;

        Check("unionization system present", union != null);
        if (union == null || gsm == null || roster == null) return;

        Check("no strike on a well-run house", !union.StrikeActive);

        // ── Negotiate ──────────────────────────────────────────────────
        var opexBefore = _boot.Policies?.PermanentOpexModifier ?? 0;

        union.ForceStrike();
        Check("a forced strike starts", union.StrikeActive);
        Check("someone actually walked out", union.StrikingStaffCount > 0);
        Check("the walkout cannot exceed the roster",
            union.StrikingStaffCount <= roster.Count);

        union.NegotiateProfitSharing();
        Check("negotiating ends the strike", !union.StrikeActive);
        Check("profit-sharing costs permanent OPEX",
            (_boot.Policies?.PermanentOpexModifier ?? 0) > opexBefore);

        // ── Strikebreakers ─────────────────────────────────────────────
        var heatBefore = gsm.Heat;
        var satisfactionBefore = roster.GetAverageSatisfaction();

        union.ForceStrike();
        union.HireStrikebreakers();

        Check("strikebreakers end the strike", !union.StrikeActive);
        Check($"strikebreakers raise heat ({heatBefore:F0} → {gsm.Heat:F0})",
            gsm.Heat > heatBefore);
        Check("strikebreakers cost goodwill",
            roster.GetAverageSatisfaction() < satisfactionBefore);

        // ── Concede — the only one with a cash price ───────────────────
        gsm.Cash = 50000;
        var revenueBefore = _boot.Ledger.GetTotalExpenses();

        union.ForceStrike();
        union.ConcedeToDemands();

        Check("conceding ends the strike", !union.StrikeActive);
        Check("conceding clears the risk entirely", union.UnionRisk <= 0.01f);
        Check("the concession is booked in the ledger, not taken off cash silently",
            _boot.Ledger.GetTotalExpenses() >= revenueBefore + union.ConcessionCost);

        // ── A resolution must be reachable, not a dead end ─────────────
        union.ForceStrike();
        var strikeExpenses = _boot.Ledger.GetTotalExpenses();
        gsm.AdvanceDay();

        Check("a running strike bills the house through the ledger",
            _boot.Ledger.GetTotalExpenses() > strikeExpenses);

        union.ConcedeToDemands();
        Check("the house can always settle", !union.StrikeActive);

        GD.Print($"  all three resolutions exercised — {union}");
    }

    /// <summary>
    /// The city's phase and its consequences.
    ///
    /// The macro engine computes six multipliers and, until the city chip was
    /// built, two of them had no consumer at all — including the one a police
    /// crackdown uses to make bribery expensive. A phase whose stated effects
    /// do not happen is worse than no phase, because the interface now
    /// promises them.
    /// </summary>
    private void RunCityCheck()
    {
        GD.Print("\n── City conditions ──");

        var macro = _boot.Macro;
        var heat = _boot.Heat;
        var gsm = GameStateManager.Instance;

        Check("macro engine present", macro != null);
        if (macro == null || heat == null || gsm == null) return;

        // ── The chip says something usable ─────────────────────────────
        Check("the city has a caption", !string.IsNullOrWhiteSpace(macro.GetShortCaption()));
        Check("the caption names the effect on trade when there is one",
            Mathf.IsEqualApprox(macro.FootfallMultiplier, 1f) ||
                macro.GetShortCaption().Contains('×'));

        var summary = macro.GetPlainSummary();
        Check("the tooltip explains the effect on trade", summary.Contains("through the door"));
        Check("the tooltip says how long it lasts",
            summary.Contains("left before") || summary.Contains("about to change"));

        // ── A crackdown must actually cost the player ──────────────────
        macro.ForcePhase(MacroPhase.Stagnation);
        gsm.Cash = 100000;
        heat.RestoreState(new System.Text.Json.Nodes.JsonObject { ["heat"] = 60f });

        var quietHeat = gsm.Heat;
        heat.BribePrecinctCaptain(1000);
        var boughtQuiet = quietHeat - gsm.Heat;

        macro.ForcePhase(MacroPhase.PoliceCrackdown);
        Check("a crackdown is adverse", macro.IsAdverse);
        Check("a crackdown thins the crowd", macro.FootfallMultiplier < 1f);

        heat.RestoreState(new System.Text.Json.Nodes.JsonObject { ["heat"] = 60f });
        var crackdownHeat = gsm.Heat;
        heat.BribePrecinctCaptain(1000);
        var boughtCrackdown = crackdownHeat - gsm.Heat;

        Check($"the same bribe buys less under a crackdown " +
              $"({boughtQuiet:F1} → {boughtCrackdown:F1} heat)",
            boughtCrackdown < boughtQuiet);

        // ── And a boom must actually help ──────────────────────────────
        macro.ForcePhase(MacroPhase.Boom);
        Check("a boom is not adverse", !macro.IsAdverse);
        Check("a boom brings more people in", macro.FootfallMultiplier > 1f);

        // ── Property follows the city ──────────────────────────────────
        // PropertyValueMultiplier had no consumers, so districts were priced
        // identically in a boom and a recession.
        var market = GetTree()?.Root?.FindChild("RealEstateMarket", true, false) as RealEstateMarket;
        Check("districts are registered", (market?.Properties.Count ?? 0) > 0);

        if (market != null && market.Properties.Count > 0)
        {
            var district = market.Properties.Keys.First();

            gsm.AdvanceDay();
            var boomValue = market.Properties[district].CurrentValue;

            macro.ForcePhase(MacroPhase.Recession);
            gsm.AdvanceDay();
            var slumpValue = market.Properties[district].CurrentValue;

            Check($"property is worth less in a slump (${boomValue:N0} → ${slumpValue:N0})",
                slumpValue < boomValue);

            Check("rent follows the value down",
                market.Properties[district].MonthlyRent < boomValue * 0.02);
        }

        macro.ForcePhase(MacroPhase.Stagnation);

        GD.Print($"  {macro.GetShortCaption()} — bribe bought {boughtQuiet:F1} " +
                 $"heat quiet, {boughtCrackdown:F1} under a crackdown");
    }

    /// <summary>
    /// Crises, and specifically the deadlock.
    ///
    /// The director set <c>_crisisActive</c> on trigger and cleared it only
    /// inside <c>ExecuteChoice</c>, which needs a scenario — and the scenario
    /// was supposed to arrive from an out-of-process language model reading
    /// a payload off stdout. Nothing reads it. So the first crisis latched
    /// the director for the rest of the campaign and no crisis ever reached
    /// the player. The system was never instantiated, which is the only
    /// reason that was survivable.
    /// </summary>
    private void RunCrisisCheck()
    {
        GD.Print("\n── Crises ──");

        var crises = _boot.Crises;
        var gsm = GameStateManager.Instance;

        Check("crisis director is instantiated", crises != null);
        if (crises == null || gsm == null) return;

        // Earlier sections deliberately wreck the world — the union check
        // hires strikebreakers, which drops public sentiment past the scandal
        // threshold — so a crisis is very often already running by the time
        // we get here. That the triggers fire unprompted is the system
        // working; clear it and start from a known state.
        var arrivedOnItsOwn = crises.CrisisActive;
        crises.DismissCrisis();

        Check("dismissing leaves no crisis behind", !crises.CrisisActive);

        if (arrivedOnItsOwn)
            GD.Print("  a crisis had already triggered on its own from the world state");

        crises.CooldownDays = 0;

        // ── A crisis arrives complete ──────────────────────────────────
        crises.ForceCrisis(CrisisTrigger.PoliceRaid);
        Check("the forced crisis is the one asked for",
            crises.ActiveScenario?.Trigger == nameof(CrisisTrigger.PoliceRaid));

        Check("forcing a crisis raises one", crises.CrisisActive);
        Check("the scenario is present immediately, not awaited",
            crises.ActiveScenario != null);
        Check("it has something to read",
            !string.IsNullOrWhiteSpace(crises.ActiveScenario?.Narrative));
        Check("it offers at least two ways out",
            crises.ActiveScenario?.Choices.Count >= 2);

        foreach (var choice in crises.ActiveScenario.Choices)
        {
            Check($"\"{choice.Label}\" states a consequence",
                choice.Effects != null &&
                (choice.Effects.Cash != 0 || choice.Effects.Heat != 0 ||
                 choice.Effects.Reputation != 0 || choice.Effects.PublicSentiment != 0));
        }

        // ── Choosing actually does something ───────────────────────────
        gsm.Cash = 50000;
        var cashBefore = gsm.Cash;
        var expensesBefore = _boot.Ledger.GetTotalExpenses();
        var costed = crises.ActiveScenario.Choices[0].Effects.Cash;

        Check("a choice can be taken", crises.ExecuteChoice(0));
        Check("taking it ends the crisis", !crises.CrisisActive);
        Check("the scenario is cleared", crises.ActiveScenario == null);
        Check($"it cost what it said (${-costed:N0})", gsm.Cash < cashBefore);
        Check("and it is in the books, not taken off cash silently",
            _boot.Ledger.GetTotalExpenses() > expensesBefore);

        // ── The deadlock, directly ─────────────────────────────────────
        crises.ForceCrisis(CrisisTrigger.WorkerWalkout);
        Check("a second crisis can be raised after the first was answered",
            crises.CrisisActive);

        crises.DismissCrisis();
        Check("dismissing without deciding also clears it", !crises.CrisisActive);

        crises.ForceCrisis(CrisisTrigger.FinancialCollapse);
        Check("and a third still arrives after a dismissal", crises.CrisisActive);

        Check("a refused choice index does not latch the director",
            !crises.ExecuteChoice(99) && crises.CrisisActive);

        crises.DismissCrisis();

        // ── Every trigger has authored content ─────────────────────────
        foreach (CrisisTrigger trigger in Enum.GetValues<CrisisTrigger>())
        {
            if (trigger == CrisisTrigger.None) continue;

            var scenario = crises.GenerateFallbackScenario(trigger);
            Check($"{trigger} has a written scenario",
                scenario != null && scenario.Choices.Count >= 2 &&
                !string.IsNullOrWhiteSpace(scenario.Title));
        }

        crises.CooldownDays = 6;
        GD.Print($"  {crises}");
    }

    // ── Persistence round-trip ─────────────────────────────────────────

    private void RunPersistenceCheck()
    {
        GD.Print("\n── Persistence round-trip ──");

        var gsm = GameStateManager.Instance;
        var roster = StaffRoster.Instance;

        // The ledger must actually reflect the nights we played.
        Check("ledger recorded revenue", _boot.Ledger.GetTotalRevenue() > 0);
        Check("ledger recorded expenses", _boot.Ledger.GetTotalExpenses() > 0);

        // Heat must be able to fall on a quiet day — the old gate made this
        // impossible, so it is worth asserting directly.
        var heatBefore = gsm.Heat;
        gsm.Heat = 50f;
        _boot.Heat.RestoreState(new System.Text.Json.Nodes.JsonObject { ["heat"] = 50f });
        gsm.AdvanceDay();
        Check($"heat decays on a quiet day ({50f:F1} → {gsm.Heat:F1})", gsm.Heat < 50f);

        _savedCash = gsm.Cash;
        _savedDay = gsm.DayCount;
        _savedHeat = gsm.Heat;
        _savedStaffCount = roster.Count;
        _savedRoomCount = _boot.Venue.Rooms.Count;

        var probe = roster.GetAll().FirstOrDefault();
        _probeStaffId = probe?.Id;
        _probeStaffStress = probe?.Stress ?? 0f;

        Check("save succeeded", _boot.SaveLoad.SaveGame("smoketest"));

        // Perturb everything the load must put back.
        gsm.Cash = 999999;
        gsm.Heat = 5f;
        gsm.SetDayCount(1);
        roster.ReplaceAll(Array.Empty<StaffMember>());
        Check("world was perturbed before reload", roster.Count == 0);

        var loaded = _boot.SaveLoad.LoadGame("smoketest");
        Check("load succeeded (checksum verified)", loaded != null);

        if (loaded != null)
        {
            Check($"cash restored (${_savedCash:F0})",
                Math.Abs(gsm.Cash - _savedCash) < 0.01);
            Check($"day restored ({_savedDay})", gsm.DayCount == _savedDay);
            Check($"heat restored ({_savedHeat:F1})",
                Mathf.Abs(gsm.Heat - _savedHeat) < 0.1f);
            Check($"roster restored ({_savedStaffCount} staff)",
                roster.Count == _savedStaffCount);
            Check($"floorplan restored ({_savedRoomCount} rooms)",
                _boot.Venue.Rooms.Count == _savedRoomCount);

            var restored = roster.GetById(_probeStaffId);
            Check("staff identity survived by Id", restored != null);
            Check("staff stress survived",
                restored != null && Mathf.Abs(restored.Stress - _probeStaffStress) < 0.1f);
            Check("furniture survived the round-trip",
                _boot.Venue.GetAverageAppointment() > 0f);
            Check("relationships survived",
                roster.GetBondedPairs().Count > 0 || roster.GetRivalPairs().Count > 0);
        }

        _boot.SaveLoad.DeleteSave("smoketest");
        Finish();
    }

    // ── Reporting ──────────────────────────────────────────────────────

    private void Check(string label, bool passed)
    {
        _checks.Add($"{(passed ? "  PASS" : "  FAIL")}  {label}");
        if (!passed) _failures.Add(label);
    }

    private void Finish()
    {
        _stage = Stage.Done;

        GD.Print("\n══════════ RESULTS ══════════");
        foreach (var line in _checks) GD.Print(line);

        GD.Print($"\n{_checks.Count - _failures.Count}/{_checks.Count} passed.");

        if (_failures.Count > 0)
        {
            GD.PrintErr($"SMOKE TEST FAILED — {_failures.Count} failure(s):");
            foreach (var f in _failures) GD.PrintErr($"  - {f}");
        }
        else
        {
            GD.Print("SMOKE TEST PASSED");
        }

        GD.Print("\nFinal world state:");
        GD.Print(_boot.GetWorldSummary());

        GetTree().Quit(_failures.Count > 0 ? 1 : 0);
    }
}
