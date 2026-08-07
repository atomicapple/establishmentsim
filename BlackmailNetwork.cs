using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A single piece of gathered intelligence.</summary>
public class CapitalFavor
{
    public string Id { get; set; } = Guid.NewGuid().ToString()[..8];
    public string TargetName { get; set; }
    public string IntelType { get; set; }  // "financial", "personal", "criminal", "political"
    public int Rarity { get; set; } = 1;   // 1-3, higher = more valuable
    public int DayAcquired { get; set; }
    public string Description { get; set; }

    public override string ToString() => $"[{IntelType}] {TargetName} (R{Rarity}) — {Description}";
}

/// <summary>
/// Blackmail & information trading subsystem.
/// Staff assigned to Information Gathering roll discovery checks
/// against VIP clients based on Discretion. Stores intel as
/// Capital Favors — reusable inventory assets that can be burned
/// to quash investigations, extort cash, or pressure rivals.
/// </summary>
public partial class BlackmailNetwork : Node, ISaveableSystem
{
    [Signal] public delegate void OnIntelGatheredEventHandler(string target, string intelType, int rarity);
    [Signal] public delegate void OnCapitalFavorBurnedEventHandler(string favorId, string purpose);
    [Signal] public delegate void OnDiscoveryCheckEventHandler(string staffName, float discretion, bool success);

    private readonly List<CapitalFavor> _inventory = new();
    private readonly RandomNumberGenerator _rng = new();
    private readonly List<string> _discoveredTargets = new();

    public IReadOnlyList<CapitalFavor> Inventory => _inventory;
    public int TotalFavorsEarned { get; private set; }
    public int TotalFavorsBurned { get; private set; }

    // Discovery tuning
    public float BaseDiscoveryChance { get; set; } = 0.25f;
    public float DiscretionBonusPerPoint { get; set; } = 0.006f;

    public override void _Ready()
    {
        _rng.Randomize();
        GD.Print("[BlackmailNetwork] Initialized.");
    }

    /// <summary>
    /// Roll a discovery check when a staff member with Information Gathering
    /// specialization entertains VIP clients. Success = new CapitalFavor.
    /// </summary>
    public bool AttemptIntelGathering(StaffMember staff, string clientName)
    {
        if (staff == null) return false;

        float chance = BaseDiscoveryChance + (staff.Discretion * DiscretionBonusPerPoint);
        chance = Mathf.Clamp(chance, 0.05f, 0.85f);

        float roll = _rng.Randf();
        bool success = roll < chance;

        EmitSignal(SignalName.OnDiscoveryCheck, staff.StaffName, staff.Discretion, success);

        if (!success) return false;

        // Generate intel
        var intelTypes = new[] { "financial", "personal", "criminal", "political" };
        var targets = new[] { "Judge Hawthorne", "Councilman Vex", "CEO Sterling",
                              "Captain Morrison", "Madame Nyx", "The Broker" };

        var favor = new CapitalFavor
        {
            TargetName = targets[_rng.Randi() % targets.Length],
            IntelType = intelTypes[_rng.Randi() % intelTypes.Length],
            Rarity = (int)(_rng.Randi() % 3) + 1,
            DayAcquired = GameStateManager.Instance?.DayCount ?? 0,
            Description = $"Sensitive intel gathered by {staff.StaffName} from {clientName}."
        };

        _inventory.Add(favor);
        _discoveredTargets.Add(favor.TargetName);
        TotalFavorsEarned++;

        EmitSignal(SignalName.OnIntelGathered, favor.TargetName, favor.IntelType, favor.Rarity);
        GD.Print($"[BlackmailNetwork] Intel gathered: {favor}");

        return true;
    }

    /// <summary>Burn a Capital Favor to quash a police investigation (Heat -25).</summary>
    public bool BurnForInvestigationQuash()
    {
        var favor = GetBestFavor("criminal", "political");
        if (favor == null) return false;

        var heatSystem = FindNode<HeatSystem>("HeatSystem");
        if (heatSystem != null)
            heatSystem.RemoveHeat(25f);

        ConsumeFavor(favor, "investigation_quash");
        GD.Print($"[BlackmailNetwork] Burned {favor.Id}: investigation quashed, Heat -25.");
        return true;
    }

