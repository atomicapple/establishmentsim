using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>Beat of the night loop.</summary>
public enum NightPhase
{
    /// <summary>Between nights. Nothing scheduled.</summary>
    Idle,

    /// <summary>Paused. Assign staff to rooms, set prices, buy furniture.</summary>
    Preparation,

    /// <summary>Live. Clients arrive, are matched, and encounters resolve.</summary>
    Service,

    /// <summary>Remaining encounters finish; stragglers leave.</summary>
    Closing,

    /// <summary>Paused. The night's books, recap, and decisions.</summary>
    Ledger
}

/// <summary>One staff member's posting for the night.</summary>
public class ShiftAssignment
{
    public string StaffId { get; set; }
    public Vector3I RoomTile { get; set; }

    /// <summary>Encounters worked so far tonight.</summary>
    public int EncountersWorked { get; set; }

    /// <summary>Whether this staff member is mid-encounter right now.</summary>
    public bool IsBusy { get; set; }
}

/// <summary>An encounter in progress — the "cloud" window.</summary>
public class ActiveEncounter
{
    public string StaffId { get; set; }
    public Vector3I RoomTile { get; set; }
    public ClientProfile Client { get; set; }
    public double AgreedPrice { get; set; }

    /// <summary>Patron record when this is a return visit, else null.</summary>
    public string PatronId { get; set; }

    /// <summary>Seconds of service time remaining before it resolves.</summary>
    public float TimeRemaining { get; set; }

    /// <summary>Total duration, for driving the VFX progress bar.</summary>
    public float TotalDuration { get; set; }
}

/// <summary>Everything that happened in one night, for the Ledger screen.</summary>
public class NightReport
{
    public int Night { get; set; }
    public double Revenue { get; set; }
    public double Upkeep { get; set; }
    public double Salaries { get; set; }

    /// <summary>Cut paid to staff on what they earned tonight.</summary>
    public double StaffCommission { get; set; }

    public int ClientsArrived { get; set; }
    public int ClientsServed { get; set; }
    public int ClientsTurnedAway { get; set; }

    public int NewRegulars { get; set; }

    /// <summary>Clients tonight who had been here before.</summary>
    public int ReturningClients { get; set; }
    public float ReputationDelta { get; set; }
    public float HeatDelta { get; set; }

    public Dictionary<EncounterQuality, int> QualityCounts { get; } = new();
    public List<string> Highlights { get; } = new();
    public List<string> Incidents { get; } = new();

    /// <summary>Net cash position for the night, before standing OPEX.</summary>
    public double Net => Revenue - Upkeep - Salaries - StaffCommission;

    public override string ToString() =>
        $"[Night {Night}] Served {ClientsServed}/{ClientsArrived} — " +
        $"revenue ${Revenue:F0}, net ${Net:F0}, {Incidents.Count} incidents";
}

