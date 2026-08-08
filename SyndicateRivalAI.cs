using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A rival criminal syndicate operating in adjacent territory.</summary>
public class RivalSyndicate
{
    public string Name { get; set; }
    public string District { get; set; }
    public float Power { get; set; } = 50f;       // 0-100
    public float Aggression { get; set; } = 30f;   // 0-100, drives raid frequency
    public float Respect { get; set; } = 20f;      // toward player
    public bool IsHostile => Respect < 15f;
    public bool IsNeutral => Respect >= 15f && Respect < 50f;
    public bool IsAllied => Respect >= 70f;
}

/// <summary>Record of a rival action taken against the player.</summary>
public struct RivalAction
{
    public int Day;
    public string SyndicateName;
    public string ActionType;
    public string Description;
    public float Severity;
}

/// <summary>
/// Simulates rival criminal factions in adjacent districts.
/// Executes weekly decisions: turf expansion, staff poaching,
/// extortion rackets. Player can retaliate via security,
/// protection fees, or counter-sabotage.
/// </summary>
public partial class SyndicateRivalAI : Node, ISaveableSystem
{
    [Signal] public delegate void OnRivalActionEventHandler(string syndicate, string action, float severity);
    [Signal] public delegate void OnTurfExpandedEventHandler(string syndicate, string district);
    [Signal] public delegate void OnStaffPoachedEventHandler(string staffName, string syndicate, double offer);
    [Signal] public delegate void OnExtortionAttemptEventHandler(string syndicate, double amount);

    private readonly List<RivalSyndicate> _syndicates = new();
    private readonly List<RivalAction> _actionLog = new();
    private readonly RandomNumberGenerator _rng = new();
    private int _lastWeeklyTick;
    private double _protectionFeePaid;

    public IReadOnlyList<RivalSyndicate> Syndicates => _syndicates;
    public IReadOnlyList<RivalAction> ActionLog => _actionLog;
    public bool ProtectionActive => _protectionFeePaid > 0;
    public double ProtectionFee => _protectionFeePaid;

