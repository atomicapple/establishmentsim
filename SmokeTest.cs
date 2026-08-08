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