/// <summary>
/// Runs the night: Preparation → Service → Closing → Ledger.
///
/// This replaces the old shape of play, where a "day" was an invisible
/// 60-second accounting tick that fired <c>OnDailyTick</c> and did nothing
/// the player could see or influence. A night has a beginning, a rising
/// action and a resolution, which is what turns a long session into a
/// sequence of decisions rather than one undifferentiated blur.
///
/// It owns arrivals, staff assignment, and encounter resolution, and it is
/// the thing that finally connects furniture → appointment → price → cash.
/// </summary>
public partial class NightDirector : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnPhaseChangedEventHandler(int oldPhase, int newPhase);

    [Signal]
    public delegate void OnNightStartedEventHandler(int night);

    [Signal]
    public delegate void OnClientArrivedEventHandler(string clientName, int tier);

    [Signal]
    public delegate void OnClientTurnedAwayEventHandler(string clientName, string reason);

    /// <summary>Fired when an encounter begins — the cue to play the cloud VFX.</summary>
    [Signal]
    public delegate void OnEncounterStartedEventHandler(
        string staffId, int x, int y, int floor, float duration);

    /// <summary>Fired when an encounter resolves, carrying the quality band for the VFX.</summary>
    [Signal]
    public delegate void OnEncounterResolvedEventHandler(
        string staffId, int quality, double payment, int incident);

    [Signal]
    public delegate void OnNightConcludedEventHandler(int night, double revenue, double net);

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Length of the Service beat in seconds at 1× time scale.</summary>
    [Export] public float ServiceDurationSeconds { get; set; } = 240f;

    /// <summary>Seconds one encounter occupies a room at 1× time scale.</summary>
    [Export] public float EncounterDurationSeconds { get; set; } = 22f;

    /// <summary>
    /// Advance the night by this much per frame instead of by real elapsed
    /// time. Zero — the default — uses the real delta. Set only by headless
    /// harnesses, which need a run to be repeatable.
    /// </summary>
    [Export] public float FixedStepSeconds { get; set; }

    /// <summary>Base clients per night before reputation and footfall scaling.</summary>
    [Export] public float BaseArrivalsPerNight { get; set; } = 8f;

    /// <summary>Maximum clients waiting before further arrivals are turned away.</summary>
    [Export] public int MaxLobbySize { get; set; } = 8;

    /// <summary>Encounters a staff member can work before they are done for the night.</summary>
    [Export] public int MaxEncountersPerStaff { get; set; } = 4;

    /// <summary>Base nightly rate before Appointment and floor scaling.</summary>
    [Export] public double BaseRoomPrice { get; set; } = 78.0;

    /// <summary>
    /// Condition restored to every furnishing each night, paid for by the
    /// upkeep charge. Set below a busy room's nightly wear so heavily-used
    /// rooms still degrade over time — maintenance slows the decay, it does
    /// not cancel it.
    /// </summary>
    [Export] public float NightlyMaintenanceRepair { get; set; } = 2.0f;

    /// <summary>
    /// Share of each encounter's take that goes to the staff member who
    /// earned it.
    ///
    /// This is the main brake on the economy, and it is structural rather
    /// than a flat fee: costs now scale with revenue, so a busier house is
    /// not automatically a richer one. It also makes the Workforce Protection
    /// branch legible — paying people properly is what the exploitation
    /// branch is buying its way out of.
    /// </summary>
    [Export] public double StaffCommissionRate { get; set; } = 0.42;

    // ── State ──────────────────────────────────────────────────────────

    private NightPhase _phase = NightPhase.Idle;
    private readonly List<ShiftAssignment> _assignments = new();
    private readonly List<ActiveEncounter> _encounters = new();
    private readonly List<ClientProfile> _lobby = new();

    /// <summary>Patron id for lobby clients who have been here before.</summary>
    private readonly Dictionary<ClientProfile, string> _lobbyPatrons = new();
    private readonly RandomNumberGenerator _rng = new();

    private float _serviceElapsed;
    private float _nextArrivalIn;
    private int _arrivalsRemaining;
    private NightReport _report = new();

    public NightPhase Phase => _phase;
    public NightReport CurrentReport => _report;
    public IReadOnlyList<ShiftAssignment> Assignments => _assignments;
    public IReadOnlyList<ActiveEncounter> ActiveEncounters => _encounters;
    public IReadOnlyList<ClientProfile> Lobby => _lobby;

    /// <summary>Fraction of the Service beat elapsed, 0–1. Drives the night clock UI.</summary>
    public float ServiceProgress =>
        ServiceDurationSeconds <= 0f ? 1f : Mathf.Clamp(_serviceElapsed / ServiceDurationSeconds, 0f, 1f);

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        WorldRandom.Seed(_rng, nameof(NightDirector));
        GD.Print("[NightDirector] Ready.");
    }

    public override void _Process(double delta)
    {
        if (_phase is not (NightPhase.Service or NightPhase.Closing)) return;

        // Time scale and modal pausing stay MasterGameLoop's job — it already
        // tracks 0–3× and modal depth correctly, so the night simply consumes
        // whatever effective delta it hands out.
        // A fixed step makes a night advance in a deterministic number of
        // beats regardless of frame rate. Without it the balance harness
        // measured the machine it ran on as much as the economy: identical
        // 20-night runs landed anywhere from +$1,199 to +$5,118, because a
        // slower frame consumed more of the night per tick and served fewer
        // clients. Zero keeps real time, which is what play wants.
        float raw = FixedStepSeconds > 0f ? FixedStepSeconds : (float)delta;

        float scaled = GetEffectiveDelta(raw);
        if (scaled <= 0f) return;

        if (_phase == NightPhase.Service)
        {
            _serviceElapsed += scaled;
            TickArrivals(scaled);
            TickDispatch();

            if (_serviceElapsed >= ServiceDurationSeconds)
                CloseDoors();
        }

        TickEncounters(scaled);

        if (_phase == NightPhase.Closing && _encounters.Count == 0)
            SettleNight();
    }

    private float GetEffectiveDelta(float raw)
    {
        var loop = GetTree()?.Root?.FindChild("MasterGameLoop", true, false) as MasterGameLoop;
        return loop?.GetEffectiveDelta(raw) ?? raw;
    }

    // ── Beat transitions ───────────────────────────────────────────────

    private void SetPhase(NightPhase next)
    {
        if (next == _phase) return;

        var old = _phase;
        _phase = next;

        EmitSignal(SignalName.OnPhaseChanged, (int)old, (int)next);
        GD.Print($"[NightDirector] {old} → {next}");
    }

    /// <summary>Beat 1. Open the Preparation screen. Time is paused.</summary>
    public void BeginNight()
    {
        if (_phase != NightPhase.Idle && _phase != NightPhase.Ledger)
        {
            GD.PrintErr($"[NightDirector] Cannot begin a night from {_phase}.");
            return;
        }

        var night = (GameStateManager.Instance?.DayCount ?? 0) + 1;

        _report = new NightReport { Night = night };
        _assignments.Clear();
        _encounters.Clear();
        _lobby.Clear();
        _serviceElapsed = 0f;

        SetPhase(NightPhase.Preparation);
        EmitSignal(SignalName.OnNightStarted, night);
    }

    /// <summary>
    /// Assign a staff member to a room for the night. Only valid during
    /// Preparation — reassigning mid-service would let the player dodge every
    /// consequence of a bad posting.
    /// </summary>
    public bool AssignStaff(string staffId, Vector3I roomTile)
    {
        if (_phase != NightPhase.Preparation)
        {
            GD.PrintErr("[NightDirector] Staff can only be assigned during Preparation.");
            return false;
        }

        var roster = StaffRoster.Instance;
        var venue = FindVenue();
        if (roster == null || venue == null) return false;

        var staff = roster.GetById(staffId);
        if (staff == null) return false;

        var room = venue.GetRoom(roomTile);
        if (room == null || !venue.IsRevenueGenerating(room))
        {
            GD.Print("[NightDirector] That room cannot take a posting.");
            return false;
        }

        // One posting per staff member, one staff member per room.
        _assignments.RemoveAll(a => a.StaffId == staffId || a.RoomTile == room.GridPosition);
        _assignments.Add(new ShiftAssignment { StaffId = staffId, RoomTile = room.GridPosition });

        return true;
    }

    /// <summary>Beat 2. Open the doors. Time starts running.</summary>
    public void OpenDoors()
    {
        if (_phase != NightPhase.Preparation)
        {
            GD.PrintErr($"[NightDirector] Cannot open doors from {_phase}.");
            return;
        }

        _arrivalsRemaining = RollArrivalCount();
        _nextArrivalIn = 0f;
        _serviceElapsed = 0f;

        SetPhase(NightPhase.Service);
        GD.Print($"[NightDirector] Doors open. ~{_arrivalsRemaining} expected, " +
                 $"{_assignments.Count} staff posted.");
    }

    /// <summary>Beat 3. Stop admitting; let running encounters finish.</summary>
    public void CloseDoors()
    {
        if (_phase != NightPhase.Service) return;

        // Anyone still waiting when the doors close never gets seen.
        foreach (var waiting in _lobby)
        {
            _report.ClientsTurnedAway++;
            EmitSignal(SignalName.OnClientTurnedAway, waiting.Name, "still waiting at closing");
        }

        _lobby.Clear();
        _lobbyPatrons.Clear();

        SetPhase(NightPhase.Closing);
    }

    /// <summary>Beat 4. Settle the books and show the Ledger.</summary>
    private void SettleNight()
    {
        var venue = FindVenue();
        var roster = StaffRoster.Instance;

        // Furniture upkeep is what stops a large house from being free to own.
        _report.Upkeep = venue?.GetNightlyUpkeep() ?? 0.0;
        _report.Salaries = (roster?.GetTotalSalaries() ?? 0.0) / 30.0;

        if (_report.Upkeep > 0)
            ChargeExpense(ExpenseCategory.FacilityMaintenance, _report.Upkeep,
                $"Night {_report.Night} furniture upkeep");

        // The upkeep charge now buys something. Paying it refurbishes the
        // house a little each night, which is the player's answer to furniture
        // wear — without it, Appointment only ever falls.
        var repaired = venue?.MaintainAllFurniture(NightlyMaintenanceRepair) ?? 0;
        if (repaired > 0)
            GD.Print($"[NightDirector] Maintenance refurbished {repaired} pieces.");

        ApplySocialAndShiftEffects();
        ApplyReputationMemory();

        // Age the client book so people who stop coming eventually drop off.
        FindRegulars()?.AdvanceNight();

        SetPhase(NightPhase.Ledger);
        EmitSignal(SignalName.OnNightConcluded, _report.Night, _report.Revenue, _report.Net);

        GD.Print($"[NightDirector] {_report}");
    }

    /// <summary>
    /// The city forgets, in both directions.
    ///
    /// Reputation had no recovery term. It moved only through encounter
    /// outcomes, and arrivals scale with it — so a shock that lowered
    /// reputation lowered the number of chances to earn it back, and lowered
    /// it again. Over fifty nights it fell 83 → 25 and never returned. The
    /// author of <c>EncounterResolver</c> saw this coming and kept the Poor
    /// penalty deliberately small to slow the spiral, but nothing was ever
    /// added on the other side of the ledger.
    ///
    /// This is the same fix stress needed and for the same reason: a stat
    /// that only moves one way is a countdown, not a resource.
    ///
    /// Asymmetric, and that is the whole design. A symmetric pull was tried
    /// first and it flattened the game: the ceiling fell from 84 to 60 while
    /// the floor rose, compressing fifty nights of play into a narrow band
    /// around the baseline and making good nights stop mattering. Bad news
    /// fades; an earned reputation does not evaporate on its own. So the pull
    /// upward is real and the pull downward is off by default — what takes a
    /// good house down is bad nights, not amnesia.
    /// </summary>
    private void ApplyReputationMemory()
    {
        var gsm = GameStateManager.Instance;
        if (gsm == null) return;

        var gap = ReputationBaseline - gsm.Reputation;

        var rate = gap > 0f ? ReputationRecoveryRate : ReputationDecayRate;
        if (Mathf.IsZeroApprox(rate)) return;

        var drift = gap * rate;
        if (Mathf.IsZeroApprox(drift)) return;

        gsm.Reputation += drift;
        _report.ReputationDelta += drift;
    }

    /// <summary>
    /// What reputation decays toward — the standing of a house nobody has an
    /// opinion about.
    /// </summary>
    [Export] public float ReputationBaseline { get; set; } = 50f;

    /// <summary>
    /// How much of the gap up to the baseline is closed each night while
    /// reputation is below it. Zero restores the old one-way behaviour where
    /// a slump was permanent.
    /// </summary>
    [Export] public float ReputationRecoveryRate { get; set; } = 0.15f;

    /// <summary>
    /// The same, downward, while reputation is above the baseline. Zero by
    /// default: a reputation the house earned should be taken away by bad
    /// nights, not by the passage of time. Raise it if standing at the top
    /// ever needs to cost something.
    /// </summary>
    [Export] public float ReputationDecayRate { get; set; }

    /// <summary>
    /// Close the Ledger and advance the world by one day, which is what
    /// drives every existing OnDailyTick consumer.
    /// </summary>
    public void ConcludeNight()
    {
        if (_phase != NightPhase.Ledger) return;

        GameStateManager.Instance?.AdvanceDay();

        // The human layer moves on a weekly rhythm so neglect is something the
        // player can notice and correct rather than a nightly drip.
        var day = GameStateManager.Instance?.DayCount ?? 0;
        if (day > 0 && day % 7 == 0)
            StaffRoster.Instance?.RunWeeklyReview();

        SetPhase(NightPhase.Idle);
    }

    // ── Arrivals ───────────────────────────────────────────────────────

    /// <summary>
    /// How many clients the house draws tonight. Scales with Reputation and
    /// the macro footfall multiplier — the latter was computed by
    /// MacroEconomyEngine and read by nothing before this.
    /// </summary>
    private int RollArrivalCount()
    {
        var gsm = GameStateManager.Instance;
        var reputation = gsm?.Reputation ?? 50f;

        float footfall = 1f;
        var macro = GetTree()?.Root?.FindChild("MacroEconomyEngine", true, false) as MacroEconomyEngine;
        if (macro != null) footfall = macro.FootfallMultiplier;

        float expected = BaseArrivalsPerNight
                       * (0.5f + reputation / 100f)
                       * Mathf.Max(0.1f, footfall);

        // Bookable rooms cap what the house can realistically host.
        var venue = FindVenue();
        int capacity = Mathf.Max(1, (venue?.GetBookableRoomCount() ?? 1) * MaxEncountersPerStaff);

        return Mathf.Clamp(Mathf.RoundToInt(expected + (_rng.Randf() * 4f - 2f)), 0, capacity);
    }

    /// <summary>
    /// Spread arrivals across the Service beat rather than dropping them all
    /// at once, so the night has a shape the player can respond to.
    /// </summary>
    private void TickArrivals(float delta)
    {
        if (_arrivalsRemaining <= 0) return;

        _nextArrivalIn -= delta;
        if (_nextArrivalIn > 0f) return;

        // Remaining time divided by remaining guests, jittered.
        float remainingTime = Mathf.Max(1f, ServiceDurationSeconds - _serviceElapsed);
        float meanGap = remainingTime / Mathf.Max(1, _arrivalsRemaining);
        _nextArrivalIn = meanGap * (0.5f + _rng.Randf());

        AdmitClient();
    }

    private void AdmitClient()
    {
        // A known client walking back in takes precedence over a stranger.
        // This is what makes good service compound instead of evaporating at
        // the end of each night.
        var regulars = FindRegulars();
        var returning = regulars?.RollReturningClient();

        var client = returning != null
            ? regulars.BuildProfile(returning)
            : ClientNegotiationHandler.GenerateRandomClient();

        _arrivalsRemaining--;
        _report.ClientsArrived++;

        if (returning != null)
        {
            _lobbyPatrons[client] = returning.Id;
            _report.ReturningClients++;
        }

        if (_lobby.Count >= MaxLobbySize)
        {
            _report.ClientsTurnedAway++;
            EmitSignal(SignalName.OnClientTurnedAway, client.Name, "lobby full");
            return;
        }

        // A well-appointed ground floor raises what every client will spend
        // before they ever go upstairs.
        var venue = FindVenue();
        if (venue != null)
        {
            float upsell = venue.GetUpsellMultiplier();
            client.Budget *= upsell;
            client.FairPrice *= upsell;
        }

        _lobby.Add(client);
        EmitSignal(SignalName.OnClientArrived, client.Name, (int)client.PreferredRoom);
    }

    // ── Dispatch ───────────────────────────────────────────────────────

    /// <summary>
    /// Match waiting clients to a free posted staff member in a room that
    /// meets their expectation.
    /// </summary>
    private void TickDispatch()
    {
        if (_lobby.Count == 0) return;

        var venue = FindVenue();
        var roster = StaffRoster.Instance;
        if (venue == null || roster == null) return;

        for (int i = _lobby.Count - 1; i >= 0; i--)
        {
            var client = _lobby[i];

            var assignment = _assignments.FirstOrDefault(a =>
                !a.IsBusy &&
                a.EncountersWorked < MaxEncountersPerStaff &&
                MeetsExpectation(venue, a.RoomTile, client));

            if (assignment == null) continue;

            var staff = roster.GetById(assignment.StaffId);
            if (staff == null) continue;

            var room = venue.GetRoom(assignment.RoomTile);
            if (room == null) continue;

            _lobby.RemoveAt(i);
            StartEncounter(venue, assignment, staff, room, client);
        }
    }

    /// <summary>
    /// Whether a room clears the bar this client set. Gating on Appointment
    /// rather than a raw discretion field is what makes furniture spend
    /// actually decide who the house can serve.
    /// </summary>
    private static bool MeetsExpectation(VenueBuilding venue, Vector3I tile, ClientProfile client)
    {
        var appointment = venue.GetRoomAppointment(tile);

        // A little slack — clients will accept slightly under par, grudgingly.
        return appointment >= client.ExpectedAppointment - 12f;
    }

    private void StartEncounter(
        VenueBuilding venue, ShiftAssignment assignment,
        StaffMember staff, RoomModule room, ClientProfile client)
    {
        assignment.IsBusy = true;

        var price = venue.GetSuggestedPrice(room, BaseRoomPrice);
        price = Math.Min(price, client.Budget);

        var duration = EncounterDurationSeconds * (0.8f + _rng.Randf() * 0.4f);

        _lobbyPatrons.TryGetValue(client, out var patronId);
        _lobbyPatrons.Remove(client);

        _encounters.Add(new ActiveEncounter
        {
            StaffId = staff.Id,
            RoomTile = room.GridPosition,
            Client = client,
            PatronId = patronId,
            AgreedPrice = price,
            TimeRemaining = duration,
            TotalDuration = duration
        });

        EmitSignal(SignalName.OnEncounterStarted,
            staff.Id, room.GridPosition.X, room.GridPosition.Y, room.GridPosition.Z, duration);
    }

    // ── Resolution ─────────────────────────────────────────────────────

    private void TickEncounters(float delta)
    {
        if (_encounters.Count == 0) return;

        for (int i = _encounters.Count - 1; i >= 0; i--)
        {
            _encounters[i].TimeRemaining -= delta;
            if (_encounters[i].TimeRemaining > 0f) continue;

            var encounter = _encounters[i];
            _encounters.RemoveAt(i);
            ResolveEncounter(encounter);
        }
    }

    private void ResolveEncounter(ActiveEncounter encounter)
    {
        var venue = FindVenue();
        var roster = StaffRoster.Instance;

        var staff = roster?.GetById(encounter.StaffId);
        var room = venue?.GetRoom(encounter.RoomTile);

        var assignment = _assignments.FirstOrDefault(a => a.StaffId == encounter.StaffId);
        if (assignment != null)
        {
            assignment.IsBusy = false;
            assignment.EncountersWorked++;
        }

        if (venue == null || room == null) return;

        float appointment = venue.GetRoomAppointment(encounter.RoomTile);
        float discretion = venue.GetEffectiveDiscretion(encounter.RoomTile);
        bool styleMatched = venue.GetRoomStyle(encounter.RoomTile) == encounter.Client.PreferredStyle;

        float spend = 1f;
        var macro = GetTree()?.Root?.FindChild("MacroEconomyEngine", true, false) as MacroEconomyEngine;
        if (macro != null) spend = macro.ClientSpendMultiplier;

        var outcome = EncounterResolver.Resolve(
            appointment,
            encounter.Client.ExpectedAppointment,
            styleMatched,
            discretion,
            staff,
            encounter.AgreedPrice,
            spend,
            _rng);

        ApplyOutcome(outcome, staff, room, encounter);

        EmitSignal(SignalName.OnEncounterResolved,
            encounter.StaffId, (int)outcome.Quality, outcome.Payment, (int)outcome.Incident);
    }

    /// <summary>Commit an outcome to the world.</summary>
    private void ApplyOutcome(
        EncounterOutcome outcome, StaffMember staff, RoomModule room, ActiveEncounter encounter)
    {
        var gsm = GameStateManager.Instance;
        var venue = FindVenue();

        // ── Money ──────────────────────────────────────────────────────
        if (outcome.Payment > 0)
        {
            _report.Revenue += outcome.Payment;

            // Routed through the ledger, not straight onto Cash — every other
            // money-maker in this codebase bypassed it, which is why the
            // revenue categories were always empty.
            var category = room.Type == RoomType.VIPSuite
                ? RevenueCategory.VIPServices
                : RevenueCategory.ClientFees;

            RecordRevenue(category, outcome.Payment,
                $"Night {_report.Night}: {encounter.Client.Name} in {room.RoomName}");

            // The earner's cut, booked immediately against the same encounter
            // so the Ledger screen can show gross and net side by side.
            var commission = Math.Round(outcome.Payment * GetCommissionRate(), 2);
            if (commission > 0)
            {
                _report.StaffCommission += commission;
                ChargeExpense(ExpenseCategory.StaffSalaries, commission,
                    $"Night {_report.Night}: {staff?.StaffName ?? "house"} commission");

                // Being paid well is the most direct route to a Money ambition.
                if (staff?.Ambition == StaffAmbition.Money)
                    staff.AdvanceAmbition((float)(commission / 120.0));
            }
        }

        // ── Staff ──────────────────────────────────────────────────────
        if (staff != null)
        {
            staff.Stress += outcome.StaffStressDelta;

            if (outcome.StaffTraumaDelta > 0f)
            {
                var breakSystem = GetTree()?.Root?.FindChild(
                    "PsychologicalBreakSystem", true, false) as PsychologicalBreakSystem;

                // Attribute the source rather than letting the break system
                // infer it — the displaced-aggression mapping is the whole
                // point, and inference collapses it to a single outcome.
                breakSystem?.RecordTraumaEvent(
                    staff, TraumaSource.ClientAbuse, outcome.StaffTraumaDelta / 20f);

                staff.Trauma += outcome.StaffTraumaDelta;
            }

            // Good nights build commitment; bad ones erode it. Adequate is
            // deliberately neutral — it is the modal outcome and the intended
            // baseline, so treating it as an erosion made loyalty fall on
            // every ordinary night and collapsed the roster by night twenty.
            var loyaltyDelta = outcome.Quality switch
            {
                EncounterQuality.Exceptional => 1.5f,
                EncounterQuality.Good => 0.8f,
                EncounterQuality.Adequate => 0f,
                EncounterQuality.Poor => -0.8f,
                _ => -2.5f
            };

            if (!Mathf.IsZeroApprox(loyaltyDelta))
                staff.AdjustLoyalty(loyaltyDelta, "encounter");

            if (outcome.Quality >= EncounterQuality.Good &&
                staff.Ambition == StaffAmbition.Status)
                staff.AdvanceAmbition(2f);
        }

        // ── World ──────────────────────────────────────────────────────
        if (gsm != null)
        {
            if (!Mathf.IsZeroApprox(outcome.ReputationDelta))
            {
                gsm.Reputation += outcome.ReputationDelta;
                _report.ReputationDelta += outcome.ReputationDelta;
            }

            if (!Mathf.IsZeroApprox(outcome.HeatDelta))
            {
                gsm.Heat += outcome.HeatDelta;
                _report.HeatDelta += outcome.HeatDelta;
            }
        }

        // Furniture wears with use — high-traffic rooms degrade fastest.
        venue?.ApplyRoomUsage(encounter.RoomTile, 1f);

        // ── Follow-ons ─────────────────────────────────────────────────
        if (outcome.IntelOpportunity && staff != null)
        {
            var blackmail = GetTree()?.Root?.FindChild("BlackmailNetwork", true, false) as BlackmailNetwork;

            if (blackmail?.AttemptIntelGathering(staff, encounter.Client.Name) == true &&
                !string.IsNullOrEmpty(encounter.PatronId))
            {
                // Intel on a stranger is worth little; intel on a patron is
                // the leverage the political layer actually runs on.
                FindRegulars()?.MarkIntelGathered(encounter.PatronId);
            }
        }

        RecordPatronVisit(encounter, outcome);

        // ── Report ─────────────────────────────────────────────────────
        _report.ClientsServed++;
        _report.QualityCounts[outcome.Quality] =
            _report.QualityCounts.GetValueOrDefault(outcome.Quality) + 1;

        if (outcome.Incident != EncounterIncident.None)
            _report.Incidents.Add($"{staff?.StaffName ?? "Unstaffed"}: {outcome.Narrative}");
        else if (outcome.Quality == EncounterQuality.Exceptional)
            _report.Highlights.Add($"{staff?.StaffName}: {outcome.Narrative}");
    }

    /// <summary>
    /// End-of-night effects that depend on the whole shift rather than a
    /// single encounter: colleague friction and the accumulated workload.
    /// </summary>
    private void ApplySocialAndShiftEffects()
    {
        var roster = StaffRoster.Instance;
        var venue = FindVenue();
        if (roster == null) return;

        foreach (var assignment in _assignments)
        {
            var staff = roster.GetById(assignment.StaffId);
            if (staff == null) continue;

            // Who else worked this floor tonight.
            var floor = assignment.RoomTile.Z;
            var colleagues = _assignments
                .Where(a => a.RoomTile.Z == floor && a.StaffId != assignment.StaffId)
                .Select(a => a.StaffId);

            var social = roster.GetSocialStressModifier(assignment.StaffId, colleagues);
            if (!Mathf.IsZeroApprox(social)) staff.Stress += social;

            // A staff member posted all night to an empty room is bored, not rested.
            if (assignment.EncountersWorked == 0)
                staff.Satisfaction -= 2f;

            // Bonded pairs grow closer by working together.
            foreach (var colleagueId in colleagues)
            {
                if (staff.IsBondedWith(colleagueId))
                    roster.AdjustRelationship(assignment.StaffId, colleagueId, 1f);
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private VenueBuilding FindVenue() =>
        GetTree()?.Root?.FindChild("VenueBuilding", true, false) as VenueBuilding;

    private RegularsRegistry FindRegulars() =>
        GetTree()?.Root?.FindChild("RegularsRegistry", true, false) as RegularsRegistry;

    /// <summary>
    /// Write the encounter into the client's history, creating or promoting
    /// their record. A stranger who did not enjoy themselves is forgotten;
    /// anyone else becomes someone the house can build on.
    /// </summary>
    private void RecordPatronVisit(ActiveEncounter encounter, EncounterOutcome outcome)
    {
        var regulars = FindRegulars();
        if (regulars == null)
        {
            if (outcome.BecameRegular) _report.NewRegulars++;
            return;
        }

        // Reuse the encounter score as the client's read on the night.
        var before = regulars.GetById(encounter.PatronId)?.Standing ?? PatronStanding.FirstTime;

        var patron = regulars.RecordVisit(
            encounter.PatronId, encounter.Client, outcome.Payment,
            outcome.Score, outcome.BecameRegular || !string.IsNullOrEmpty(encounter.PatronId));

        if (patron == null) return;

        if (before == PatronStanding.FirstTime && patron.Standing != PatronStanding.FirstTime)
            _report.NewRegulars++;

        if (before != PatronStanding.Patron && patron.Standing == PatronStanding.Patron)
            _report.Highlights.Add($"{patron.Name} has become a patron of the house.");
    }

    /// <summary>
    /// Commission rate after policy. The exploitation branch suppresses the
    /// earner's cut — cheaper per night, and paid for in Loyalty and Stress
    /// through the modifiers those policies already set.
    /// </summary>
    private double GetCommissionRate()
    {
        var policies = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        if (policies == null) return StaffCommissionRate;

        return policies.ActiveBranch switch
        {
            PolicyBranch.SystemicExploitation => StaffCommissionRate * 0.55,
            PolicyBranch.WorkforceProtection => StaffCommissionRate * 1.15,
            _ => StaffCommissionRate
        };
    }

    private void RecordRevenue(RevenueCategory category, double amount, string description)
    {
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;

        if (ledger != null)
            ledger.RecordRevenue(category, amount, description);
        else if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash += amount;
    }

    private void ChargeExpense(ExpenseCategory category, double amount, string description)
    {
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;

        if (ledger != null)
            ledger.RecordExpense(category, amount, description);
        else if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= amount;
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "night";

    /// <summary>
    /// Only between-night state is saved. Saving mid-encounter would mean
    /// persisting in-flight timers and client objects for a beat the player
    /// cannot meaningfully resume into, so a load always lands cleanly
    /// between nights.
    /// </summary>
    public JsonObject CaptureState()
    {
        var assignments = new JsonArray();
        foreach (var a in _assignments)
        {
            assignments.Add(new JsonObject
            {
                ["staffId"] = a.StaffId,
                ["x"] = a.RoomTile.X,
                ["y"] = a.RoomTile.Y,
                ["floor"] = a.RoomTile.Z
            });
        }

        return new JsonObject
        {
            ["phase"] = (_phase is NightPhase.Service or NightPhase.Closing
                ? NightPhase.Preparation
                : _phase).ToString(),
            ["assignments"] = assignments
        };
    }

    public void RestoreState(JsonObject state)
    {
        if (state == null) return;

        _encounters.Clear();
        _lobby.Clear();
        _assignments.Clear();
        _serviceElapsed = 0f;

        if (Enum.TryParse<NightPhase>((string)state["phase"], true, out var phase))
            _phase = phase is NightPhase.Service or NightPhase.Closing ? NightPhase.Preparation : phase;

        if (state["assignments"] is JsonArray assignments)
        {
            foreach (var node in assignments)
            {
                if (node is not JsonObject entry) continue;

                _assignments.Add(new ShiftAssignment
                {
                    StaffId = (string)entry["staffId"],
                    RoomTile = new Vector3I(
                        (int?)entry["x"] ?? 0,
                        (int?)entry["y"] ?? 0,
                        (int?)entry["floor"] ?? 0)
                });
            }
        }

        _report = new NightReport { Night = GameStateManager.Instance?.DayCount ?? 0 };

        GD.Print($"[NightDirector] Restored in {_phase} with {_assignments.Count} assignments.");
    }

    public override string ToString() =>
        $"[NightDirector] {_phase} — lobby {_lobby.Count}, " +
        $"active {_encounters.Count}, served {_report.ClientsServed}";
}
