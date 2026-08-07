using Godot;
using System;
using System.Collections.Generic;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>Root cause categories for staff psychological trauma.</summary>
public enum TraumaSource
{
    /// <summary>No significant trauma source identified.</summary>
    None,

    /// <summary>Trauma from client mistreatment, verbal abuse, or physical assault.</summary>
    ClientAbuse,

    /// <summary>Trauma from financial stress — debt, wage garnishment, threats from creditors.</summary>
    FinancialDebt,

    /// <summary>Trauma from police raids, arrests, or law-enforcement intimidation.</summary>
    PoliceAction,

    /// <summary>Trauma from coworker conflict or management abuse.</summary>
    WorkplaceHostility
}

/// <summary>Possible psychological break event outcomes.</summary>
public enum BreakEventType
{
    None,
    ClientAssault,
    CorporateSabotage,
    SelfDestructiveBehavior,
    ViolentOutburst
}

/// <summary>Data payload describing a triggered break event.</summary>
public class BreakEvent
{
    public StaffMember Staff { get; set; }
    public BreakEventType EventType { get; set; }
    public TraumaSource Source { get; set; }
    public float StressLevel { get; set; }
    public float TraumaLevel { get; set; }
    public int Day { get; set; }
    public string Narrative { get; set; }

    public override string ToString() =>
        $"[Day {Day}] {Staff?.StaffName}: {EventType} " +
        $"(Stress={StressLevel:F0} Trauma={TraumaLevel:F0} Source={Source})";
}

// ── PsychologicalBreakSystem Node ──────────────────────────────────────

