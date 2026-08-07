using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>
/// Component-by-component explanation of an Appointment score, for UI
/// tooltips, the build screen, and balance debugging.
/// </summary>
public struct AppointmentBreakdown
{
    /// <summary>Does the room have the furniture categories its type needs? 0–100.</summary>
    public float Coverage;

    /// <summary>Weighted mean of Comfort and Prestige across placed items. 0–100.</summary>
    public float Quality;

    /// <summary>How well the placed styles agree with each other. 0–100.</summary>
    public float Coherence;

    /// <summary>Multiplier from mean wear. 0.45 (ruined) – 1.0 (pristine).</summary>
    public float ConditionMultiplier;

    /// <summary>Final composite score, 0–100.</summary>
    public float Appointment;

    // ── Supporting detail ──────────────────────────────────────────────
    public int ItemCount;
    public float MeanCondition;
    public FurnitureStyle DominantStyle;

    /// <summary>Share of items in (or compatible with) the dominant style, 0–1.</summary>
    public float DominantStyleShare;

    public int DistinctStyleCount;

    /// <summary>Required categories this room is still missing.</summary>
    public List<FurnitureCategory> MissingCategories;

    /// <summary>True if the room lacks the one category its type cannot function without.</summary>
    public bool MissingEssential;

    public override string ToString()
    {
        string missing = MissingCategories is { Count: > 0 }
            ? string.Join(", ", MissingCategories)
            : "none";

        return $"Appointment {Appointment:F1}/100 — " +
               $"Coverage {Coverage:F1} (×0.4), Quality {Quality:F1} (×0.4), " +
               $"Coherence {Coherence:F1} (×0.2), Condition ×{ConditionMultiplier:F2}\n" +
               $"  Items={ItemCount} Styles={DistinctStyleCount} " +
               $"Dominant={DominantStyle} ({DominantStyleShare:P0}) " +
               $"MeanCond={MeanCondition:F0}\n" +
               $"  Missing: {missing}{(MissingEssential ? " [ESSENTIAL MISSING]" : "")}";
    }
}

// ── RoomAppointmentCalculator ──────────────────────────────────────────

/// <summary>
/// Turns a pile of <see cref="FurnitureItem"/> into a single 0–100
/// "Appointment" score — how well-appointed the room reads to a client
/// standing in the doorway.
///
///   Appointment = (Coverage·0.4 + Quality·0.4 + Coherence·0.2) · ConditionMultiplier
///
/// The design intent is that <b>cheap but matching beats expensive but
/// mismatched per dollar</b>. Quality scales with tier and therefore with
/// spend, but Coherence is free: five tier-1 pieces in one style land
/// around 85% of the Appointment of five tier-5 pieces in five styles for
/// roughly 5% of the money. Coverage uses diminishing returns past the
/// required category set, so cramming furniture in cannot brute-force it.
///
/// Usage: <c>RoomAppointmentCalculator.Calculate(room)</c> for the number,
///   <c>GetBreakdown(room)</c> for the components.
/// </summary>
public static class RoomAppointmentCalculator
{
    // ── Composite Weights ──────────────────────────────────────────────

    public const float CoverageWeight = 0.4f;
    public const float QualityWeight = 0.4f;
    public const float CoherenceWeight = 0.2f;

    // ── Coverage Tuning ────────────────────────────────────────────────

    /// <summary>Share of Coverage earned by satisfying the required category set.</summary>
    public const float RequiredCategoryWeight = 85f;

    /// <summary>Share of Coverage earnable from extra pieces (diminishing).</summary>
    public const float ExtraItemWeight = 15f;

    /// <summary>Saturation rate for extra pieces — lower means faster diminishing.</summary>
    public const float ExtraItemFalloff = 0.45f;

    /// <summary>
    /// Coverage multiplier applied when the room lacks its essential
    /// category. A suite with no bed is not a suite.
    /// </summary>
    public const float MissingEssentialPenalty = 0.15f;

    // ── Coherence Tuning ───────────────────────────────────────────────

    /// <summary>Dominant-style share at which the large coherence bonus unlocks.</summary>
    public const float CoherenceThreshold = 0.7f;

    /// <summary>Coherence awarded exactly at the threshold (the cliff top).</summary>
    public const float CoherentFloor = 72f;

    /// <summary>Best coherence achievable below the threshold (the cliff bottom).</summary>
    public const float IncoherentCeiling = 58f;

