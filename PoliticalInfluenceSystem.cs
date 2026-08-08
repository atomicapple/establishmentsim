using Godot;
using System;

/// <summary>Municipal figure the player can influence.</summary>
public enum MunicipalFigure { PrecinctCaptain, DistrictAttorney, CityCommissioner }

/// <summary>Relationship state with a municipal figure.</summary>
public class PoliticalRelationship
{
    public MunicipalFigure Figure { get; set; }
    public float Favor { get; set; } = 25f;     // 0-100
    public double MonthlyAllocation { get; set; }
    public string Name => Figure switch
    {
        MunicipalFigure.PrecinctCaptain => "Precinct Captain Morrison",
        MunicipalFigure.DistrictAttorney => "District Attorney Vance",
        MunicipalFigure.CityCommissioner => "Commissioner Ashford",
        _ => "Unknown"
    };
}

/// <summary>
/// Models civic corruption and political influence.
/// Tracks relationships with three municipal figures.
/// Monthly cash allocations increase favor, unlocking bonuses:
///   Precinct Captain → reduced Heat accumulation
///   District Attorney → prevents criminal charges during raids
///   City Commissioner → unlocks zoning permits for expansion
/// </summary>
public partial class PoliticalInfluenceSystem : Node, ISaveableSystem
{
    [Signal] public delegate void OnFavorChangedEventHandler(int figure, float newFavor, float delta);
    [Signal] public delegate void OnAllocationChangedEventHandler(int figure, double newAmount);
    [Signal] public delegate void OnChargesDismissedEventHandler();
    [Signal] public delegate void OnPermitUnlockedEventHandler(string permitType);

    private readonly PoliticalRelationship[] _relationships = new PoliticalRelationship[3];
    private readonly RandomNumberGenerator _rng = new();

    // ── Properties ─────────────────────────────────────────────────────

    public float PrecinctFavor => _relationships[0].Favor;
    public float DAFavor => _relationships[1].Favor;
    public float CommissionerFavor => _relationships[2].Favor;

    /// <summary>Heat reduction multiplier from precinct favor (0-100% at max favor).</summary>
    public float HeatReductionMultiplier => Mathf.Clamp(PrecinctFavor / 100f, 0f, 0.5f);

    /// <summary>Chance DA dismisses charges during a raid.</summary>
    public float ChargeDismissalChance => Mathf.Clamp(DAFavor / 120f, 0f, 0.8f);

    /// <summary>Whether zoning permits are unlocked (commissioner favor > 60).</summary>
    public bool ZoningPermitsUnlocked => CommissionerFavor >= 60f;

    public double TotalMonthlyAllocation
    {
        get
        {
            double total = 0;
            foreach (var r in _relationships) total += r.MonthlyAllocation;
            return total;
        }
    }

    public PoliticalRelationship[] Relationships => _relationships;