/// <summary>
/// Monitors staff mental state and triggers contextual psychological
/// break events based on displaced aggression mechanics.
///
/// When Stress exceeds 80, the system evaluates the dominant trauma
/// source and triggers a matching event instead of random action:
///   ClientAbuse trauma  →  ClientAssaultEvent (staff attacks a client)
///   FinancialDebt trauma →  CorporateSabotage (staff leaks data, Heat +25)
///   PoliceAction trauma  →  SelfDestructiveBehavior
///   WorkplaceHostility    →  ViolentOutburst
///
/// Usage: Add as a child of GameStateManager. Register staff via
///   RegisterStaff() and record trauma via RecordTraumaEvent().
/// </summary>
public partial class PsychologicalBreakSystem : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a psychological break is triggered.</summary>
    [Signal]
    public delegate void OnPsychologicalBreakEventHandler(
        string staffName,
        int eventType,
        int traumaSource,
        float stressLevel,
        float traumaLevel,
        int day,
        string narrative);

    /// <summary>Fired specifically for ClientAssault events.</summary>
    [Signal]
    public delegate void OnClientAssaultEventHandler(
        StaffMember staff, float stress, float trauma);

    /// <summary>Fired specifically for CorporateSabotage events (Heat +25).</summary>
    [Signal]
    public delegate void OnCorporateSabotageEventHandler(
        StaffMember staff, float heatIncrease);

    /// <summary>Fired when a break is narrowly avoided (stress 70–80).</summary>
    [Signal]
    public delegate void OnNearBreakEventHandler(StaffMember staff, float stress);

    // ── State ──────────────────────────────────────────────────────────

    /// <summary>Registered staff members to monitor.</summary>
    private readonly List<StaffMember> _staffRegistry = new();

    /// <summary>Dominant trauma source per staff member.</summary>
    private readonly Dictionary<StaffMember, TraumaSource> _traumaSources = new();

    /// <summary>Cooldown between psychological breaks per staff (days).</summary>
    private readonly Dictionary<StaffMember, int> _breakCooldowns = new();

    /// <summary>Break history for analysis.</summary>
    private readonly List<BreakEvent> _breakHistory = new();

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Stress threshold for break check.</summary>
    public float BreakThreshold { get; set; } = 80.0f;

    /// <summary>Base probability of a break when above threshold, per tick.</summary>
    public float BaseBreakProbability { get; set; } = 0.30f;

    /// <summary>Additional probability per point of stress above threshold.</summary>
    public float ProbabilityPerPointOverThreshold { get; set; } = 0.03f;

    /// <summary>Maximum break probability cap.</summary>
    public float MaxBreakProbability { get; set; } = 0.90f;

    /// <summary>Days a staff member is immune after a break event.</summary>
    public int BreakCooldownDays { get; set; } = 3;

    /// <summary>Whether the system is actively monitoring.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Read-only view of break history.</summary>
    public IReadOnlyList<BreakEvent> BreakHistory => _breakHistory;

    /// <summary>Total breaks triggered since session start.</summary>
    public int TotalBreaks => _breakHistory.Count;

    // ── Lifecycle ──────────────────────────────────────────────────────

    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        _rng.Randomize();

        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
            GD.Print("[PsychBreak] Connected to GameStateManager.OnDailyTick.");
        }
        else
        {
            GD.PrintErr("[PsychBreak] GameStateManager.Instance is null.");
        }

        GD.Print($"[PsychBreak] Initialized. Threshold={BreakThreshold:F0} " +
                 $"BaseProb={BaseBreakProbability:P0} Cooldown={BreakCooldownDays}d");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Staff Registration ─────────────────────────────────────────────

    /// <summary>Register a staff member for monitoring.</summary>
    public void RegisterStaff(StaffMember staff)
    {
        if (staff == null || _staffRegistry.Contains(staff)) return;
        _staffRegistry.Add(staff);

        // Listen to staff agency changes to auto-detect trauma buildup
        staff.OnAgencyChanged += (name, value, delta) =>
        {
            if (name == "Stress" && value >= BreakThreshold)
                OnStaffStressCritical(staff, value);
        };

        GD.Print($"[PsychBreak] Registered staff: {staff.StaffName}");
    }

    /// <summary>Remove a staff member from monitoring.</summary>
    public void UnregisterStaff(StaffMember staff)
    {
        _staffRegistry.Remove(staff);
        _traumaSources.Remove(staff);
        _breakCooldowns.Remove(staff);
        GD.Print($"[PsychBreak] Unregistered staff: {staff.StaffName}");
    }

    /// <summary>
    /// Record a traumatic event with its source category.
    /// Updates the dominant trauma source for the staff member.
    /// </summary>
    public void RecordTraumaEvent(StaffMember staff, TraumaSource source, float intensity)
    {
        if (staff == null) return;

        // Update or set dominant trauma source
        // Higher intensity sources override lower ones
        if (!_traumaSources.ContainsKey(staff) ||
            intensity > 15f || // significant event
            _traumaSources[staff] == TraumaSource.None)
        {
            _traumaSources[staff] = source;
        }
        // If multiple sources, the most recent high-intensity one wins

        GD.Print($"[PsychBreak] Recorded trauma for {staff.StaffName}: " +
                 $"{source} (intensity={intensity:F1})");
    }

    // ── Daily Tick Evaluation ──────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float publicSentiment)
    {
        if (!IsActive) return;

        int day = GameStateManager.Instance?.DayCount ?? 0;

        ApplyDailyRecovery();

        foreach (var staff in _staffRegistry)
        {
            // Skip staff in cooldown
            if (_breakCooldowns.TryGetValue(staff, out int cooldownEnd) && day < cooldownEnd)
                continue;

            // Near-miss warning zone (70–80)
            if (staff.Stress >= 70f && staff.Stress < BreakThreshold)
            {
                EmitSignal(SignalName.OnNearBreak, staff, staff.Stress);
                continue;
            }

            // Break check zone (≥ 80)
            if (staff.Stress >= BreakThreshold)
            {
                EvaluateBreak(staff, day);
            }
        }
    }

    // ── Daily Recovery ─────────────────────────────────────────────────

    /// <summary>
    /// Stress recovered per day by an unmodified staff member resting
    /// between nights.
    ///
    /// Calibrated to sit slightly *below* a normal night's load. A staff
    /// member working four Adequate encounters takes on roughly 18 stress, so
    /// recovery of 15 means a working night still accumulates a few points
    /// while an idle or light one repays them.
    ///
    /// The margin matters in both directions. At the original 6, stress
    /// climbed ~19/night, saturated by night five and dragged every encounter
    /// into the Disastrous band. At 20 it cancelled the load outright and
    /// stress sat at zero forever, which made this whole system inert. The
    /// point is a resource the player has to actively manage — by rotating
    /// staff, hiring depth, or taking the Medical Care policy.
    /// </summary>
    public float BaseDailyStressRecovery { get; set; } = 15.0f;

    /// <summary>
    /// Rest and policy effects, applied before the break checks.
    ///
    /// Without this there was no stress decay anywhere in the game — stress
    /// only ever rose, from shifts, so every staff member eventually crossed
    /// the break threshold no matter how well the house was run. Recovery is
    /// what makes staffing a manageable rhythm rather than a countdown.
    ///
    /// This is also the consumer for three PolicyTreeManager modifiers
    /// (StressDecayMultiplier, PermanentStaffStressModifier,
    /// PermanentStaffSatisfactionModifier) that were computed on enactment
    /// and then read by nothing, so half the policy tree had no felt effect.
    /// </summary>
    private void ApplyDailyRecovery()
    {
        var policies = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;

        var decayMultiplier = policies?.StressDecayMultiplier ?? 1.0f;
        var stressModifier = policies?.PermanentStaffStressModifier ?? 0f;
        var satisfactionModifier = policies?.PermanentStaffSatisfactionModifier ?? 0f;

        // Net daily stress change: rest recovers, harsh policy adds.
        var netStress = (BaseDailyStressRecovery * Mathf.Max(0f, decayMultiplier)) - stressModifier;

        // Satisfaction modifiers are a standing offset rather than a one-shot
        // bonus, so they are applied as a gentle daily drift toward the level
        // the policy implies. A +10 policy takes roughly ten days to land.
        var satisfactionDrift = satisfactionModifier * 0.1f;

        foreach (var staff in _staffRegistry)
        {
            if (!Mathf.IsZeroApprox(netStress))
                staff.Stress -= netStress;

            if (!Mathf.IsZeroApprox(satisfactionDrift))
                staff.Satisfaction += satisfactionDrift;
        }
    }

    // ── Break Evaluation ───────────────────────────────────────────────

    private void EvaluateBreak(StaffMember staff, int day)
    {
        float pointsOver = staff.Stress - BreakThreshold;
        float probability = Mathf.Clamp(
            BaseBreakProbability + (pointsOver * ProbabilityPerPointOverThreshold),
            0f,
            MaxBreakProbability);

        float roll = _rng.Randf();

        if (roll >= probability)
        {
            GD.Print($"[PsychBreak] {staff.StaffName}: No break (roll={roll:F3} > prob={probability:F3})");
            return;
        }

        // Determine trauma source — fallback chain
        TraumaSource source = DetermineTraumaSource(staff);

        // Map source → event type using displaced aggression mechanics
        BreakEventType eventType = MapSourceToEvent(source, staff);

        // Build and emit the event
        var breakEvent = new BreakEvent
        {
            Staff = staff,
            EventType = eventType,
            Source = source,
            StressLevel = staff.Stress,
            TraumaLevel = staff.Trauma,
            Day = day,
            Narrative = GenerateNarrative(staff, eventType, source)
        };

        _breakHistory.Add(breakEvent);
        _breakCooldowns[staff] = day + BreakCooldownDays;

        GD.Print($"[PsychBreak] BREAK: {breakEvent}");

        EmitSignal(SignalName.OnPsychologicalBreak,
            breakEvent.Staff?.StaffName ?? "Unknown",
            (int)eventType,
            (int)source,
            breakEvent.StressLevel,
            breakEvent.TraumaLevel,
            breakEvent.Day,
            breakEvent.Narrative);

        // Execute event-specific effects
        ExecuteBreakEffects(breakEvent);
    }

    // ── Trauma Source Determination ────────────────────────────────────

    /// <summary>
    /// Determine the dominant trauma source for a staff member.
    /// Uses the recorded source if available, otherwise infers from
    /// environmental context.
    /// </summary>
    private TraumaSource DetermineTraumaSource(StaffMember staff)
    {
        // 1. Check recorded trauma source
        if (_traumaSources.TryGetValue(staff, out var recorded) && recorded != TraumaSource.None)
            return recorded;

        // 2. Infer from environmental context
        if (GameStateManager.Instance != null && GameStateManager.Instance.Heat > 50f)
            return TraumaSource.PoliceAction;

        // 3. Check financial stress (negative cash flow)
        if (GameStateManager.Instance != null && GameStateManager.Instance.Cash < 200)
            return TraumaSource.FinancialDebt;

        // 4. Default: client-facing work is the primary interaction
        return TraumaSource.ClientAbuse;
    }

    /// <summary>
    /// Map a trauma source to the appropriate break event type
    /// using displaced aggression mechanics.
    /// </summary>
    private BreakEventType MapSourceToEvent(TraumaSource source, StaffMember staff)
    {
        return source switch
        {
            // Client abuse → staff displaces aggression onto a client
            TraumaSource.ClientAbuse => BreakEventType.ClientAssault,

            // Financial debt → staff sabotages the business, leaking data
            TraumaSource.FinancialDebt => BreakEventType.CorporateSabotage,

            // Police action → staff turns inward, self-destructive behavior
            TraumaSource.PoliceAction => BreakEventType.SelfDestructiveBehavior,

            // Workplace hostility → outward violent outburst
            TraumaSource.WorkplaceHostility => BreakEventType.ViolentOutburst,

            _ => BreakEventType.ClientAssault // safest fallback
        };
    }

    // ── Break Effect Execution ─────────────────────────────────────────

    private void ExecuteBreakEffects(BreakEvent evt)
    {
        var staff = evt.Staff;

        // Stress relief after break (catharsis)
        staff.Stress -= 15f + (_rng.Randf() * 15f);
        // But trauma increases
        staff.Trauma += 5f + (_rng.Randf() * 10f);

        switch (evt.EventType)
        {
            case BreakEventType.ClientAssault:
                ExecuteClientAssault(staff);
                break;

            case BreakEventType.CorporateSabotage:
                ExecuteCorporateSabotage(staff);
                break;

            case BreakEventType.SelfDestructiveBehavior:
                ExecuteSelfDestructive(staff);
                break;

            case BreakEventType.ViolentOutburst:
                ExecuteViolentOutburst(staff);
                break;
        }
    }

    /// <summary>
    /// ClientAssaultEvent: Staff attacks a client.
    /// Heavy reputation damage, client injury, potential lawsuit.
    /// </summary>
    private void ExecuteClientAssault(StaffMember staff)
    {
        EmitSignal(SignalName.OnClientAssault, staff, staff.Stress, staff.Trauma);

        // Reputation nosedive
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Reputation -= 10f + (_rng.Randf() * 15f);

        // Heat spike (police called)
        var heatSystem = FindHeatSystem();
        if (heatSystem != null)
            heatSystem.AddHeat(15f + (_rng.Randf() * 10f));

        // Financial liability — settlement or medical costs
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= 500 + (_rng.Randi() % 1501); // $500–$2000

        GD.Print($"[PsychBreak] ClientAssault: {staff.StaffName} attacked a client. " +
                 $"Rep damage, Heat spike, financial liability.");
    }

    /// <summary>
    /// CorporateSabotage: Staff leaks sensitive financial data.
    /// Massive Heat increase (+25), potential criminal investigation.
    /// </summary>
    private void ExecuteCorporateSabotage(StaffMember staff)
    {
        float heatIncrease = 25f;

        EmitSignal(SignalName.OnCorporateSabotage, staff, heatIncrease);

        // Critical: Heat +25 as specified
        var heatSystem = FindHeatSystem();
        if (heatSystem != null)
            heatSystem.AddHeat(heatIncrease);

        // Financial data leak — lose some cash as accounts are frozen/audited
        if (GameStateManager.Instance != null)
        {
            double lostCash = GameStateManager.Instance.Cash * (0.10 + _rng.Randf() * 0.15); // 10–25%
            GameStateManager.Instance.Cash -= lostCash;
        }

        // Public sentiment hit from scandal
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.PublicSentiment -= 8f + (_rng.Randf() * 12f);

        GD.Print($"[PsychBreak] CorporateSabotage: {staff.StaffName} leaked ledger data! " +
                 $"Heat +{heatIncrease:F0}, finances compromised.");
    }

    /// <summary>
    /// SelfDestructiveBehavior: Staff harms themselves.
    /// Extended absence, morale impact on team.
    /// </summary>
    private void ExecuteSelfDestructive(StaffMember staff)
    {
        // Staff is unavailable for several days (modeled as trauma spike)
        staff.Trauma += 10f;
        staff.Stress -= 30f; // Crisis intervention reduces stress but trauma remains

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Reputation -= 3f;

        GD.Print($"[PsychBreak] SelfDestructive: {staff.StaffName} requires intervention. " +
                 $"Trauma +10, extended absence.");
    }

    /// <summary>
    /// ViolentOutburst: Staff destroys property or assaults coworkers.
    /// Facility damage, team morale collapse.
    /// </summary>
    private void ExecuteViolentOutburst(StaffMember staff)
    {
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.Reputation -= 5f + (_rng.Randf() * 10f);
            GameStateManager.Instance.Cash -= 300 + (_rng.Randi() % 701); // $300–$1000 damages
        }

        GD.Print($"[PsychBreak] ViolentOutburst: {staff.StaffName} caused property damage. " +
                 $"Facility repairs needed.");
    }

    // ── Narrative Generation ───────────────────────────────────────────

    private string GenerateNarrative(StaffMember staff, BreakEventType type, TraumaSource source)
    {
        return type switch
        {
            BreakEventType.ClientAssault =>
                $"{staff.StaffName}, pushed past the breaking point by repeated {source.ToString().ToLower()}, " +
                "lashed out at a client in a sudden fit of displaced aggression. Security had to intervene.",

            BreakEventType.CorporateSabotage =>
                $"Overwhelmed by {source.ToString().ToLower()}, {staff.StaffName} covertly leaked sensitive " +
                "financial records to authorities and rival establishments. The ledger is compromised.",

            BreakEventType.SelfDestructiveBehavior =>
                $"The weight of accumulated {source.ToString().ToLower()} became too much. " +
                $"{staff.StaffName} withdrew completely and requires immediate support.",

            BreakEventType.ViolentOutburst =>
                $"After enduring relentless {source.ToString().ToLower()}, {staff.StaffName} erupted " +
                "in a violent outburst, destroying property and threatening coworkers.",

            _ => $"{staff.StaffName} suffered a psychological break due to {source}."
        };
    }

    // ── Utility ────────────────────────────────────────────────────────

    private void OnStaffStressCritical(StaffMember staff, float stress)
    {
        GD.Print($"[PsychBreak] ⚠ {staff.StaffName} stress critical: {stress:F0}/{BreakThreshold:F0}");
    }

    /// <summary>Find HeatSystem in the scene tree.</summary>
    private HeatSystem FindHeatSystem()
    {
        return GetTree()?.Root?.FindChild("HeatSystem", recursive: true, owned: false) as HeatSystem;
    }

    /// <summary>
    /// Get the recorded trauma source for a staff member.
    /// </summary>
    public TraumaSource GetTraumaSource(StaffMember staff)
    {
        return _traumaSources.TryGetValue(staff, out var source) ? source : TraumaSource.None;
    }

    /// <summary>Get all staff currently above the break threshold.</summary>
    public List<StaffMember> GetStaffAtRisk()
    {
        var atRisk = new List<StaffMember>();
        foreach (var staff in _staffRegistry)
        {
            if (staff.Stress >= BreakThreshold)
                atRisk.Add(staff);
        }
        return atRisk;
    }

    public override string ToString()
    {
        return $"[PsychBreak] Active={IsActive} Staff={_staffRegistry.Count} " +
               $"AtRisk={GetStaffAtRisk().Count} Breaks={TotalBreaks}";
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "psychBreak";

    /// <summary>
    /// Capture break history and cooldowns.
    ///
    /// Staff are referenced by Id, never by object — the roster rebuilds its
    /// StaffMember instances on load, so any stored reference would point at
    /// a discarded object. SaveLoadSystem restores the roster before systems
    /// precisely so these Ids resolve.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        var history = new System.Text.Json.Nodes.JsonArray();
        foreach (var e in _breakHistory)
        {
            history.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["staffId"] = e.Staff?.Id ?? "",
                ["eventType"] = e.EventType.ToString(),
                ["source"] = e.Source.ToString(),
                ["stressLevel"] = e.StressLevel,
                ["traumaLevel"] = e.TraumaLevel,
                ["day"] = e.Day,
                ["narrative"] = e.Narrative
            });
        }

        var cooldowns = new System.Text.Json.Nodes.JsonArray();
        foreach (var kvp in _breakCooldowns)
        {
            if (kvp.Key == null) continue;
            cooldowns.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["staffId"] = kvp.Key.Id,
                ["until"] = kvp.Value
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["history"] = history,
            ["cooldowns"] = cooldowns
        };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        var roster = StaffRoster.Instance;

        _breakHistory.Clear();
        _breakCooldowns.Clear();

        if (state["history"] is System.Text.Json.Nodes.JsonArray history)
        {
            foreach (var node in history)
            {
                if (node is not System.Text.Json.Nodes.JsonObject entry) continue;

                Enum.TryParse<BreakEventType>((string)entry["eventType"], true, out var eventType);
                Enum.TryParse<TraumaSource>((string)entry["source"], true, out var source);

                // A break belonging to someone who has since left is kept with
                // a null Staff — the history is a record of the house, not of
                // the current roster, and the Ledger screen reads it back.
                _breakHistory.Add(new BreakEvent
                {
                    Staff = roster?.GetById((string)entry["staffId"]),
                    EventType = eventType,
                    Source = source,
                    StressLevel = (float?)entry["stressLevel"] ?? 0f,
                    TraumaLevel = (float?)entry["traumaLevel"] ?? 0f,
                    Day = (int?)entry["day"] ?? 0,
                    Narrative = (string)entry["narrative"] ?? ""
                });
            }
        }

        if (state["cooldowns"] is System.Text.Json.Nodes.JsonArray cooldowns)
        {
            foreach (var node in cooldowns)
            {
                if (node is not System.Text.Json.Nodes.JsonObject entry) continue;

                var staff = roster?.GetById((string)entry["staffId"]);
                if (staff == null) continue;

                _breakCooldowns[staff] = (int?)entry["until"] ?? 0;
            }
        }

        GD.Print($"[PsychBreak] Restored: {_breakHistory.Count} breaks on record, " +
                 $"{_breakCooldowns.Count} cooldowns active.");
    }
}