    /// <summary>Coherence for a total jumble.</summary>
    public const float IncoherentFloor = 8f;

    /// <summary>Credit fraction given to styles merely compatible with the dominant one.</summary>
    public const float CompatibleStyleCredit = 0.5f;

    /// <summary>Coherence lost per distinct style beyond the second.</summary>
    public const float PerExtraStylePenalty = 5f;

    // ── Condition Tuning ───────────────────────────────────────────────

    /// <summary>Condition multiplier when every piece is scrap.</summary>
    public const float MinConditionMultiplier = 0.45f;

    // ── Category Requirements ──────────────────────────────────────────

    private static readonly Dictionary<RoomType, FurnitureCategory[]> RequiredCategories = new()
    {
        [RoomType.VIPSuite] = new[]
        {
            FurnitureCategory.Bed, FurnitureCategory.Seating, FurnitureCategory.Lighting,
            FurnitureCategory.Decor, FurnitureCategory.Bath
        },
        [RoomType.PrivateSuite] = new[]
        {
            FurnitureCategory.Bed, FurnitureCategory.Lighting, FurnitureCategory.Vanity
        },
        [RoomType.Lounge] = new[]
        {
            FurnitureCategory.Seating, FurnitureCategory.Lighting,
            FurnitureCategory.Rug, FurnitureCategory.Decor
        },
        [RoomType.Bar] = new[]
        {
            FurnitureCategory.Bar, FurnitureCategory.Seating, FurnitureCategory.Lighting
        },
        [RoomType.VIPEntrance] = new[]
        {
            FurnitureCategory.Lighting, FurnitureCategory.Decor, FurnitureCategory.Seating
        },
        [RoomType.Medical] = new[]
        {
            FurnitureCategory.Bed, FurnitureCategory.Lighting
        },
        [RoomType.Security] = new[]
        {
            FurnitureCategory.Screen, FurnitureCategory.Lighting
        },
        [RoomType.Service] = new[] { FurnitureCategory.Lighting },
        [RoomType.Soundproofing] = new[] { FurnitureCategory.Screen },
        [RoomType.Storage] = Array.Empty<FurnitureCategory>(),
        [RoomType.Empty] = Array.Empty<FurnitureCategory>()
    };

    /// <summary>
    /// The one category a room type cannot function without. Its absence
    /// collapses Coverage rather than merely denting it.
    /// </summary>
    private static readonly Dictionary<RoomType, FurnitureCategory> EssentialCategory = new()
    {
        [RoomType.VIPSuite] = FurnitureCategory.Bed,
        [RoomType.PrivateSuite] = FurnitureCategory.Bed,
        [RoomType.Medical] = FurnitureCategory.Bed,
        [RoomType.Lounge] = FurnitureCategory.Seating,
        [RoomType.Bar] = FurnitureCategory.Bar,
        [RoomType.Security] = FurnitureCategory.Screen,
        [RoomType.VIPEntrance] = FurnitureCategory.Lighting
    };

    /// <summary>
    /// Comfort-vs-Prestige weighting per room type. Bedrooms are judged on
    /// how they feel, public rooms on how they look.
    /// </summary>
    private static readonly Dictionary<RoomType, float> ComfortBias = new()
    {
        [RoomType.VIPSuite] = 0.55f,
        [RoomType.PrivateSuite] = 0.70f,
        [RoomType.Medical] = 0.85f,
        [RoomType.Lounge] = 0.45f,
        [RoomType.Bar] = 0.35f,
        [RoomType.VIPEntrance] = 0.20f,
        [RoomType.Security] = 0.60f,
        [RoomType.Service] = 0.75f,
        [RoomType.Storage] = 0.90f,
        [RoomType.Soundproofing] = 0.80f
    };

    /// <summary>
    /// Style pairs that read as intentional when mixed. Anything not listed
    /// here clashes.
    /// </summary>
    private static readonly Dictionary<FurnitureStyle, FurnitureStyle[]> CompatibleStyles = new()
    {
        [FurnitureStyle.Baroque] = new[] { FurnitureStyle.Oriental },
        [FurnitureStyle.Oriental] = new[] { FurnitureStyle.Baroque, FurnitureStyle.Bohemian },
        [FurnitureStyle.Bohemian] = new[] { FurnitureStyle.Oriental },
        [FurnitureStyle.ArtDeco] = new[] { FurnitureStyle.Modern },
        [FurnitureStyle.Modern] = new[] { FurnitureStyle.ArtDeco, FurnitureStyle.Spartan },
        [FurnitureStyle.Spartan] = new[] { FurnitureStyle.Modern }
    };

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Categories a room of this type is expected to contain.</summary>
    public static IReadOnlyList<FurnitureCategory> GetRequiredCategories(RoomType type) =>
        RequiredCategories.TryGetValue(type, out var cats)
            ? cats
            : Array.Empty<FurnitureCategory>();

