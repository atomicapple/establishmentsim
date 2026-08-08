using Godot;
using System;

/// <summary>Macroeconomic phase affecting the entire city.</summary>
public enum MacroPhase
{
    Boom,            // High spending, low enforcement, rising wages
    Recession,       // Low spending, high crime, desperate clients
    Stagnation,      // Flat economy, stable but unexciting
    PoliceCrackdown  // High enforcement, low spending, expensive bribes
}

/// <summary>
/// Simulates city-wide economic trends shifting every 30–90 game days.
/// Client spend capacity, staff wage demands, bribe costs, and
/// law enforcement strictness change dynamically per phase.
/// </summary>
public partial class MacroEconomyEngine : Node, ISaveableSystem
{
    [Signal] public delegate void OnPhaseChangedEventHandler(int oldPhase, int newPhase, string phaseName);
    [Signal] public delegate void OnPhaseWarningEventHandler(int daysRemaining);

    private MacroPhase _currentPhase = MacroPhase.Stagnation;
    private int _phaseStartDay;
    private int _phaseDurationDays;
    private int _daysInPhase;
    private readonly RandomNumberGenerator _rng = new();

    // ── Phase Modifiers (updated on phase change) ──────────────────────
    public float ClientSpendMultiplier { get; private set; } = 1.0f;
    public float WageDemandMultiplier { get; private set; } = 1.0f;
    public float BribeCostMultiplier { get; private set; } = 1.0f;
    public float EnforcementStrictness { get; private set; } = 0.5f;
    public float FootfallMultiplier { get; private set; } = 1.0f;
    public float PropertyValueMultiplier { get; private set; } = 1.0f;

    public MacroPhase CurrentPhase => _currentPhase;
    public int DaysInPhase => _daysInPhase;
    public int PhaseDurationDays => _phaseDurationDays;
    public int DaysRemaining => _phaseDurationDays - _daysInPhase;

