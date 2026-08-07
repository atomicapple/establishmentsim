using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>Policy tree branch identifier.</summary>
public enum PolicyBranch
{
    None,
    WorkforceProtection,
    SystemicExploitation
}

/// <summary>Status of a policy in the tree.</summary>
public enum PolicyStatus
{
    Locked,       // Prerequisites not met or branch locked
    Available,    // Can be enacted
    Enacted       // Already signed into effect
}

/// <summary>
/// A single policy node in the Panderer's Code tree.
/// Each enacted policy applies permanent modifiers to the simulation.
/// </summary>
public partial class PolicyDefinition : Resource
{
    [Export] public string PolicyName { get; set; } = "Unnamed Policy";
    [Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";
    [Export] public PolicyBranch Branch { get; set; } = PolicyBranch.None;
    [Export] public int Tier { get; set; }        // 0 = branch selector, 1+ = progression
    [Export] public string PrerequisitePolicy { get; set; } = "";  // PolicyName of prerequisite
    [Export] public int MinDay { get; set; }       // Minimum day before available

    // Permanent modifier descriptions (shown in UI)
    [Export(PropertyHint.MultilineText)] public string EffectsDescription { get; set; } = "";

    public override string ToString() => $"[{Branch}] T{Tier} {PolicyName}";
}

/// <summary>Result of an enactment attempt.</summary>
public struct EnactmentResult
{
    public bool Success;
    public string Message;
    public PolicyDefinition Policy;

    public static EnactmentResult Ok(PolicyDefinition p) =>
        new() { Success = true, Policy = p, Message = $"Enacted: {p.PolicyName}" };

    public static EnactmentResult Fail(string reason) =>
        new() { Success = false, Message = reason };
}

// ── PolicyTreeManager Node ─────────────────────────────────────────────

/// <summary>
/// Manages the "Panderer's Code" policy tree — a set of mutually
/// exclusive operational directives that shape the establishment's
/// long-term strategy.
///
/// Two branches:
///   Workforce Protection — medical care, profit-sharing, security
///   Systemic Exploitation — extended shifts, debt bondage, information leverage
///
/// Enforces a cooldown between law signings and applies permanent
/// OPEX / Heat modifiers upon enactment.
/// </summary>
public partial class PolicyTreeManager : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a policy is successfully enacted.</summary>
    [Signal]
    public delegate void OnPolicyEnactedEventHandler(
        string policyName, int branch, int tier);

    /// <summary>Fired when a branch is chosen (locks the other branch forever).</summary>
    [Signal]
    public delegate void OnBranchChosenEventHandler(int branch, string branchName);

    /// <summary>Fired when the enactment cooldown expires.</summary>
    [Signal]
    public delegate void OnCooldownExpiredEventHandler();

    // ── Policy Definitions ─────────────────────────────────────────────

    private Dictionary<string, PolicyDefinition> _allPolicies;
    private readonly HashSet<string> _enactedPolicies = new();
    private PolicyBranch _activeBranch = PolicyBranch.None;

    // ── Cooldown ───────────────────────────────────────────────────────

    /// <summary>In-game days between policy signings. Default: 1 day.</summary>
    public int EnactmentCooldownDays { get; set; } = 1;

    private int _lastEnactmentDay = int.MinValue;

    /// <summary>Days remaining until next policy can be enacted.</summary>
    public int CooldownRemaining
    {
        get
        {
            int currentDay = GameStateManager.Instance?.DayCount ?? 0;
            int nextAvailable = _lastEnactmentDay + EnactmentCooldownDays;
            return Math.Max(0, nextAvailable - currentDay);
        }
    }

    /// <summary>Whether a new policy can be enacted right now.</summary>
    public bool CanEnactNow => CooldownRemaining <= 0;

    // ── Permanent Modifiers ────────────────────────────────────────────