    /// <summary>
    /// The single category whose absence guts the room, or null if the type
    /// has no such dependency.
    /// </summary>
    public static FurnitureCategory? GetEssentialCategory(RoomType type) =>
        EssentialCategory.TryGetValue(type, out var cat) ? cat : null;

    /// <summary>Appointment score, 0–100, for a room and its contents.</summary>
    public static float Calculate(RoomType type, IReadOnlyList<FurnitureItem> items) =>
        GetBreakdown(type, items).Appointment;

    /// <summary>Appointment score for a placed room.</summary>
    public static float Calculate(RoomModule room) =>
        room == null ? 0f : GetBreakdown(room.Type, room.Furniture).Appointment;

    /// <summary>Component breakdown for a placed room.</summary>
    public static AppointmentBreakdown GetBreakdown(RoomModule room) =>
        room == null
            ? GetBreakdown(RoomType.Empty, Array.Empty<FurnitureItem>())
            : GetBreakdown(room.Type, room.Furniture);

    /// <summary>
    /// Full component breakdown. Every component is clamped 0–100 before
    /// compositing, and the composite is clamped again after.
    /// </summary>
    public static AppointmentBreakdown GetBreakdown(RoomType type, IReadOnlyList<FurnitureItem> items)
    {
        var placed = (items ?? Array.Empty<FurnitureItem>())
            .Where(i => i != null)
            .ToList();

        var breakdown = new AppointmentBreakdown
        {
            ItemCount = placed.Count,
            MissingCategories = new List<FurnitureCategory>(),
            DominantStyle = FurnitureStyle.Modern
        };

        if (placed.Count == 0)
        {
            breakdown.Coverage = 0f;
            breakdown.Quality = 0f;
            breakdown.Coherence = 0f;
            breakdown.MeanCondition = 0f;
            breakdown.ConditionMultiplier = MinConditionMultiplier;
            breakdown.MissingCategories.AddRange(GetRequiredCategories(type));
            breakdown.MissingEssential = GetEssentialCategory(type).HasValue;
            breakdown.Appointment = 0f;
            return breakdown;
        }

        breakdown.Coverage = ComputeCoverage(type, placed, breakdown.MissingCategories,
            out bool missingEssential);
        breakdown.MissingEssential = missingEssential;

        breakdown.Quality = ComputeQuality(type, placed);

        breakdown.Coherence = ComputeCoherence(placed,
            out var dominant, out float share, out int distinct);
        breakdown.DominantStyle = dominant;
        breakdown.DominantStyleShare = share;
        breakdown.DistinctStyleCount = distinct;

        breakdown.MeanCondition = Mathf.Clamp(placed.Average(i => i.Condition), 0f, 100f);
        breakdown.ConditionMultiplier = ComputeConditionMultiplier(breakdown.MeanCondition);

        float composite = breakdown.Coverage * CoverageWeight
                        + breakdown.Quality * QualityWeight
                        + breakdown.Coherence * CoherenceWeight;

        breakdown.Appointment = Mathf.Clamp(composite * breakdown.ConditionMultiplier, 0f, 100f);
        return breakdown;
    }

    // ── Coverage ───────────────────────────────────────────────────────

