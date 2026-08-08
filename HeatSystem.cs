using Godot;
using System;

/// <summary>
/// Manages police scrutiny and vice-raid risk for the establishment.
/// Heat (0–100) scales with daily revenue and client tier.
/// Thresholds trigger probability-based police intervention checks
/// on each daily tick. Mitigation via bribes and legal defense.
///
/// Usage: Add as a child of GameStateManager or any Node in the scene tree.
///   It auto-connects to GameStateManager.OnDailyTick.
/// </summary>
public partial class HeatSystem : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────
    /// <summary>Fired when Heat changes by any amount.</summary>
    [Signal]
    public delegate void OnHeatChangedEventHandler(float newValue, float delta);

    /// <summary>Fired when the daily police intervention check runs.</summary>
    [Signal]
    public delegate void OnPoliceInterventionCheckEventHandler(
        bool raidTriggered,
        float heatLevel,
        float roll,
        float threshold);

    /// <summary>Fired when a police raid actually occurs.</summary>
    [Signal]
    public delegate void OnRaidTriggeredEventHandler(float heatLevel);

    /// <summary>Fired when a bribe is paid.</summary>
    [Signal]
    public delegate void OnBribePaidEventHandler(double cost, float heatReduction);

    /// <summary>Fired when legal defense is executed.</summary>
    [Signal]
    public delegate void OnLegalDefenseExecutedEventHandler(double cost, float heatReduction);

    // ── Configuration ──────────────────────────────────────────────────
    private float _heat;

    /// <summary>Current heat level, clamped 0–100.</summary>
    public float Heat
    {
        get => _heat;
        private set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(_heat, clamped)) return;
            var delta = clamped - _heat;
            _heat = clamped;

            // Sync to global state manager
            if (GameStateManager.Instance != null)
                GameStateManager.Instance.Heat = _heat;

            EmitSignal(SignalName.OnHeatChanged, _heat, delta);
        }
    }

    // ── Generation tuning ──────────────────────────────────────────────

    /// <summary>
    /// Base heat generated per $1000 of daily revenue.
    ///
    /// Must out-scale <see cref="DailyDecayRate"/> at a working house or the
    /// police layer never engages at all: at the original 0.5, a $600 night
    /// generated 0.3 heat against 2.0 of decay, so heat sat at zero forever
    /// and raids, bribes and DA influence were unreachable. At 5.0 a modest
    /// house drifts up slowly while a busy one climbs fast — success is what
    /// attracts attention, which is the intended pressure.
    /// </summary>
    public float HeatPerThousandRevenue { get; set; } = 5.0f;

    /// <summary>
    /// Permanent multiplier on heat generation, set by narrative arc endings.
    /// A friendly mayor bends this down for the rest of the campaign; a
    /// hostile one does the opposite. Separate from the policy modifier so
    /// the two stack rather than overwriting each other.
    /// </summary>
    public float CampaignHeatMultiplier { get; set; } = 1.0f;

    /// <summary>Multiplier applied per client tier level.</summary>
    public float[] TierMultipliers { get; set; } = { 1.0f, 1.5f, 2.5f };

    /// <summary>Current average client tier (0-based index).</summary>
    public int CurrentClientTier { get; set; }

    /// <summary>Daily passive heat decay when no revenue is generated.</summary>
    public float DailyDecayRate { get; set; } = 2.0f;

    // ── Police intervention tuning ─────────────────────────────────────

    /// <summary>Heat level at which police checks begin.</summary>
    public float InterventionThreshold { get; set; } = 70.0f;

    /// <summary>Base probability of a raid when above threshold, per tick.</summary>
    public float BaseRaidProbability { get; set; } = 0.15f;

    /// <summary>How much the dice roll is boosted per point above threshold.</summary>
    public float ProbabilityPerPointOverThreshold { get; set; } = 0.01f;

    /// <summary>Maximum raid probability cap.</summary>
    public float MaxRaidProbability { get; set; } = 0.75f;

    // ── State ──────────────────────────────────────────────────────────
    private readonly RandomNumberGenerator _rng = new();

    /// <summary>Total raids triggered since session start.</summary>
    public int TotalRaidsTriggered { get; private set; }

    /// <summary>Total amount spent on bribes.</summary>
    public double TotalBribeSpend { get; private set; }

    /// <summary>Total amount spent on legal defense.</summary>
    public double TotalLegalSpend { get; private set; }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        WorldRandom.Seed(_rng, nameof(HeatSystem));

        // Connect to the global daily tick
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
            GD.Print("[HeatSystem] Connected to GameStateManager.OnDailyTick.");
        }
        else
        {
            GD.PrintErr("[HeatSystem] GameStateManager.Instance is null — daily tick will not fire.");
        }

        GD.Print($"[HeatSystem] Initialized. Heat={_heat:F1} Threshold={InterventionThreshold:F1}");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Tick Handler ─────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float publicSentiment)
    {
        var day = GameStateManager.Instance?.DayCount ?? 0;

        // 1. Real revenue for the night that just ended.
        //    This previously estimated revenue as 10% of current cash, which
        //    made heat a function of the player's bank balance rather than
        //    their activity — and, because cash is essentially always
        //    positive, meant the decay branch below could never run.
        //
        //    Note the day - 1: encounters book revenue while DayCount is still
        //    the old value, and AdvanceDay() increments before emitting this
        //    tick. Reading `day` here queries a night that has not happened
        //    yet, which silently pinned heat at zero and left raids, bribes
        //    and DA influence permanently unreachable.
        double dailyRevenue = GetDailyRevenue(day - 1);

        // 2. Passive decay. Police attention fades on its own; this is
        //    unconditional so that a quiet night is genuinely a way to cool
        //    off. Generation is then added on top, and the net is what moves.
        float decay = _heat > 0f ? DailyDecayRate : 0f;

        // 3. Generation from revenue × client tier, modified by any enacted
        //    policy. PolicyTreeManager.PermanentHeatModifier was previously
        //    computed and read by nothing.
        float tierMultiplier = GetTierMultiplier(CurrentClientTier);
        float policyModifier = 1f + GetPolicyHeatModifier();
        float heatFromRevenue = (float)(dailyRevenue / 1000.0)
                                * HeatPerThousandRevenue
                                * tierMultiplier
                                * Mathf.Max(0f, policyModifier)
                                * Mathf.Max(0f, CampaignHeatMultiplier);

        float net = heatFromRevenue - decay;
        if (!Mathf.IsZeroApprox(net)) Heat += net;

        GD.Print($"[HeatSystem] Day {day}: Revenue=${dailyRevenue:F0} " +
                 $"Tier={CurrentClientTier}(×{tierMultiplier:F1}) Policy(×{policyModifier:F2}) " +
                 $"+{heatFromRevenue:F2} −{decay:F2} → Heat={_heat:F1}");

        // 4. Threshold check: police intervention
        if (_heat > InterventionThreshold)
        {
            CheckPoliceIntervention();
        }
    }

    /// <summary>
    /// Revenue booked for a given day. Falls back to zero when the ledger is
    /// absent, which correctly means "no activity, so heat only decays."
    /// </summary>
    private double GetDailyRevenue(int day)
    {
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;
        return ledger == null ? 0.0 : Math.Max(0.0, ledger.GetRevenueForDay(day));
    }

    /// <summary>
    /// Additive heat-generation modifier from enacted policy, e.g. the
    /// Security Detail policy's −0.3. Zero when no policy tree is present.
    /// </summary>
    private float GetPolicyHeatModifier()
    {
        var policies = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        return policies?.PermanentHeatModifier ?? 0f;
    }

    // ── Police Intervention Logic ──────────────────────────────────────

    /// <summary>
    /// Roll for police intervention. Probability scales with how far
    /// heat exceeds the threshold.
    /// </summary>
    private void CheckPoliceIntervention()
    {
        float pointsOver = _heat - InterventionThreshold;

        // City-wide enforcement climate scales the roll. MacroEconomyEngine
        // already computes this (0.3 in a Boom, 0.9 during a PoliceCrackdown)
        // and warns the player at 80% of the phase elapsed — but nothing read
        // it, so the warning had no teeth. Normalized around the 0.5 baseline
        // of the Stagnation phase so default behaviour is unchanged.
        float enforcement = GetEnforcementStrictness();
        float enforcementScale = Mathf.Clamp(enforcement / 0.5f, 0.5f, 2.0f);

        float probability = Mathf.Clamp(
            (BaseRaidProbability + (pointsOver * ProbabilityPerPointOverThreshold)) * enforcementScale,
            0f,
            MaxRaidProbability);

        float roll = _rng.Randf();

        bool raidTriggered = roll < probability;

        GD.Print($"[HeatSystem] Police check: Heat={_heat:F1} Over={pointsOver:F1} " +
                 $"Enforcement={enforcement:F2}(×{enforcementScale:F2}) " +
                 $"Prob={probability:P1} Roll={roll:F3} → {(raidTriggered ? "RAID!" : "clear")}");

        EmitSignal(SignalName.OnPoliceInterventionCheck, raidTriggered, _heat, roll, probability);

        if (!raidTriggered) return;

        TotalRaidsTriggered++;
        EmitSignal(SignalName.OnRaidTriggered, _heat);

        // Post-raid: heat drops by a chunk (cops got their bust)
        Heat -= 15.0f + (_rng.Randf() * 15.0f);

        // A friendly District Attorney can make the charges go away. This
        // method existed but was never called from the raid path, so
        // cultivating the DA had no observable payoff.
        var charges = GetTree()?.Root?.FindChild("PoliticalInfluenceSystem", true, false)
            as PoliticalInfluenceSystem;
        bool dismissed = charges?.AttemptChargeDismissal() ?? false;

        if (GameStateManager.Instance != null)
        {
            // Dismissed charges blunt the reputational damage but do not erase it —
            // the raid still happened in front of the clients.
            float repLoss = (5.0f + (_rng.Randf() * 10.0f)) * (dismissed ? 0.35f : 1.0f);
            GameStateManager.Instance.Reputation -= repLoss;
        }

        ApplyRaidTraumaToStaff(dismissed);
    }

    /// <summary>
    /// Stamp PoliceAction trauma on the roster after a raid.
    ///
    /// PsychologicalBreakSystem maps trauma sources to distinct break types
    /// (PoliceAction produces self-destructive behaviour rather than a client
    /// assault or sabotage), but <c>RecordTraumaEvent</c> had no callers
    /// anywhere — so the system always fell back to inferring a source, and
    /// that differentiation never actually reached play.
    /// </summary>
    private void ApplyRaidTraumaToStaff(bool chargesDismissed)
    {
        var roster = StaffRoster.Instance;
        var breakSystem = GetTree()?.Root?.FindChild("PsychologicalBreakSystem", true, false)
            as PsychologicalBreakSystem;

        if (roster == null || breakSystem == null) return;

        // A raid that ends in dismissed charges is frightening rather than ruinous.
        float intensity = chargesDismissed ? 0.35f : 0.7f;

        foreach (var staff in roster.GetAll())
        {
            breakSystem.RecordTraumaEvent(staff, TraumaSource.PoliceAction, intensity);

            // Being raided is also a reason to doubt the house.
            staff.AdjustLoyalty(chargesDismissed ? -2f : -6f, "police raid");
        }

        GD.Print($"[HeatSystem] Raid trauma applied to {roster.Count} staff " +
                 $"(intensity {intensity:F2}, charges {(chargesDismissed ? "dismissed" : "filed")}).");
    }

    /// <summary>
    /// City-wide enforcement climate, 0–1. Defaults to the neutral 0.5
    /// baseline when no macro economy is present.
    /// </summary>
    private float GetEnforcementStrictness()
    {
        var macro = GetTree()?.Root?.FindChild("MacroEconomyEngine", true, false) as MacroEconomyEngine;
        return macro?.EnforcementStrictness ?? 0.5f;
    }

    // ── Mitigation Methods ─────────────────────────────────────────────

    /// <summary>
    /// Pay off the local precinct captain. Immediate Heat reduction
    /// proportional to the bribe amount. Higher heat = less effective.
    /// </summary>
    /// <param name="cost">Amount to spend on the bribe.</param>
    /// <returns>Actual heat reduction achieved.</returns>
    public float BribePrecinctCaptain(double cost)
    {
        if (cost <= 0)
        {
            GD.Print("[HeatSystem] BribePrecinctCaptain: Invalid cost — must be > 0.");
            return 0f;
        }

        if (GameStateManager.Instance != null)
        {
            if (GameStateManager.Instance.Cash < cost)
            {
                GD.Print($"[HeatSystem] BribePrecinctCaptain: Insufficient funds (need ${cost:F2}, have ${GameStateManager.Instance.Cash:F2}).");
                return 0f;
            }
            GameStateManager.Instance.Cash -= cost;
        }

        TotalBribeSpend += cost;

        // Effectiveness decays as heat rises (harder to bribe when heat is high)
        float effectiveness = Mathf.Clamp(1.0f - (_heat / 120.0f), 0.1f, 1.0f);
        float heatReduction = (float)(cost / 50.0) * effectiveness;

        Heat -= heatReduction;

        GD.Print($"[HeatSystem] BribePrecinctCaptain: ${cost:F2} paid → " +
                 $"Heat -{heatReduction:F2} (eff={effectiveness:P0}) New Heat={_heat:F1}");

        EmitSignal(SignalName.OnBribePaid, cost, heatReduction);

        // Small reputation penalty for corruption
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Reputation -= 1.0f;

        return heatReduction;
    }

    /// <summary>
    /// Execute a legal defense strategy. Expensive but clean —
    /// reduces Heat with no reputation penalty and can lower future
    /// intervention probability temporarily.
    /// </summary>
    /// <param name="cost">Amount to spend on legal fees.</param>
    /// <returns>Actual heat reduction achieved.</returns>
    public float ExecuteLegalDefense(double cost)
    {
        if (cost <= 0)
        {
            GD.Print("[HeatSystem] ExecuteLegalDefense: Invalid cost — must be > 0.");
            return 0f;
        }

        if (GameStateManager.Instance != null)
        {
            if (GameStateManager.Instance.Cash < cost)
            {
                GD.Print($"[HeatSystem] ExecuteLegalDefense: Insufficient funds (need ${cost:F2}, have ${GameStateManager.Instance.Cash:F2}).");
                return 0f;
            }
            GameStateManager.Instance.Cash -= cost;
        }

        TotalLegalSpend += cost;

        // Legal defense is more cost-effective than bribes at high heat
        // because lawyers scale better (flat rate per heat reduction)
        float heatReduction = (float)(cost / 75.0);

        Heat -= heatReduction;

        GD.Print($"[HeatSystem] ExecuteLegalDefense: ${cost:F2} paid → " +
                 $"Heat -{heatReduction:F2} New Heat={_heat:F1}");

        EmitSignal(SignalName.OnLegalDefenseExecuted, cost, heatReduction);

        // Legal defense slightly improves reputation (playing by the rules)
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Reputation += 0.5f;

        return heatReduction;
    }

    // ── Utility ────────────────────────────────────────────────────────

    /// <summary>
    /// Get the heat multiplier for a given client tier index.
    /// </summary>
    private float GetTierMultiplier(int tier)
    {
        if (TierMultipliers == null || TierMultipliers.Length == 0)
            return 1.0f;

        int index = Mathf.Clamp(tier, 0, TierMultipliers.Length - 1);
        return TierMultipliers[index];
    }

    /// <summary>
    /// Set the current client tier by index (0 = common, 1 = mid, 2 = VIP).
    /// </summary>
    public void SetClientTier(int tier)
    {
        CurrentClientTier = Mathf.Clamp(tier, 0, TierMultipliers?.Length - 1 ?? 0);
        GD.Print($"[HeatSystem] Client tier set to {CurrentClientTier} (×{GetTierMultiplier(CurrentClientTier):F1}).");
    }

    /// <summary>
    /// Manually add heat (e.g., from a story event).
    /// </summary>
    public void AddHeat(float amount) => Heat += amount;

    /// <summary>
    /// Manually remove heat (e.g., from a cooling-off period).
    /// </summary>
    public void RemoveHeat(float amount) => Heat -= amount;

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "heat";

    public System.Text.Json.Nodes.JsonObject CaptureState() => new()
    {
        ["heat"] = _heat,
        ["clientTier"] = CurrentClientTier,
        ["totalRaids"] = TotalRaidsTriggered,
        ["totalBribeSpend"] = TotalBribeSpend,
        ["totalLegalSpend"] = TotalLegalSpend
    };

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        // Assign through the property so GameStateManager.Heat stays in sync —
        // the two are mirrored and drifting them apart desynchronizes every
        // consumer that reads the global scalar instead of this system.
        Heat = Mathf.Clamp((float?)state["heat"] ?? 0f, 0f, 100f);

        CurrentClientTier = (int?)state["clientTier"] ?? 0;
        TotalRaidsTriggered = Math.Max(0, (int?)state["totalRaids"] ?? 0);
        TotalBribeSpend = Math.Max(0.0, (double?)state["totalBribeSpend"] ?? 0.0);
        TotalLegalSpend = Math.Max(0.0, (double?)state["totalLegalSpend"] ?? 0.0);

        GD.Print($"[HeatSystem] Restored: Heat={_heat:F1}, {TotalRaidsTriggered} raids.");
    }

    public override string ToString()
    {
        return $"[HeatSystem] Heat={_heat:F1}/100 Raids={TotalRaidsTriggered} " +
               $"Bribes=${TotalBribeSpend:F0} Legal=${TotalLegalSpend:F0}";
    }
}