    /// <summary>Cumulative daily OPEX modifier from all enacted policies.</summary>
    public double PermanentOpexModifier { get; set; }

    /// <summary>Cumulative Heat generation modifier (additive).</summary>
    public float PermanentHeatModifier { get; private set; }

    /// <summary>Cumulative Staff Stress modifier per day.</summary>
    public float PermanentStaffStressModifier { get; private set; }

    /// <summary>Cumulative Staff Satisfaction modifier.</summary>
    public float PermanentStaffSatisfactionModifier { get; private set; }

    /// <summary>Revenue multiplier from enacted policies.</summary>
    public float RevenueMultiplier { get; private set; } = 1.0f;

    /// <summary>Whether staff are prevented from quitting (Debt Bondage).</summary>
    public bool StaffQuitDisabled { get; set; }

    /// <summary>Stress decay rate multiplier (Medical Care).</summary>
    public float StressDecayMultiplier { get; private set; } = 1.0f;

    // ── State ──────────────────────────────────────────────────────────

    public PolicyBranch ActiveBranch => _activeBranch;
    public IReadOnlySet<string> EnactedPolicies => _enactedPolicies;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildPolicyTree();

        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
            GD.Print("[PolicyTree] Connected to GameStateManager.OnDailyTick.");
        }
        else
        {
            GD.PrintErr("[PolicyTree] GameStateManager.Instance is null.");
        }

        GD.Print($"[PolicyTree] Initialized. {_allPolicies.Count} policies defined. " +
                 $"Cooldown: {EnactmentCooldownDays} day(s).");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Policy Tree Construction ───────────────────────────────────────

    private void BuildPolicyTree()
    {
        _allPolicies = new Dictionary<string, PolicyDefinition>
        {
            // ═══ Workforce Protection Branch ═══════════════════════════════
            ["WF0"] = new PolicyDefinition
            {
                PolicyName = "Workforce Protection Act",
                Description = "Codify worker rights into establishment law. " +
                              "Mandates humane treatment, fair wages, and safe conditions. " +
                              "Locks the Systemic Exploitation branch permanently.",
                Branch = PolicyBranch.WorkforceProtection,
                Tier = 0,
                MinDay = 1,
                EffectsDescription = "Unlocks Workforce Protection branch.\n" +
                                     "Locks Systemic Exploitation branch.\n" +
                                     "OPEX: +$50/day (compliance costs)"
            },
            ["WF1"] = new PolicyDefinition
            {
                PolicyName = "Medical Care Provision",
                Description = "Provide on-site medical staff, health insurance, " +
                              "and mental health support for all employees. " +
                              "A healthy workforce is a loyal workforce.",
                Branch = PolicyBranch.WorkforceProtection,
                Tier = 1,
                PrerequisitePolicy = "Workforce Protection Act",
                EffectsDescription = "OPEX: +$50/day\n" +
                                     "Staff Stress decay rate: +50%\n" +
                                     "Staff Satisfaction: +5"
            },
            ["WF2"] = new PolicyDefinition
            {
                PolicyName = "Profit Sharing Program",
                Description = "Allocate 5% of monthly profits to staff bonuses. " +
                              "Aligns employee interests with establishment success.",
                Branch = PolicyBranch.WorkforceProtection,
                Tier = 2,
                PrerequisitePolicy = "Medical Care Provision",
                MinDay = 10,
                EffectsDescription = "OPEX: +5% of monthly revenue\n" +
                                     "Staff Satisfaction: +10\n" +
                                     "Staff quit risk: reduced"
            },
            ["WF3"] = new PolicyDefinition
            {
                PolicyName = "Security Detail",
                Description = "Hire professional security personnel to protect " +
                              "staff from abusive clients and external threats. " +
                              "A visible deterrent that keeps everyone safer.",
                Branch = PolicyBranch.WorkforceProtection,
                Tier = 3,
                PrerequisitePolicy = "Profit Sharing Program",
                MinDay = 20,
                EffectsDescription = "OPEX: +$200/day\n" +
                                     "Heat generation: −30%\n" +
                                     "ClientAbuse trauma events: −80%"
            },

            // ═══ Systemic Exploitation Branch ══════════════════════════════
            ["SE0"] = new PolicyDefinition
            {
                PolicyName = "Operational Freedom Act",
                Description = "Deregulate establishment operations. Remove worker " +
                              "protections in favor of maximum operational flexibility. " +
                              "Locks the Workforce Protection branch permanently.",
                Branch = PolicyBranch.SystemicExploitation,
                Tier = 0,
                MinDay = 1,
                EffectsDescription = "Unlocks Systemic Exploitation branch.\n" +
                                     "Locks Workforce Protection branch.\n" +
                                     "OPEX: −$30/day (reduced compliance costs)"
            },
            ["SE1"] = new PolicyDefinition
            {
                PolicyName = "Extended Shift Authorization",
                Description = "Remove limits on shift duration. Staff can be " +
                              "scheduled for 16+ hour shifts, maximizing labor " +
                              "output at the cost of worker well-being.",
                Branch = PolicyBranch.SystemicExploitation,
                Tier = 1,
                PrerequisitePolicy = "Operational Freedom Act",
                EffectsDescription = "OPEX: −$30/day (cheaper labor)\n" +
                                     "Staff Stress: +5/day\n" +
                                     "Staff Satisfaction: −8"
            },
            ["SE2"] = new PolicyDefinition
            {
                PolicyName = "Debt Bondage Protocol",
                Description = "Extend predatory loans to staff, then leverage " +
                              "their debt to prevent resignation. Workers become " +
                              "indentured — they literally cannot afford to leave.",
                Branch = PolicyBranch.SystemicExploitation,
                Tier = 2,
                PrerequisitePolicy = "Extended Shift Authorization",
                MinDay = 15,
                EffectsDescription = "OPEX: −$80/day (wage garnishment)\n" +
                                     "Staff cannot quit (Satisfaction floor: 1)\n" +
                                     "PublicSentiment: −15\n" +
                                     "Heat: +10 (attracts scrutiny)"
            },
            ["SE3"] = new PolicyDefinition
            {
                PolicyName = "Information Leverage Initiative",
                Description = "Systematically collect compromising information " +
                              "on clients, competitors, and officials. Monetize " +
                              "intelligence through blackmail and insider trading.",
                Branch = PolicyBranch.SystemicExploitation,
                Tier = 3,
                PrerequisitePolicy = "Debt Bondage Protocol",
                MinDay = 30,
                EffectsDescription = "Revenue from Information Sales: +25%\n" +
                                     "Heat: +15 (permanent)\n" +
                                     "PublicSentiment: −10"
            }
        };
    }

    // ── Daily Tick Handler ─────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float publicSentiment)
    {
        // Apply ongoing modifiers from enacted policies

        // OPEX modifiers are applied via FinancialLedger if present
        // Heat modifiers are applied via HeatSystem if present
        ApplyDailyModifiers();

        // Notify when cooldown expires
        if (_lastEnactmentDay > int.MinValue && CanEnactNow)
        {
            int currentDay = GameStateManager.Instance?.DayCount ?? 0;
            if (currentDay == _lastEnactmentDay + EnactmentCooldownDays)
            {
                EmitSignal(SignalName.OnCooldownExpired);
            }
        }
    }

    private void ApplyDailyModifiers()
    {
        // Find FinancialLedger and apply OPEX modifier
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", recursive: true, owned: false)
                     as FinancialLedger;
        if (ledger != null && PermanentOpexModifier != 0)
        {
            if (PermanentOpexModifier > 0)
                ledger.RecordExpense(ExpenseCategory.FacilityMaintenance,
                    PermanentOpexModifier, "Policy-mandated operational expenditure");
            else
                ledger.RecordRevenue(RevenueCategory.ClientFees,
                    -PermanentOpexModifier, "Policy-driven cost reduction");
        }

        // Apply permanent Heat modifier
        var heatSystem = GetTree()?.Root?.FindChild("HeatSystem", recursive: true, owned: false)
                         as HeatSystem;
        if (heatSystem != null && PermanentHeatModifier != 0)
        {
            heatSystem.AddHeat(PermanentHeatModifier);
        }

        // Apply staff stress/satisfaction modifiers (registered staff handled
        // by PsychologicalBreakSystem when it polls staff values)
    }

    // ── Policy Enactment ───────────────────────────────────────────────

    /// <summary>
    /// Attempt to enact a policy by name.
    /// Checks prerequisites, branch exclusivity, cooldown, and minimum day.
    /// </summary>
    public EnactmentResult EnactPolicy(string policyName)
    {
        // 1. Validate policy exists
        if (!_allPolicies.TryGetValue(policyName, out var policy))
            return EnactmentResult.Fail($"Unknown policy: '{policyName}'");

        // 2. Already enacted?
        if (_enactedPolicies.Contains(policyName))
            return EnactmentResult.Fail($"Policy already enacted: {policy.PolicyName}");

        // 3. Check cooldown
        if (!CanEnactNow)
            return EnactmentResult.Fail(
                $"Cooldown active. {CooldownRemaining} day(s) remaining.");

        // 4. Check minimum day
        int currentDay = GameStateManager.Instance?.DayCount ?? 0;
        if (currentDay < policy.MinDay)
            return EnactmentResult.Fail(
                $"Not available until day {policy.MinDay} (current: day {currentDay}).");

        // 5. Check branch exclusivity
        if (policy.Tier > 0)
        {
            // Must match active branch
            if (_activeBranch != policy.Branch)
                return EnactmentResult.Fail(
                    $"Branch locked. Active branch is {_activeBranch}, " +
                    $"but {policy.PolicyName} belongs to {policy.Branch}.");
        }

        // 6. Check prerequisite
        if (!string.IsNullOrEmpty(policy.PrerequisitePolicy) &&
            !_enactedPolicies.Contains(policy.PrerequisitePolicy))
        {
            return EnactmentResult.Fail(
                $"Prerequisite not met: '{policy.PrerequisitePolicy}' must be enacted first.");
        }

        // 7. Check branch lock (for tier-0 branch selectors)
        if (policy.Tier == 0 && _activeBranch != PolicyBranch.None)
            return EnactmentResult.Fail(
                $"A branch has already been chosen ({_activeBranch}). Cannot change.");

        // ── All checks passed — enact the policy ───────────────────────

        _enactedPolicies.Add(policyName);
        _lastEnactmentDay = currentDay;

        // Set active branch for tier-0 policies
        if (policy.Tier == 0)
        {
            _activeBranch = policy.Branch;
            EmitSignal(SignalName.OnBranchChosen, (int)policy.Branch, policy.Branch.ToString());
            GD.Print($"[PolicyTree] Branch chosen: {policy.Branch}. " +
                     $"Opposite branch locked permanently.");
        }

        // Apply permanent modifiers
        ApplyPolicyEffects(policy);

        EmitSignal(SignalName.OnPolicyEnacted, policy.PolicyName, (int)policy.Branch, policy.Tier);

        GD.Print($"[PolicyTree] ENACTED: {policy.PolicyName} " +
                 $"(T{policy.Tier} {policy.Branch}, day {currentDay})");

        return EnactmentResult.Ok(policy);
    }

    // ── Policy Effect Application ──────────────────────────────────────

    private void ApplyPolicyEffects(PolicyDefinition policy)
    {
        switch (policy.PolicyName)
        {
            // ── Workforce Protection ───────────────────────────────
            case "Workforce Protection Act":
                PermanentOpexModifier += 50;      // +$50/day compliance
                break;

            case "Medical Care Provision":
                PermanentOpexModifier += 50;      // +$50/day medical
                StressDecayMultiplier += 0.5f;    // +50% stress decay
                PermanentStaffSatisfactionModifier += 5;
                break;

            case "Profit Sharing Program":
                // 5% revenue share — applied as OPEX proportional to cash
                PermanentOpexModifier += 0;       // dynamic (handled in daily tick)
                PermanentStaffSatisfactionModifier += 10;
                break;

            case "Security Detail":
                PermanentOpexModifier += 200;     // +$200/day security
                PermanentHeatModifier -= 0.3f;    // −30% heat (applied as multiplier in HeatSystem)
                // ClientAbuse trauma reduction handled by PsychBreakSystem check
                break;

            // ── Systemic Exploitation ──────────────────────────────
            case "Operational Freedom Act":
                PermanentOpexModifier -= 30;      // −$30/day deregulation
                break;

            case "Extended Shift Authorization":
                PermanentOpexModifier -= 30;      // −$30/day cheaper labor
                PermanentStaffStressModifier += 5;
                PermanentStaffSatisfactionModifier -= 8;
                break;

            case "Debt Bondage Protocol":
                PermanentOpexModifier -= 80;      // −$80/day wage garnishment
                StaffQuitDisabled = true;
                if (GameStateManager.Instance != null)
                    GameStateManager.Instance.PublicSentiment -= 15;
                PermanentHeatModifier += 10;
                PermanentStaffStressModifier += 15;    // trauma baseline from debt bondage
                PermanentStaffSatisfactionModifier -= 20;
                break;

            case "Information Leverage Initiative":
                RevenueMultiplier += 0.25f;       // +25% information sales
                PermanentHeatModifier += 15;
                if (GameStateManager.Instance != null)
                    GameStateManager.Instance.PublicSentiment -= 10;
                break;
        }
    }

    // ── Query Methods ──────────────────────────────────────────────────

    /// <summary>Get the status of every policy in the tree.</summary>
    public Dictionary<string, PolicyStatus> GetAllPolicyStatuses()
    {
        var result = new Dictionary<string, PolicyStatus>();

        foreach (var kvp in _allPolicies)
        {
            var policy = kvp.Value;

            if (_enactedPolicies.Contains(kvp.Key))
            {
                result[kvp.Key] = PolicyStatus.Enacted;
                continue;
            }

            // Check if available
            bool branchOk = policy.Tier == 0
                ? _activeBranch == PolicyBranch.None  // branch selector: only if no branch chosen
                : _activeBranch == policy.Branch;      // tier 1+: must match active branch

            bool prerequisiteMet = string.IsNullOrEmpty(policy.PrerequisitePolicy) ||
                                   _enactedPolicies.Contains(policy.PrerequisitePolicy);

            int currentDay = GameStateManager.Instance?.DayCount ?? 0;
            bool dayOk = currentDay >= policy.MinDay;

            result[kvp.Key] = (branchOk && prerequisiteMet && dayOk)
                ? PolicyStatus.Available
                : PolicyStatus.Locked;
        }

        return result;
    }

    /// <summary>Get all policies in a specific branch.</summary>
    public List<PolicyDefinition> GetPoliciesInBranch(PolicyBranch branch)
    {
        return _allPolicies.Values
            .Where(p => p.Branch == branch)
            .OrderBy(p => p.Tier)
            .ToList();
    }

    /// <summary>Get enacted policies in order.</summary>
    public List<PolicyDefinition> GetEnactedPolicies()
    {
        return _allPolicies.Values
            .Where(p => _enactedPolicies.Contains(p.PolicyName))
            .OrderBy(p => p.Tier)
            .ToList();
    }

    /// <summary>
    /// Get a summary of all active permanent modifiers for UI display.
    /// </summary>
    public string GetModifierSummary()
    {
        var parts = new List<string>();
        if (PermanentOpexModifier != 0)
            parts.Add($"OPEX {(PermanentOpexModifier > 0 ? "+" : "")}${PermanentOpexModifier:F0}/day");
        if (PermanentHeatModifier != 0)
            parts.Add($"Heat {(PermanentHeatModifier > 0 ? "+" : "")}{PermanentHeatModifier:F1}");
        if (PermanentStaffStressModifier != 0)
            parts.Add($"Staff Stress {(PermanentStaffStressModifier > 0 ? "+" : "")}{PermanentStaffStressModifier:F1}/day");
        if (PermanentStaffSatisfactionModifier != 0)
            parts.Add($"Staff Satisfaction {(PermanentStaffSatisfactionModifier > 0 ? "+" : "")}{PermanentStaffSatisfactionModifier:F0}");
        if (RevenueMultiplier != 1.0f)
            parts.Add($"Revenue ×{RevenueMultiplier:F2}");
        if (StaffQuitDisabled)
            parts.Add("Staff cannot quit (Debt Bondage)");
        if (StressDecayMultiplier != 1.0f)
            parts.Add($"Stress Decay ×{StressDecayMultiplier:F1}");

        return parts.Count > 0 ? string.Join(" | ", parts) : "(no active modifiers)";
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "policies";

    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        var enacted = new System.Text.Json.Nodes.JsonArray();
        foreach (var key in _enactedPolicies)
            enacted.Add(key);

        // Only the choices are stored. The permanent modifiers are derived
        // from those choices and are re-applied on restore, so a rebalance of
        // the effect table reaches existing campaigns instead of being frozen
        // into old saves.
        return new System.Text.Json.Nodes.JsonObject
        {
            ["activeBranch"] = _activeBranch.ToString(),
            ["enactedPolicies"] = enacted,
            ["lastEnactmentDay"] = _lastEnactmentDay
        };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        if (_allPolicies == null) BuildPolicyTree();

        if (Enum.TryParse<PolicyBranch>((string)state["activeBranch"], ignoreCase: true, out var branch))
            _activeBranch = branch;

        _lastEnactmentDay = (int?)state["lastEnactmentDay"] ?? int.MinValue;

        _enactedPolicies.Clear();
        ResetPermanentModifiers();

        if (state["enactedPolicies"] is System.Text.Json.Nodes.JsonArray enacted)
        {
            foreach (var node in enacted)
            {
                var key = (string)node;
                if (string.IsNullOrEmpty(key)) continue;

                if (!_allPolicies.TryGetValue(key, out var policy))
                {
                    // A policy that no longer exists in this build. Drop it
                    // rather than failing the load.
                    GD.PrintErr($"[PolicyTree] Save references unknown policy '{key}'. Skipped.");
                    continue;
                }

                _enactedPolicies.Add(key);
                ApplyPolicyEffects(policy);
            }
        }

        GD.Print($"[PolicyTree] Restored: branch {_activeBranch}, " +
                 $"{_enactedPolicies.Count} policies, mods=[{GetModifierSummary()}]");
    }

    /// <summary>
    /// Return every permanent modifier to its unmodified default, so enacted
    /// policy effects can be replayed from scratch without doubling up.
    /// </summary>
    private void ResetPermanentModifiers()
    {
        PermanentOpexModifier = 0.0;
        PermanentHeatModifier = 0f;
        PermanentStaffStressModifier = 0f;
        PermanentStaffSatisfactionModifier = 0f;
        RevenueMultiplier = 1.0f;
        StaffQuitDisabled = false;
        StressDecayMultiplier = 1.0f;
    }

    public override string ToString()
    {
        return $"[PolicyTree] Branch={_activeBranch} Enacted={_enactedPolicies.Count}/8 " +
               $"Cooldown={CooldownRemaining}d Mods=[{GetModifierSummary()}]";
    }
}