    /// <summary>
    /// Rewards having the right <i>kinds</i> of furniture. Extra pieces past
    /// the required set saturate, so twelve rugs never substitute for a bed.
    /// </summary>
    private static float ComputeCoverage(
        RoomType type,
        List<FurnitureItem> items,
        List<FurnitureCategory> missingOut,
        out bool missingEssential)
    {
        var present = new HashSet<FurnitureCategory>(items.Select(i => i.Category));
        var required = GetRequiredCategories(type);

        var essential = GetEssentialCategory(type);
        missingEssential = essential.HasValue && !present.Contains(essential.Value);

        foreach (var cat in required)
            if (!present.Contains(cat))
                missingOut.Add(cat);

        float coverage;

        if (required.Count == 0)
        {
            // No specific needs (Storage, Empty): any furnishing helps, saturating.
            coverage = 100f * (1f - Mathf.Exp(-items.Count * 0.6f));
        }
        else
        {
            int hits = required.Count - missingOut.Count;
            float fraction = Mathf.Clamp(hits / (float)required.Count, 0f, 1f);

            int extras = Mathf.Max(0, items.Count - required.Count);
            float extraCredit = ExtraItemWeight *
                                (1f - 1f / (1f + extras * ExtraItemFalloff));

            // Extras only count once the basics are in place — otherwise a
            // bedless suite could buy its way to a passing Coverage.
            coverage = fraction * RequiredCategoryWeight + extraCredit * fraction;
        }

        if (missingEssential)
            coverage *= MissingEssentialPenalty;

        return Mathf.Clamp(coverage, 0f, 100f);
    }

    // ── Quality ────────────────────────────────────────────────────────

    /// <summary>
    /// Area-weighted mean of Comfort and Prestige. Larger pieces dominate
    /// the impression of a room; the Comfort/Prestige split shifts by room
    /// type. Condition is deliberately excluded here — it is applied once,
    /// as the final multiplier, so wear is never double-counted.
    /// </summary>
    private static float ComputeQuality(RoomType type, List<FurnitureItem> items)
    {
        float comfortBias = ComfortBias.TryGetValue(type, out float bias) ? bias : 0.5f;
        float prestigeBias = 1f - comfortBias;

        float weighted = 0f;
        float totalWeight = 0f;

        foreach (var item in items)
        {
            float weight = item.Area;
            float score = item.Comfort * comfortBias + item.Prestige * prestigeBias;
            weighted += score * weight;
            totalWeight += weight;
        }

        if (Mathf.IsEqualApprox(totalWeight, 0f)) return 0f;
        return Mathf.Clamp(weighted / totalWeight, 0f, 100f);
    }

    // ── Coherence ──────────────────────────────────────────────────────

    /// <summary>
    /// The heart of the system. Crossing <see cref="CoherenceThreshold"/>
    /// share in one style is a cliff, not a ramp — that discontinuity is
    /// what makes a coordinated cheap room a real strategy rather than a
    /// consolation prize.
    /// </summary>
    private static float ComputeCoherence(
        List<FurnitureItem> items,
        out FurnitureStyle dominant,
        out float effectiveShare,
        out int distinctCount)
    {
        var byStyle = items
            .GroupBy(i => i.StyleTag)
            .ToDictionary(g => g.Key, g => g.Count());

        distinctCount = byStyle.Count;

        dominant = byStyle.OrderByDescending(kvp => kvp.Value).First().Key;
        int dominantCount = byStyle[dominant];
        int total = items.Count;

        float pureShare = dominantCount / (float)total;

        // Styles that merely harmonise with the dominant one count at half rate.
        int compatibleCount = 0;
        if (CompatibleStyles.TryGetValue(dominant, out var friends))
        {
            foreach (var friend in friends)
                if (byStyle.TryGetValue(friend, out int c))
                    compatibleCount += c;
        }

        float compatShare = (dominantCount + compatibleCount) / (float)total;
        effectiveShare = Mathf.Clamp(
            pureShare + (compatShare - pureShare) * CompatibleStyleCredit, 0f, 1f);

        float coherence;
        if (effectiveShare >= CoherenceThreshold)
        {
            float t = (effectiveShare - CoherenceThreshold) / (1f - CoherenceThreshold);
            coherence = Mathf.Lerp(CoherentFloor, 100f, Mathf.Clamp(t, 0f, 1f));
        }
        else
        {
            // Below the threshold the room reads as an accident.
            float t = (effectiveShare - 0.2f) / (CoherenceThreshold - 0.2f);
            coherence = Mathf.Lerp(IncoherentFloor, IncoherentCeiling, Mathf.Clamp(t, 0f, 1f));
        }

        coherence -= PerExtraStylePenalty * Mathf.Max(0, distinctCount - 2);

        return Mathf.Clamp(coherence, 0f, 100f);
    }

    // ── Condition ──────────────────────────────────────────────────────

    /// <summary>Mean wear scaled into a 0.45–1.0 multiplier.</summary>
    private static float ComputeConditionMultiplier(float meanCondition)
    {
        float t = Mathf.Clamp(meanCondition / 100f, 0f, 1f);
        return Mathf.Lerp(MinConditionMultiplier, 1f, t);
    }
}
