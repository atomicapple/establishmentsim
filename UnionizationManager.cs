using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Models collective worker action and labor crisis mechanics.
/// Hidden Unionization Risk metric increases when average staff
/// Stress > 70 or Systemic Exploitation policies are active.
/// At 80% risk, triggers a strike crisis with management choices.
/// </summary>
public partial class UnionizationManager : Node, ISaveableSystem
{
    [Signal] public delegate void OnUnionRiskChangedEventHandler(float risk, float delta);
    [Signal] public delegate void OnStrikeTriggeredEventHandler(float risk, int strikingStaffCount);
    [Signal] public delegate void OnStrikeResolvedEventHandler(string resolution);
    [Signal] public delegate void OnDemandConcededEventHandler(string demand);

    private float _unionRisk;
    private bool _strikeActive;
    private int _strikeDayStart;
    private readonly RandomNumberGenerator _rng = new();

    // ── Properties ─────────────────────────────────────────────────────

    public float UnionRisk
    {
        get => _unionRisk;
        private set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(_unionRisk, clamped)) return;
            var delta = clamped - _unionRisk;
            _unionRisk = clamped;
            EmitSignal(SignalName.OnUnionRiskChanged, _unionRisk, delta);
        }
    }

    public bool StrikeActive => _strikeActive;
    public int StrikingStaffCount { get; private set; }
    public int StrikeDurationDays => _strikeActive ? (GameStateManager.Instance?.DayCount ?? 0) - _strikeDayStart : 0;
    public float StrikeSeverityThreshold { get; set; } = 80f;
    public float ExploitationPolicyRiskBonus { get; set; } = 2.5f;

    // Strike costs per day
    public double DailyRevenueLossDuringStrike { get; set; } = 200.0;
    public float DailyReputationLossDuringStrike { get; set; } = 1.5f;

    public override void _Ready()
    {
        _rng.Randomize();

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;

        GD.Print("[Unionization] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Tick ─────────────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float sentiment)
    {
        if (_strikeActive)
        {
            ApplyStrikeEffects(cash);
            return;
        }

        // Calculate unionization risk
        float riskDelta = CalculateRiskDelta();
        UnionRisk += riskDelta;

        // Check for strike trigger
        if (_unionRisk >= StrikeSeverityThreshold)
            TriggerStrike();
    }

    private float CalculateRiskDelta()
    {
        float delta = 0f;

        // Factor 1: Average staff stress
        float avgStress = GetAverageStaffStress();
        if (avgStress > 70f)
            delta += (avgStress - 70f) * 0.3f;

        // Factor 2: Systemic Exploitation policies
        var policyTree = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        if (policyTree != null && policyTree.ActiveBranch == PolicyBranch.SystemicExploitation)
            delta += ExploitationPolicyRiskBonus;

        // Factor 3: Low satisfaction
        float avgSatisfaction = GetAverageStaffSatisfaction();
        if (avgSatisfaction < 30f)
            delta += (30f - avgSatisfaction) * 0.2f;

        // Factor 4: Natural decay if conditions are good
        if (avgStress < 40f && avgSatisfaction > 60f)
            delta -= 1.5f;

        // Factor 5: Debt bondage increases risk (enslaved workers rebel)
        if (policyTree?.StaffQuitDisabled == true)
            delta += 1.0f;

        return delta;
    }

    // ── Strike Logic ───────────────────────────────────────────────────

    private void TriggerStrike()
    {
        _strikeActive = true;
        _strikeDayStart = GameStateManager.Instance?.DayCount ?? 0;

        var allStaff = FindAllStaff();
        StrikingStaffCount = Math.Max(1, allStaff.Count / 2 + (int)(_rng.Randi() % (uint)Math.Max(1, allStaff.Count / 2)));

        EmitSignal(SignalName.OnStrikeTriggered, _unionRisk, StrikingStaffCount);
        GD.Print($"[Unionization] STRIKE! {StrikingStaffCount} staff walking out. Risk={_unionRisk:F1}");
    }

    private void ApplyStrikeEffects(double cash)
    {
        // Revenue loss
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= DailyRevenueLossDuringStrike;

        // Reputation loss
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Reputation -= DailyReputationLossDuringStrike;

        // Union risk stays high during strike
        UnionRisk = Mathf.Max(_unionRisk, 60f);

        GD.Print($"[Unionization] Strike day {StrikeDurationDays}: " +
                 $"-${DailyRevenueLossDuringStrike:F0}, Rep -{DailyReputationLossDuringStrike:F1}");
    }

    // ── Management Choices ─────────────────────────────────────────────

    /// <summary>Negotiate profit-sharing. Ends strike, increases daily OPEX by $100.</summary>
    public void NegotiateProfitSharing()
    {
        if (!_strikeActive) return;

        _strikeActive = false;
        UnionRisk -= 40f;

        // Permanent OPEX increase
        var policyTree = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        if (policyTree != null)
            policyTree.PermanentOpexModifier += 100;

        // Staff satisfaction boost
        foreach (var staff in FindAllStaff())
            staff.Satisfaction += 15f;

        EmitSignal(SignalName.OnStrikeResolved, "profit_sharing");
        EmitSignal(SignalName.OnDemandConceded, "profit_sharing");
        GD.Print("[Unionization] Strike resolved: Profit-sharing negotiated. OPEX +$100/day.");
    }

    /// <summary>Hire strikebreakers. Ends strike, Heat +20.</summary>
    public void HireStrikebreakers()
    {
        if (!_strikeActive) return;

        _strikeActive = false;
        UnionRisk -= 20f;

        var heatSystem = GetTree()?.Root?.FindChild("HeatSystem", true, false) as HeatSystem;
        if (heatSystem != null)
            heatSystem.AddHeat(20f);

        // Staff satisfaction plummets
        foreach (var staff in FindAllStaff())
        {
            staff.Satisfaction -= 25f;
            staff.Stress += 15f;
        }

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.PublicSentiment -= 10f;

        EmitSignal(SignalName.OnStrikeResolved, "strikebreakers");
        GD.Print("[Unionization] Strike resolved: Strikebreakers hired. Heat +20.");
    }

    /// <summary>Concede to all worker demands. Ends strike, high cost.</summary>
    public void ConcedeToDemands()
    {
        if (!_strikeActive) return;

        _strikeActive = false;
        UnionRisk = 0f;

        // Cash payout
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= 2000;

        // Staff fully satisfied
        foreach (var staff in FindAllStaff())
        {
            staff.Satisfaction = 80f;
            staff.Stress -= 20f;
        }

        // Disable debt bondage if active
        var policyTree = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        if (policyTree != null)
            policyTree.StaffQuitDisabled = false;

        EmitSignal(SignalName.OnStrikeResolved, "full_concession");
        EmitSignal(SignalName.OnDemandConceded, "full_concession");
        GD.Print("[Unionization] Strike resolved: Full concession to worker demands. -$2000.");
    }

    // ── Utility ────────────────────────────────────────────────────────

    private float GetAverageStaffStress()
    {
        var staff = FindAllStaff();
        return staff.Count > 0 ? staff.Average(s => s.Stress) : 0f;
    }

    private float GetAverageStaffSatisfaction()
    {
        var staff = FindAllStaff();
        return staff.Count > 0 ? staff.Average(s => s.Satisfaction) : 100f;
    }

    private List<StaffMember> FindAllStaff()
    {
        // Previously read PsychologicalBreakSystem.GetStaffAtRisk(), which
        // filters to Stress >= 80 — so the average-stress calculation was
        // taken over only the already-broken and never dropped below 80.
        return StaffRoster.Instance?.GetAll().ToList() ?? new List<StaffMember>();
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "unionization";

    public System.Text.Json.Nodes.JsonObject CaptureState() => new()
    {
        ["unionRisk"] = _unionRisk,
        ["strikeActive"] = _strikeActive,
        ["strikeDayStart"] = _strikeDayStart,
        ["strikingStaffCount"] = StrikingStaffCount
    };

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        _unionRisk = Mathf.Clamp((float?)state["unionRisk"] ?? 0f, 0f, 100f);
        _strikeActive = (bool?)state["strikeActive"] ?? false;
        _strikeDayStart = (int?)state["strikeDayStart"] ?? 0;
        StrikingStaffCount = Math.Max(0, (int?)state["strikingStaffCount"] ?? 0);

        GD.Print($"[Unionization] Restored: risk {_unionRisk:F0}%, " +
                 $"strike {(_strikeActive ? "ACTIVE" : "none")}.");
    }

    public override string ToString() =>
        $"[Unionization] Risk={_unionRisk:F1}% Strike={(_strikeActive ? $"ACTIVE (day {StrikeDurationDays})" : "none")}";
}