    public override void _Ready()
    {
        WorldRandom.Seed(_rng, nameof(SyndicateRivalAI));

        _syndicates.AddRange(new[]
        {
            new RivalSyndicate { Name="The Iron Circle", District="Harbor Wharfs", Power=65f, Aggression=45f },
            new RivalSyndicate { Name="Velvet Cartel", District="Gilded Heights", Power=40f, Aggression=20f },
            new RivalSyndicate { Name="Ash Row Collective", District="The Warrens", Power=55f, Aggression=60f },
        });

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;

        GD.Print("[SyndicateRivalAI] Initialized with 3 rival factions.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    private void OnDailyTick(double cash, float reputation, float heat, float sentiment)
    {
        int day = GameStateManager.Instance?.DayCount ?? 0;

        // Weekly tick (every 7 days)
        if (day > 0 && day % 7 == 0 && day != _lastWeeklyTick)
        {
            _lastWeeklyTick = day;
            ExecuteWeeklyDecisions();
        }

        // Protection fee expires after 7 days
        if (_protectionFeePaid > 0 && day % 7 == 0)
            _protectionFeePaid = 0;
    }

    // ── Weekly Decisions ───────────────────────────────────────────────

    private void ExecuteWeeklyDecisions()
    {
        foreach (var syndicate in _syndicates)
        {
            // Each syndicate takes 1-2 actions
            int actions = (int)(_rng.Randi() % 2) + 1;
            for (int i = 0; i < actions; i++)
            {
                if (syndicate.IsHostile)
                    ExecuteHostileAction(syndicate);
                else if (!syndicate.IsAllied)
                    ExecuteNeutralAction(syndicate);
            }
        }

        // Natural power shifts
        foreach (var s in _syndicates)
            s.Power = Mathf.Clamp(s.Power + (_rng.Randf() - 0.5f) * 3f, 10f, 100f);
    }

    private void ExecuteHostileAction(RivalSyndicate syndicate)
    {
        float roll = _rng.Randf();

        if (roll < 0.4f)
        {
            // Expand turf
            string target = "Iron Gate Slums";
            LogAction(syndicate, "turf_expansion", $"Expanding into {target}", 5f);
            EmitSignal(SignalName.OnTurfExpanded, syndicate.Name, target);
        }
        else if (roll < 0.7f)
        {
            // Poach staff
            AttemptStaffPoaching(syndicate);
        }
        else
        {
            // Extortion racket
            double amount = 300 + _rng.Randi() % 700;
            LogAction(syndicate, "extortion", $"Demanding ${amount:F0} protection money", 8f);
            EmitSignal(SignalName.OnExtortionAttempt, syndicate.Name, amount);

            if (!ProtectionActive && GameStateManager.Instance != null)
            {
                GameStateManager.Instance.Reputation -= 3f;
                GameStateManager.Instance.Heat += 5f;
            }
        }
    }

    private void ExecuteNeutralAction(RivalSyndicate syndicate)
    {
        float roll = _rng.Randf();

        if (roll < 0.3f)
            AttemptStaffPoaching(syndicate);
        else if (roll < 0.5f)
            LogAction(syndicate, "intel_gathering", "Gathering intelligence on player operations", 2f);

        // Respect drifts toward 25 (neutral) or away based on player heat
        float drift = (GameStateManager.Instance?.Heat ?? 0) > 50 ? +3f : -1f;
        syndicate.Respect = Mathf.Clamp(syndicate.Respect + drift, 0f, 100f);
    }

    // ── Staff Poaching ─────────────────────────────────────────────────

    private void AttemptStaffPoaching(RivalSyndicate syndicate)
    {
        var allStaff = FindAllStaff();
        if (allStaff.Count == 0) return;

        var target = allStaff[(int)(_rng.Randi() % (uint)allStaff.Count)];
        double offer = target.Salary * (1.5 + _rng.Randf());

        LogAction(syndicate, "staff_poaching", $"Offering {target.StaffName} ${offer:F0}/month", 6f);
        EmitSignal(SignalName.OnStaffPoached, target.StaffName, syndicate.Name, offer);

        // Staff satisfaction hit from the offer (temptation)
        target.Satisfaction -= 5f;
    }

    // ── Player Retaliation ─────────────────────────────────────────────

    /// <summary>Hire security guards. Reduces hostile action frequency.</summary>
    public void HireSecurity(double cost)
    {
        if (GameStateManager.Instance?.Cash < cost) return;
        GameStateManager.Instance.Cash -= cost;

        foreach (var s in _syndicates)
            s.Aggression = Mathf.Max(0, s.Aggression - 15f);

        GD.Print($"[SyndicateRivalAI] Security hired (${cost:F0}). Rival aggression -15.");
    }

    /// <summary>Pay protection fee to the most hostile syndicate.</summary>
    public void PayProtectionFee(double amount)
    {
        if (GameStateManager.Instance?.Cash < amount) return;
        GameStateManager.Instance.Cash -= amount;
        _protectionFeePaid = amount;

        // Find most hostile
        var mostHostile = _syndicates[0];
        foreach (var s in _syndicates)
            if (s.Respect < mostHostile.Respect) mostHostile = s;

        mostHostile.Respect += 25f;
        mostHostile.Aggression = Mathf.Max(0, mostHostile.Aggression - 20f);

        GD.Print($"[SyndicateRivalAI] Protection paid to {mostHostile.Name} (${amount:F0}).");
    }

    /// <summary>Launch counter-sabotage against a specific syndicate.</summary>
    public void CounterSabotage(string syndicateName, double cost)
    {
        var target = _syndicates.Find(s => s.Name == syndicateName);
        if (target == null) return;
        if (GameStateManager.Instance?.Cash < cost) return;

        GameStateManager.Instance.Cash -= cost;
        target.Power -= 15f;
        target.Aggression -= 10f;
        target.Respect += 10f;

        LogAction(target, "counter_sabotage", "Player retaliated with sabotage", 10f);
        GD.Print($"[SyndicateRivalAI] Counter-sabotage on {syndicateName} (${cost:F0}). Power -15.");

        // Anyone in the house carrying a grudge against this faction gets
        // their satisfaction. Poached staff arrive with exactly that grudge,
        // so this is the payoff for the Revenge ambition — which was
        // otherwise unreachable and simply bled loyalty forever.
        StaffRoster.Instance?.RegisterActionAgainstFaction(syndicateName, 0.6f);
    }

    // ── Utility ────────────────────────────────────────────────────────

    private void LogAction(RivalSyndicate syndicate, string actionType, string desc, float severity)
    {
        _actionLog.Add(new RivalAction
        {
            Day = GameStateManager.Instance?.DayCount ?? 0,
            SyndicateName = syndicate.Name,
            ActionType = actionType,
            Description = desc,
            Severity = severity
        });
        EmitSignal(SignalName.OnRivalAction, syndicate.Name, actionType, severity);
    }

    private List<StaffMember> FindAllStaff()
    {
        // Previously read GetStaffAtRisk(), so rivals could only ever poach
        // staff already at Stress >= 80.
        return StaffRoster.Instance?.GetAll().ToList() ?? new List<StaffMember>();
    }

    public RivalSyndicate GetSyndicate(string name) =>
        _syndicates.Find(s => s.Name == name);

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "syndicates";

    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        var factions = new System.Text.Json.Nodes.JsonArray();
        foreach (var s in _syndicates)
        {
            factions.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = s.Name,
                ["district"] = s.District,
                ["power"] = s.Power,
                ["aggression"] = s.Aggression,
                ["respect"] = s.Respect
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["factions"] = factions,
            ["lastWeeklyTick"] = _lastWeeklyTick,
            ["protectionFeePaid"] = _protectionFeePaid
        };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        // Factions are matched by name against the ones already built in
        // _Ready rather than rebuilt from the save, so that adding a faction
        // in a later build doesn't strand old campaigns without it.
        if (state["factions"] is System.Text.Json.Nodes.JsonArray factions)
        {
            foreach (var node in factions)
            {
                if (node is not System.Text.Json.Nodes.JsonObject entry) continue;

                var name = (string)entry["name"];
                var syndicate = _syndicates.Find(s => s.Name == name);
                if (syndicate == null) continue;

                syndicate.Power = Mathf.Clamp((float?)entry["power"] ?? 50f, 0f, 100f);
                syndicate.Aggression = Mathf.Clamp((float?)entry["aggression"] ?? 30f, 0f, 100f);
                syndicate.Respect = Mathf.Clamp((float?)entry["respect"] ?? 20f, 0f, 100f);
            }
        }

        _lastWeeklyTick = (int?)state["lastWeeklyTick"] ?? 0;
        _protectionFeePaid = (double?)state["protectionFeePaid"] ?? 0.0;

        GD.Print($"[SyndicateRivalAI] Restored {_syndicates.Count} factions.");
    }

    public override string ToString() =>
        $"[SyndicateRivalAI] Factions={_syndicates.Count} Protection={(ProtectionActive ? $"active" : "none")}";
}
