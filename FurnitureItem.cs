using Godot;
using System;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>
/// Functional class of a furniture piece. A room's <see cref="RoomType"/>
/// requires a specific set of categories before it reads as "furnished" —
/// see <see cref="RoomAppointmentCalculator.GetRequiredCategories"/>.
/// </summary>
public enum FurnitureCategory
{
    Bed,
    Seating,
    Lighting,
    Rug,
    Decor,
    Vanity,
    Screen,
    Bath,
    Bar
}

/// <summary>
/// Visual/aesthetic school a piece belongs to. Rooms whose pieces share a
/// style read as deliberate and score a large coherence bonus; a jumble of
/// expensive mismatched pieces reads as a junk shop and is penalised.
/// </summary>
public enum FurnitureStyle
{
    Baroque,
    ArtDeco,
    Oriental,
    Bohemian,
    Modern,
    Spartan
}

// ── FurnitureItem Resource ─────────────────────────────────────────────

/// <summary>
/// A single placed furnishing. Furniture is the bottom of the economic
/// spine: pieces determine a room's Appointment score, Appointment drives
/// what clients will pay, and Upkeep drains cash every night whether the
/// room earns or not.
///
/// Usage: create as a Resource (inspector or code), then hand to
///   <c>VenueBuilding.AddFurniture(roomPos, item)</c>. Call
///   <see cref="Wear"/> once per night of use to degrade Condition.
/// </summary>
[GlobalClass]
public partial class FurnitureItem : Resource
{
    // ── Identity ───────────────────────────────────────────────────────

    [Export] public string ItemName { get; set; } = "Furnishing";

    [Export] public FurnitureCategory Category { get; set; } = FurnitureCategory.Decor;

    [Export] public FurnitureStyle StyleTag { get; set; } = FurnitureStyle.Modern;

    /// <summary>Quality band 1–5. Drives purchase price and wear resistance.</summary>
    [Export(PropertyHint.Range, "1,5,1")]
    public int Tier
    {
        get => _tier;
        set => _tier = Mathf.Clamp(value, MinTier, MaxTier);
    }
    private int _tier = 1;

    public const int MinTier = 1;
    public const int MaxTier = 5;

    // ── Contribution Stats (0–100) ─────────────────────────────────────

    /// <summary>How pleasant the piece is to actually use.</summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public float Comfort
    {
        get => _comfort;
        set => _comfort = Mathf.Clamp(value, 0f, 100f);
    }
    private float _comfort = 30f;

    /// <summary>How impressive the piece looks to a client walking in.</summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public float Prestige
    {
        get => _prestige;
        set => _prestige = Mathf.Clamp(value, 0f, 100f);
    }
    private float _prestige = 30f;

    // ── Economics ──────────────────────────────────────────────────────

    /// <summary>Maintenance cost in dollars per night, regardless of use.</summary>
    [Export] public double Upkeep { get; set; } = 1.0;

    /// <summary>Purchase price. Informational — the build UI owns the transaction.</summary>
    [Export] public double PurchasePrice { get; set; }

    /// <summary>Physical wear, 0–100. 100 is showroom, 0 is scrap.</summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public float Condition
    {
        get => _condition;
        set => _condition = Mathf.Clamp(value, 0f, 100f);
    }
    private float _condition = 100f;

    /// <summary>Tiles occupied inside the room's furniture budget.</summary>
    [Export] public Vector2I Footprint { get; set; } = Vector2I.One;

    // ── Derived ────────────────────────────────────────────────────────

    /// <summary>Tile area consumed by this piece (never below 1).</summary>
    public int Area => Mathf.Max(1, Footprint.X * Footprint.Y);

    /// <summary>True once wear has dropped the piece below usable quality.</summary>
    public bool IsDilapidated => Condition < DilapidatedThreshold;

    /// <summary>Condition below which a piece actively drags the room down.</summary>
    public const float DilapidatedThreshold = 30f;

    /// <summary>
    /// Effective contribution of the piece once wear is accounted for.
    /// A pristine tier-5 bed and a ruined tier-5 bed are not the same asset.
    /// </summary>
    public float EffectiveComfort => Comfort * ConditionFactor;

    /// <inheritdoc cref="EffectiveComfort"/>
    public float EffectivePrestige => Prestige * ConditionFactor;

    private float ConditionFactor => Mathf.Clamp(Condition / 100f, 0f, 1f);

    // ── Wear ───────────────────────────────────────────────────────────

    /// <summary>Base condition lost per unit of usage before tier resistance.</summary>
    /// <summary>
    /// Condition lost per use by a tier-1 piece. Deliberately small: wear is
    /// meant to be a slow drift the player counters with nightly maintenance,
    /// not a cliff. At 2.5 a furnished suite lost roughly half its Appointment
    /// over twenty nights with no way to arrest it.
    /// </summary>
    public const float BaseWearPerUse = 0.9f;

    /// <summary>
    /// Degrade Condition after a night's use. Higher tiers are built better
    /// and shed wear more slowly, so premium pieces amortise across more
    /// nights even though they cost more up front.
    /// </summary>
    /// <param name="usage">
    /// Usage intensity, typically 0–1 for a single booking. Negative values
    /// are ignored rather than silently repairing the item.
    /// </param>
    public void Wear(float usage)
    {
        if (usage <= 0f) return;

        // Tier 1 takes full wear; tier 5 takes ~60%.
        float tierResistance = 1f - ((Tier - MinTier) * 0.1f);
        float loss = BaseWearPerUse * usage * tierResistance;

        Condition = Mathf.Clamp(Condition - loss, 0f, 100f);
    }

    /// <summary>Restore condition (refurbishment). Clamped to 0–100.</summary>
    public void Repair(float amount)
    {
        if (amount <= 0f) return;
        Condition = Mathf.Clamp(Condition + amount, 0f, 100f);
    }

    /// <summary>Cost to bring this piece back to pristine condition.</summary>
    public double GetRefurbishCost()
    {
        float missing = Mathf.Clamp(100f - Condition, 0f, 100f);
        return Math.Round(PurchasePrice * 0.6 * (missing / 100.0), 2);
    }

    // ── Factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Build a stock piece with stats scaled off tier. Useful for catalog
    /// generation and tests; hand-authored resources may override anything.
    /// </summary>
    public static FurnitureItem Create(
        string itemName,
        FurnitureCategory category,
        FurnitureStyle style,
        int tier,
        Vector2I? footprint = null)
    {
        int t = Mathf.Clamp(tier, MinTier, MaxTier);
        float band = (t - MinTier) / (float)(MaxTier - MinTier);   // 0–1

        return new FurnitureItem
        {
            ItemName = itemName,
            Category = category,
            StyleTag = style,
            Tier = t,
            Comfort = Mathf.Lerp(20f, 92f, band),
            Prestige = Mathf.Lerp(12f, 96f, band),
            // Upkeep is the late-game brake: a large house must be expensive
            // simply to keep standing, or the economy collapses into free
            // money once the build-out is paid for. Scales steeply with tier
            // so a palace of tier-5 pieces is a genuine ongoing commitment.
            Upkeep = 1.5 + t * t * 1.4,
            PurchasePrice = 40 * Math.Pow(2.1, t - 1),
            Condition = 100f,
            Footprint = footprint ?? Vector2I.One
        };
    }

    public override string ToString() =>
        $"{ItemName} (T{Tier} {Category}/{StyleTag}) " +
        $"Cmf={Comfort:F0} Prs={Prestige:F0} Cond={Condition:F0} Upkeep=${Upkeep:F2}";
}
