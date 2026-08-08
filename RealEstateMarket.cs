using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Ownership status of a property.</summary>
public enum PropertyStatus { Unowned, Owned, Leased, Flipped, Sabotaged }

/// <summary>A tracked property in the real estate market.</summary>
public class MarketProperty
{
    public string DistrictName { get; set; }
    public double BaseValue { get; set; }
    public double CurrentValue { get; set; }
    public double MonthlyRent { get; set; }
    public PropertyStatus Status { get; set; } = PropertyStatus.Unowned;
    public float GentrificationScore { get; set; }   // 0–100
    public float CrimeRate { get; set; }              // 0–1
    public int DaysOwned { get; set; }
    public double TotalRentPaid { get; set; }
    public double FlipProfit { get; set; }
}

/// <summary>
/// Dynamic property market simulation. Property values and rent
/// rates respond to district gentrification, local crime, and
/// player expansion. Players can buy, lease, flip, or sabotage
/// district real estate to influence venue overhead.
/// </summary>
public partial class RealEstateMarket : Node
{
    [Signal] public delegate void OnPropertyPurchasedEventHandler(string district, double price);
    [Signal] public delegate void OnPropertyLeasedEventHandler(string district, double rent);
    [Signal] public delegate void OnPropertyFlippedEventHandler(string district, double profit);
    [Signal] public delegate void OnPropertySabotagedEventHandler(string district, float gentrificationLoss);

    private readonly Dictionary<string, MarketProperty> _properties = new();
    private readonly RandomNumberGenerator _rng = new();

    public IReadOnlyDictionary<string, MarketProperty> Properties => _properties;
    public int OwnedCount => _properties.Values.Count(p => p.Status == PropertyStatus.Owned);
    public double TotalPortfolioValue => _properties.Values.Where(p => p.Status == PropertyStatus.Owned).Sum(p => p.CurrentValue);

    // Market tuning
    public float GentrificationGrowthRate { get; set; } = 0.5f;    // per day, base
    public float CrimeDepreciationRate { get; set; } = 0.02f;       // value multiplier per crime point
    public float PlayerExpansionBoost { get; set; } = 1.5f;         // gentrification multiplier from player presence
    public float FlipProfitMargin { get; set; } = 0.15f;            // 15% profit on flip
    public double SabotageCost { get; set; } = 500.0;