    /// <summary>Burn a Capital Favor to extort cash (amount scales with rarity).</summary>
    public double BurnForExtortion()
    {
        var favor = GetBestFavor("financial", "personal");
        if (favor == null) return 0;

        double cash = favor.Rarity * 800 + _rng.Randi() % 500;
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash += cash;

        ConsumeFavor(favor, "extortion");
        GD.Print($"[BlackmailNetwork] Burned {favor.Id}: extorted ${cash:F0}.");
        return cash;
    }

    /// <summary>Burn a Capital Favor to force rival syndicates to reduce turf pressure.</summary>
    public bool BurnForRivalPressure()
    {
        var favor = GetBestFavor("criminal");
        if (favor == null) return false;

        // Reduce district heat in rival territories
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Heat -= 10f;

        ConsumeFavor(favor, "rival_pressure");
        GD.Print($"[BlackmailNetwork] Burned {favor.Id}: rival pressure reduced, Heat -10.");
        return true;
    }

    /// <summary>Get count of favors by type.</summary>
    public int CountByType(string intelType) =>
        _inventory.Count(f => f.IntelType == intelType);

    private CapitalFavor GetBestFavor(params string[] preferredTypes)
    {
        return _inventory
            .Where(f => preferredTypes.Contains(f.IntelType))
            .OrderByDescending(f => f.Rarity)
            .FirstOrDefault();
    }

    private void ConsumeFavor(CapitalFavor favor, string purpose)
    {
        _inventory.Remove(favor);
        TotalFavorsBurned++;
        EmitSignal(SignalName.OnCapitalFavorBurned, favor.Id, purpose);
    }

    private T FindNode<T>(string name) where T : Node =>
        GetTree()?.Root?.FindChild(name, true, false) as T;

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "blackmail";

    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        var favors = new System.Text.Json.Nodes.JsonArray();
        foreach (var f in _inventory)
        {
            favors.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = f.Id,
                ["target"] = f.TargetName,
                ["intelType"] = f.IntelType,
                ["rarity"] = f.Rarity,
                ["dayAcquired"] = f.DayAcquired,
                ["description"] = f.Description
            });
        }

        var targets = new System.Text.Json.Nodes.JsonArray();
        foreach (var t in _discoveredTargets)
            targets.Add(t);

        return new System.Text.Json.Nodes.JsonObject
        {
            ["inventory"] = favors,
            ["discoveredTargets"] = targets,
            ["totalEarned"] = TotalFavorsEarned,
            ["totalBurned"] = TotalFavorsBurned
        };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        _inventory.Clear();
        _discoveredTargets.Clear();

        if (state["inventory"] is System.Text.Json.Nodes.JsonArray favors)
        {
            foreach (var node in favors)
            {
                if (node is not System.Text.Json.Nodes.JsonObject entry) continue;

                _inventory.Add(new CapitalFavor
                {
                    Id = (string)entry["id"] ?? Guid.NewGuid().ToString()[..8],
                    TargetName = (string)entry["target"] ?? "Unknown",
                    IntelType = (string)entry["intelType"] ?? "personal",
                    Rarity = Math.Clamp((int?)entry["rarity"] ?? 1, 1, 3),
                    DayAcquired = (int?)entry["dayAcquired"] ?? 0,
                    Description = (string)entry["description"] ?? ""
                });
            }
        }

        if (state["discoveredTargets"] is System.Text.Json.Nodes.JsonArray targets)
        {
            foreach (var node in targets)
            {
                var name = (string)node;
                if (!string.IsNullOrEmpty(name)) _discoveredTargets.Add(name);
            }
        }

        TotalFavorsEarned = Math.Max(0, (int?)state["totalEarned"] ?? 0);
        TotalFavorsBurned = Math.Max(0, (int?)state["totalBurned"] ?? 0);

        GD.Print($"[BlackmailNetwork] Restored: {_inventory.Count} favors held, " +
                 $"{_discoveredTargets.Count} targets known.");
    }

    public override string ToString() =>
        $"[BlackmailNetwork] Favors={_inventory.Count} Earned={TotalFavorsEarned} Burned={TotalFavorsBurned}";
}