    public override void _Ready()
    {
        WorldRandom.Seed(_rng, nameof(MacroEconomyEngine));
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        TransitionTo(MacroPhase.Stagnation);
        GD.Print("[MacroEconomy] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Tick ─────────────────────────────────────────────────────

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        _daysInPhase++;

        // Phase transition check
        if (_daysInPhase >= _phaseDurationDays)
            TransitionTo(GetNextPhase());

        // Warning at 80% through phase
        if (_daysInPhase == (int)(_phaseDurationDays * 0.8f))
            EmitSignal(SignalName.OnPhaseWarning, DaysRemaining);
    }

    // ── Phase Transitions ──────────────────────────────────────────────

    private void TransitionTo(MacroPhase newPhase)
    {
        var oldPhase = _currentPhase;
        _currentPhase = newPhase;
        _phaseStartDay = GameStateManager.Instance?.DayCount ?? 0;
        _phaseDurationDays = 30 + (int)(_rng.Randi() % 61u); // 30–90 days
        _daysInPhase = 0;

        ApplyPhaseMultipliers(newPhase);

        EmitSignal(SignalName.OnPhaseChanged, (int)oldPhase, (int)newPhase, newPhase.ToString());
        GD.Print($"[MacroEconomy] Phase: {oldPhase} → {newPhase} ({_phaseDurationDays}d). " +
                 $"Spend×{ClientSpendMultiplier} Wage×{WageDemandMultiplier} " +
                 $"Bribe×{BribeCostMultiplier} Law×{EnforcementStrictness}");
    }

    /// <summary>
    /// Set the six economy multipliers for a phase. Split out of
    /// <see cref="TransitionTo"/> so that restoring a save can re-derive them
    /// without re-rolling the phase duration or announcing a phase change
    /// that never happened.
    /// </summary>
    private void ApplyPhaseMultipliers(MacroPhase phase)
    {
        (ClientSpendMultiplier, WageDemandMultiplier, BribeCostMultiplier,
         EnforcementStrictness, FootfallMultiplier, PropertyValueMultiplier) = phase switch
        {
            MacroPhase.Boom => (1.5f, 1.3f, 0.7f, 0.3f, 1.4f, 1.3f),
            MacroPhase.Recession => (0.6f, 0.8f, 1.5f, 0.4f, 0.5f, 0.7f),
            MacroPhase.Stagnation => (1.0f, 1.0f, 1.0f, 0.5f, 1.0f, 1.0f),
            MacroPhase.PoliceCrackdown => (0.7f, 0.9f, 2.0f, 0.9f, 0.4f, 0.8f),
            _ => (1.0f, 1.0f, 1.0f, 0.5f, 1.0f, 1.0f)
        };
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "macroEconomy";

    public System.Text.Json.Nodes.JsonObject CaptureState() => new()
    {
        ["phase"] = _currentPhase.ToString(),
        ["phaseStartDay"] = _phaseStartDay,
        ["phaseDurationDays"] = _phaseDurationDays,
        ["daysInPhase"] = _daysInPhase
    };

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        if (Enum.TryParse<MacroPhase>((string)state["phase"], ignoreCase: true, out var phase))
            _currentPhase = phase;

        _phaseStartDay = (int?)state["phaseStartDay"] ?? 0;
        _phaseDurationDays = (int?)state["phaseDurationDays"] ?? 30;
        _daysInPhase = (int?)state["daysInPhase"] ?? 0;

        // Multipliers are derived, not stored — re-derive rather than trusting
        // saved copies, so a rebalance of the phase table applies to old saves.
        ApplyPhaseMultipliers(_currentPhase);

        GD.Print($"[MacroEconomy] Restored: {_currentPhase}, day {_daysInPhase}/{_phaseDurationDays}.");
    }

    private MacroPhase GetNextPhase()
    {
        // Transition logic: no phase repeats immediately
        float roll = _rng.Randf();

        return _currentPhase switch
        {
            MacroPhase.Boom => roll < 0.5f ? MacroPhase.Stagnation :
                               roll < 0.8f ? MacroPhase.PoliceCrackdown : MacroPhase.Recession,
            MacroPhase.Recession => roll < 0.4f ? MacroPhase.Stagnation :
                                    roll < 0.8f ? MacroPhase.Boom : MacroPhase.PoliceCrackdown,
            MacroPhase.Stagnation => roll < 0.35f ? MacroPhase.Boom :
                                     roll < 0.7f ? MacroPhase.Recession : MacroPhase.PoliceCrackdown,
            MacroPhase.PoliceCrackdown => roll < 0.5f ? MacroPhase.Stagnation :
                                          roll < 0.9f ? MacroPhase.Recession : MacroPhase.Boom,
            _ => MacroPhase.Stagnation
        };
    }

    /// <summary>Force a specific phase (e.g., for story events).</summary>
    public void ForcePhase(MacroPhase phase)
    {
        TransitionTo(phase);
        GD.Print($"[MacroEconomy] Forced phase: {phase}.");
    }

    // ── Economic Impact Methods ────────────────────────────────────────

    /// <summary>Get the current client budget modifier for a given tier.</summary>
    public double AdjustClientBudget(double baseBudget)
    {
        return baseBudget * ClientSpendMultiplier;
    }

    /// <summary>Get the current bribe cost for a base amount.</summary>
    public double AdjustBribeCost(double baseBribe)
    {
        return baseBribe * BribeCostMultiplier;
    }

    /// <summary>Get staff wage demand for a base salary.</summary>
    public double AdjustWageDemand(double baseSalary)
    {
        return baseSalary * WageDemandMultiplier;
    }

    /// <summary>Get whether a police intervention is more likely right now.</summary>
    public float GetEnforcementMultiplier()
    {
        return EnforcementStrictness;
    }

    // ── For the interface ──────────────────────────────────────────────

    /// <summary>
    /// A short caption for the top bar. Names the condition and, when trade
    /// is affected, says by how much — a phase name on its own does not
    /// explain a halved takings line.
    /// </summary>
    public string GetShortCaption()
    {
        var name = _currentPhase switch
        {
            MacroPhase.Boom => "Boom",
            MacroPhase.Recession => "Recession",
            MacroPhase.Stagnation => "Quiet city",
            MacroPhase.PoliceCrackdown => "Crackdown",
            _ => _currentPhase.ToString()
        };

        return Mathf.IsEqualApprox(FootfallMultiplier, 1f)
            ? name
            : $"{name} · trade ×{FootfallMultiplier:F1}";
    }

    /// <summary>True while the city is working against the house.</summary>
    public bool IsAdverse => FootfallMultiplier < 1f || EnforcementStrictness > 0.6f;

    /// <summary>
    /// The tooltip. Says what the condition does and how long it has left,
    /// because the useful question during a crackdown is "how long do I have
    /// to survive this", not "what is it called".
    /// </summary>
    public string GetPlainSummary()
    {
        var lines = new List<string>
        {
            GetOutlookText(),
            "",
            $"Clients through the door: ×{FootfallMultiplier:F1}",
            $"What they will spend: ×{ClientSpendMultiplier:F1}",
            $"Cost of a bribe: ×{BribeCostMultiplier:F1}",
            $"Police attention: {EnforcementStrictness:P0}",
            "",
            DaysRemaining > 0
                ? $"Day {_daysInPhase} of {_phaseDurationDays}. Around " +
                  $"{DaysRemaining} left before the city turns again."
                : "This is about to change."
        };

        return string.Join('\n', lines);
    }

    /// <summary>Get a human-readable economic report.</summary>
    public string GetEconomicReport()
    {
        return $@"
=== ECONOMIC REPORT (Day {GameStateManager.Instance?.DayCount ?? 0}) ===
Phase: {_currentPhase} (Day {_daysInPhase}/{_phaseDurationDays})
Days remaining: {DaysRemaining}

Client Spending:  ×{ClientSpendMultiplier:F1}
Staff Wages:      ×{WageDemandMultiplier:F1}
Bribe Costs:      ×{BribeCostMultiplier:F1}
Law Enforcement:  {EnforcementStrictness:P0}
Footfall:         ×{FootfallMultiplier:F1}
Property Values:  ×{PropertyValueMultiplier:F1}

Outlook: {GetOutlookText()}";
    }

    private string GetOutlookText() => _currentPhase switch
    {
        MacroPhase.Boom => "The economy is thriving. Clients spend freely. Strike while the iron is hot.",
        MacroPhase.Recession => "Hard times. Clients are desperate but cash-poor. Crime rises. Opportunities in exploitation.",
        MacroPhase.Stagnation => "Nothing changes. Steady income, predictable risks. A good time for long-term planning.",
        MacroPhase.PoliceCrackdown => "Law enforcement is aggressive. Bribes are expensive. Keep your head down.",
        _ => "Uncertain."
    };

    public override string ToString() =>
        $"[MacroEconomy] {_currentPhase} Day{_daysInPhase}/{_phaseDurationDays} " +
        $"Spend×{ClientSpendMultiplier}";
}