    public override void _Ready()
    {
        WorldRandom.Seed(_rng, nameof(RealEstateMarket));
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        GD.Print("[RealEstateMarket] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    /// <summary>Register a district in the real estate market.</summary>
    public void RegisterDistrict(string name, double baseValue, float heat)
    {
        if (_properties.ContainsKey(name)) return;
        _properties[name] = new MarketProperty
        {
            DistrictName = name,
            BaseValue = baseValue,
            CurrentValue = baseValue,
            GentrificationScore = 30f + _rng.Randf() * 40f,
            CrimeRate = Mathf.Clamp(heat / 100f, 0f, 1f),
            MonthlyRent = baseValue * 0.02  // 2% monthly rent
        };
    }

    // ── Daily Market Update ────────────────────────────────────────────

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        foreach (var prop in _properties.Values)
        {
            // Gentrification: grows naturally, boosted by player ownership
            float gentrificationDelta = GentrificationGrowthRate;
            if (prop.Status == PropertyStatus.Owned)
                gentrificationDelta *= PlayerExpansionBoost;
            if (prop.Status == PropertyStatus.Sabotaged)
                gentrificationDelta = -GentrificationGrowthRate * 2f;

            prop.GentrificationScore = Mathf.Clamp(prop.GentrificationScore + gentrificationDelta, 0f, 100f);

            // Crime rate pulls from GameStateManager Heat
            prop.CrimeRate = Mathf.Clamp((GameStateManager.Instance?.Heat ?? 0) / 100f, 0f, 1f);

            // Property value = base × gentrification bonus × crime penalty
            float gentrificationMultiplier = 0.5f + (prop.GentrificationScore / 100f) * 1.5f;
            float crimeMultiplier = 1f - (prop.CrimeRate * CrimeDepreciationRate * 100f);
            prop.CurrentValue = prop.BaseValue * gentrificationMultiplier * Mathf.Max(0.3f, crimeMultiplier);

            // Rent = 2% of current value monthly
            prop.MonthlyRent = prop.CurrentValue * 0.02;

            // Track ownership
            if (prop.Status == PropertyStatus.Owned) prop.DaysOwned++;

            // Collect rent on leased properties
            if (prop.Status == PropertyStatus.Leased && GameStateManager.Instance != null)
            {
                double dailyRent = prop.MonthlyRent / 30.0;
                GameStateManager.Instance.Cash += dailyRent;
                prop.TotalRentPaid += dailyRent;
            }
        }
    }

    // ── Player Actions ─────────────────────────────────────────────────

    /// <summary>Buy a property outright.</summary>
    public bool BuyProperty(string districtName)
    {
        if (!_properties.TryGetValue(districtName, out var prop)) return false;
        if (prop.Status != PropertyStatus.Unowned) return false;

        var gsm = GameStateManager.Instance;
        if (gsm != null && gsm.Cash < prop.CurrentValue) return false;

        if (gsm != null) gsm.Cash -= prop.CurrentValue;
        prop.Status = PropertyStatus.Owned;
        prop.DaysOwned = 0;

        EmitSignal(SignalName.OnPropertyPurchased, districtName, prop.CurrentValue);
        GD.Print($"[RealEstate] Bought {districtName} for ${prop.CurrentValue:F0}.");
        return true;
    }

    /// <summary>Lease a property instead of buying (monthly rent, lower upfront).</summary>
    public bool LeaseProperty(string districtName)
    {
        if (!_properties.TryGetValue(districtName, out var prop)) return false;
        if (prop.Status != PropertyStatus.Unowned) return false;

        double downPayment = prop.CurrentValue * 0.1;
        var gsm = GameStateManager.Instance;
        if (gsm != null && gsm.Cash < downPayment) return false;

        if (gsm != null) gsm.Cash -= downPayment;
        prop.Status = PropertyStatus.Leased;

        EmitSignal(SignalName.OnPropertyLeased, districtName, prop.MonthlyRent);
        GD.Print($"[RealEstate] Leased {districtName} for ${downPayment:F0} down, ${prop.MonthlyRent:F0}/mo.");
        return true;
    }

    /// <summary>Flip an owned property for profit.</summary>
    public bool FlipProperty(string districtName)
    {
        if (!_properties.TryGetValue(districtName, out var prop)) return false;
        if (prop.Status != PropertyStatus.Owned) return false;

        double salePrice = prop.CurrentValue * (1f + FlipProfitMargin);
        double profit = salePrice - (prop.BaseValue * (1f + FlipProfitMargin * 0.5f));

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash += salePrice;

        prop.FlipProfit = profit;
        prop.Status = PropertyStatus.Flipped;

        EmitSignal(SignalName.OnPropertyFlipped, districtName, profit);
        GD.Print($"[RealEstate] Flipped {districtName} — sold for ${salePrice:F0}, profit ${profit:F0}.");
        return true;
    }

    /// <summary>Sabotage a rival property to reduce its gentrification.</summary>
    public bool SabotageProperty(string districtName)
    {
        if (!_properties.TryGetValue(districtName, out var prop)) return false;
        if (prop.Status == PropertyStatus.Sabotaged) return false;

        var gsm = GameStateManager.Instance;
        if (gsm != null && gsm.Cash < SabotageCost) return false;

        if (gsm != null) gsm.Cash -= SabotageCost;

        float gentrificationLoss = 15f + _rng.Randf() * 15f;
        prop.GentrificationScore = Mathf.Max(0, prop.GentrificationScore - gentrificationLoss);
        prop.Status = PropertyStatus.Sabotaged;

        // Heat increases from sabotage
        if (gsm != null) gsm.Heat += 10f;

        EmitSignal(SignalName.OnPropertySabotaged, districtName, gentrificationLoss);
        GD.Print($"[RealEstate] Sabotaged {districtName}: Gentrification −{gentrificationLoss:F0}, Heat +10.");
        return true;
    }

    /// <summary>Repair a sabotaged property (restore status to unowned).</summary>
    public bool RepairProperty(string districtName)
    {
        if (!_properties.TryGetValue(districtName, out var prop)) return false;
        if (prop.Status != PropertyStatus.Sabotaged) return false;

        prop.Status = PropertyStatus.Unowned;
        prop.GentrificationScore += 5f;
        GD.Print($"[RealEstate] Repaired {districtName}.");
        return true;
    }

    // ── Market Queries ─────────────────────────────────────────────────

    /// <summary>Get estimated monthly overhead for owned properties.</summary>
    public double GetMonthlyOverhead()
    {
        return _properties.Values
            .Where(p => p.Status == PropertyStatus.Owned)
            .Sum(p => p.MonthlyRent * 0.3); // 30% of rent is overhead (taxes, maintenance)
    }

    /// <summary>Get market trend: "rising", "stable", or "declining".</summary>
    public string GetMarketTrend()
    {
        double avgGentrification = _properties.Values.Any()
            ? _properties.Values.Average(p => p.GentrificationScore) : 50;
        return avgGentrification > 60 ? "rising" : avgGentrification < 40 ? "declining" : "stable";
    }

    /// <summary>Get properties sorted by investment potential (value growth projection).</summary>
    public List<MarketProperty> GetInvestmentRankings()
    {
        return _properties.Values
            .OrderByDescending(p => p.GentrificationScore * (1f - p.CrimeRate) - (p.Status != PropertyStatus.Unowned ? 100 : 0))
            .ToList();
    }

    public MarketProperty GetProperty(string name) =>
        _properties.TryGetValue(name, out var p) ? p : null;

    public override string ToString() =>
        $"[RealEstate] {_properties.Count} properties, {OwnedCount} owned, " +
        $"Portfolio=${TotalPortfolioValue:F0}, Trend={GetMarketTrend()}";
}