    public override void _Ready()
    {
        WorldRandom.Seed(_rng, nameof(PoliticalInfluenceSystem));

        _relationships[0] = new PoliticalRelationship { Figure = MunicipalFigure.PrecinctCaptain };
        _relationships[1] = new PoliticalRelationship { Figure = MunicipalFigure.DistrictAttorney };
        _relationships[2] = new PoliticalRelationship { Figure = MunicipalFigure.CityCommissioner };

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;

        GD.Print("[PoliticalInfluence] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Tick ─────────────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float sentiment)
    {
        // Favor naturally decays (politicians need constant attention)
        foreach (var rel in _relationships)
            rel.Favor = Mathf.Max(0, rel.Favor - 0.15f);

        // Monthly allocation applied every 30 days
        int day = GameStateManager.Instance?.DayCount ?? 0;
        if (day > 0 && day % 30 == 0)
        {
            ApplyMonthlyAllocations();
        }

        // Apply precinct favor → heat reduction
        if (PrecinctFavor > 0)
        {
            var heatSystem = FindNode<HeatSystem>("HeatSystem");
            if (heatSystem != null)
                heatSystem.RemoveHeat(HeatReductionMultiplier * 0.5f);
        }
    }

    private void ApplyMonthlyAllocations()
    {
        var gsm = GameStateManager.Instance;
        foreach (var rel in _relationships)
        {
            if (rel.MonthlyAllocation <= 0) continue;

            if (gsm != null && gsm.Cash >= rel.MonthlyAllocation)
            {
                gsm.Cash -= rel.MonthlyAllocation;
                // Favor gain proportional to allocation
                float gain = Mathf.Clamp((float)(rel.MonthlyAllocation / 100.0), 1f, 15f);
                rel.Favor = Mathf.Clamp(rel.Favor + gain, 0f, 100f);
            }
            else
            {
                // Can't pay → favor drops
                rel.Favor = Mathf.Max(0, rel.Favor - 8f);
            }
        }
    }

    // ── Bribe Slider API ───────────────────────────────────────────────

    /// <summary>Set monthly allocation (bribe) for a municipal figure.</summary>
    public void SetAllocation(MunicipalFigure figure, double amount)
    {
        int idx = (int)figure;
        _relationships[idx].MonthlyAllocation = Math.Max(0, amount);
        EmitSignal(SignalName.OnAllocationChanged, idx, amount);
        GD.Print($"[PoliticalInfluence] {_relationships[idx].Name}: ${amount:F0}/month");
    }

    /// <summary>Get the relationship for a figure.</summary>
    public PoliticalRelationship GetRelationship(MunicipalFigure figure) =>
        _relationships[(int)figure];

    // ── DA Charge Dismissal ────────────────────────────────────────────

    /// <summary>Attempt to dismiss criminal charges during a raid. Returns true if dismissed.</summary>
    public bool AttemptChargeDismissal()
    {
        if (_rng.Randf() < ChargeDismissalChance)
        {
            EmitSignal(SignalName.OnChargesDismissed);
            GD.Print("[PoliticalInfluence] DA dismissed charges!");
            return true;
        }
        return false;
    }

    // ── Commissioner Permits ───────────────────────────────────────────

    /// <summary>Check if a permit type is available. Unlocks at commissioner favor thresholds.</summary>
    public bool IsPermitAvailable(string permitType)
    {
        if (!ZoningPermitsUnlocked) return false;

        return permitType switch
        {
            "basic_expansion" => CommissionerFavor >= 60f,
            "liquor_license" => CommissionerFavor >= 75f,
            "extended_hours" => CommissionerFavor >= 85f,
            "gambling_permit" => CommissionerFavor >= 95f,
            _ => false
        };
    }

    /// <summary>Unlock a zoning permit. Costs favor with the commissioner.</summary>
    public bool UnlockPermit(string permitType)
    {
        if (!IsPermitAvailable(permitType)) return false;

        float cost = permitType switch
        {
            "basic_expansion" => 10f,
            "liquor_license" => 15f,
            "extended_hours" => 20f,
            "gambling_permit" => 30f,
            _ => 0f
        };

        _relationships[2].Favor -= cost;
        EmitSignal(SignalName.OnPermitUnlocked, permitType);
        GD.Print($"[PoliticalInfluence] Permit unlocked: {permitType} (cost {cost:F0} favor)");
        return true;
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "political";

    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        var figures = new System.Text.Json.Nodes.JsonArray();
        foreach (var rel in _relationships)
        {
            figures.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["figure"] = rel.Figure.ToString(),
                ["favor"] = rel.Favor,
                ["allocation"] = rel.MonthlyAllocation
            });
        }

        return new System.Text.Json.Nodes.JsonObject { ["figures"] = figures };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state?["figures"] is not System.Text.Json.Nodes.JsonArray figures) return;

        // Matched by figure name rather than array position, so reordering the
        // MunicipalFigure enum does not silently swap the player's relationships.
        foreach (var node in figures)
        {
            if (node is not System.Text.Json.Nodes.JsonObject entry) continue;
            if (!Enum.TryParse<MunicipalFigure>((string)entry["figure"], ignoreCase: true, out var figure))
                continue;

            foreach (var rel in _relationships)
            {
                if (rel.Figure != figure) continue;
                rel.Favor = Mathf.Clamp((float?)entry["favor"] ?? 25f, 0f, 100f);
                rel.MonthlyAllocation = Math.Max(0.0, (double?)entry["allocation"] ?? 0.0);
                break;
            }
        }

        GD.Print($"[PoliticalInfluence] Restored: Precinct={PrecinctFavor:F0} " +
                 $"DA={DAFavor:F0} Commissioner={CommissionerFavor:F0}");
    }

    private T FindNode<T>(string name) where T : Node =>
        GetTree()?.Root?.FindChild(name, true, false) as T;

    public override string ToString() =>
        $"[PoliticalInfluence] Precinct={PrecinctFavor:F0} DA={DAFavor:F0} " +
        $"Commissioner={CommissionerFavor:F0} Alloc=${TotalMonthlyAllocation:F0}/mo";
}
