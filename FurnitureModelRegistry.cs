using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Finds the furniture models on disk so that adding art needs no code.
///
/// Drop a .glb anywhere under <c>res://Assets/Furniture/</c> and it is picked
/// up on the next run. The folder it sits in decides which
/// <see cref="FurnitureCategory"/> it serves; the filename can optionally
/// name a style and it will then be preferred for pieces of that style.
///
///   Assets/Furniture/Beds/baroque_four_poster.glb   → Bed, Baroque
///   Assets/Furniture/Lamps/brass_table_lamp.glb     → Lighting, any style
///   Assets/Furniture/Seating/deco_armchair.glb      → Seating, ArtDeco
///
/// Folder names are matched loosely, so Lamps / Lighting / Lights all work
/// and a plural is never wrong. Anything unrecognised is reported once at
/// startup rather than silently ignored — a model that does not show up in
/// game should be traceable to a name, not a mystery.
/// </summary>
public static class FurnitureModelRegistry
{
    /// <summary>One discovered model file.</summary>
    public class ModelEntry
    {
        public string Path { get; init; }
        public FurnitureCategory Category { get; init; }

        /// <summary>Style this model suits, or null if it fits any.</summary>
        public FurnitureStyle? Style { get; init; }

        public override string ToString() =>
            $"{Category}{(Style.HasValue ? $"/{Style}" : "")}: {Path.GetFile()}";
    }

    public const string RootDirectory = "res://Assets/Furniture/";

    /// <summary>
    /// Folder-name aliases. Meshy output and human folders rarely agree on
    /// singular/plural or on what a category is called, so several names map
    /// onto each category.
    /// </summary>
    private static readonly Dictionary<string, FurnitureCategory> FolderAliases = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["bed"] = FurnitureCategory.Bed,
        ["beds"] = FurnitureCategory.Bed,

        ["seating"] = FurnitureCategory.Seating,
        ["seats"] = FurnitureCategory.Seating,
        ["chairs"] = FurnitureCategory.Seating,
        ["sofas"] = FurnitureCategory.Seating,
        ["couches"] = FurnitureCategory.Seating,

        ["lamps"] = FurnitureCategory.Lighting,
        ["lamp"] = FurnitureCategory.Lighting,
        ["lighting"] = FurnitureCategory.Lighting,
        ["lights"] = FurnitureCategory.Lighting,
        ["chandeliers"] = FurnitureCategory.Lighting,

        ["rugs"] = FurnitureCategory.Rug,
        ["rug"] = FurnitureCategory.Rug,
        ["carpets"] = FurnitureCategory.Rug,

        ["decor"] = FurnitureCategory.Decor,
        ["decoration"] = FurnitureCategory.Decor,
        ["walldecoration"] = FurnitureCategory.Decor,
        ["mirrors"] = FurnitureCategory.Decor,
        ["art"] = FurnitureCategory.Decor,
        ["paintings"] = FurnitureCategory.Decor,

        ["vanity"] = FurnitureCategory.Vanity,
        ["vanities"] = FurnitureCategory.Vanity,
        ["tables"] = FurnitureCategory.Vanity,
        ["table"] = FurnitureCategory.Vanity,
        ["desks"] = FurnitureCategory.Vanity,
        ["dressers"] = FurnitureCategory.Vanity,

        ["screens"] = FurnitureCategory.Screen,
        ["screen"] = FurnitureCategory.Screen,
        ["dividers"] = FurnitureCategory.Screen,

        ["baths"] = FurnitureCategory.Bath,
        ["bath"] = FurnitureCategory.Bath,
        ["tubs"] = FurnitureCategory.Bath,

        ["bar"] = FurnitureCategory.Bar,
        ["bars"] = FurnitureCategory.Bar,
        ["counters"] = FurnitureCategory.Bar,
        ["cabinets"] = FurnitureCategory.Bar
    };

    /// <summary>
    /// Real-world height in metres a model of each category is scaled to.
    /// Meshy exports have no consistent unit scale, so every model is
    /// measured and normalised rather than trusted.
    /// </summary>
    public static readonly Dictionary<FurnitureCategory, float> TargetHeights = new()
    {
        [FurnitureCategory.Bed] = 0.62f,
        [FurnitureCategory.Seating] = 0.85f,
        [FurnitureCategory.Lighting] = 0.55f,
        [FurnitureCategory.Rug] = 0.03f,
        [FurnitureCategory.Decor] = 1.10f,
        [FurnitureCategory.Vanity] = 0.78f,
        [FurnitureCategory.Screen] = 1.65f,
        [FurnitureCategory.Bath] = 0.65f,
        [FurnitureCategory.Bar] = 1.10f
    };

    private static List<ModelEntry> _entries;

    /// <summary>Every model found, scanning once per run.</summary>
    public static IReadOnlyList<ModelEntry> Entries
    {
        get
        {
            _entries ??= Scan();
            return _entries;
        }
    }

    /// <summary>Force a rescan. Useful after dropping in art without restarting.</summary>
    public static void Rescan() => _entries = Scan();

    /// <summary>
    /// The best model for a piece, or null to fall back to procedural
    /// geometry. Prefers an exact style match, then any model of the right
    /// category, chosen deterministically so a given piece always looks the
    /// same.
    /// </summary>
    public static ModelEntry Resolve(FurnitureCategory category, FurnitureStyle style, string seed = null)
    {
        var candidates = Entries.Where(e => e.Category == category).ToList();
        if (candidates.Count == 0) return null;

        var styled = candidates.Where(e => e.Style == style).ToList();
        var pool = styled.Count > 0 ? styled : candidates;

        if (pool.Count == 1) return pool[0];

        var hash = 0;
        foreach (var c in seed ?? category.ToString()) hash = (hash * 31 + c) & 0x7FFFFFFF;

        return pool[hash % pool.Count];
    }

    /// <summary>Target height for a category, defaulting to knee-height.</summary>
    public static float GetTargetHeight(FurnitureCategory category) =>
        TargetHeights.TryGetValue(category, out var height) ? height : 0.7f;

    // ── Scanning ───────────────────────────────────────────────────────

    private static List<ModelEntry> Scan()
    {
        var found = new List<ModelEntry>();
        var unknownFolders = new HashSet<string>();

        ScanDirectory(RootDirectory, found, unknownFolders);

        foreach (var folder in unknownFolders)
        {
            GD.Print($"[FurnitureModels] Ignoring '{folder}' — folder name matches no category. " +
                     $"Rename it to one of: {string.Join(", ", FolderAliases.Keys.Take(9))}…");
        }

        if (found.Count == 0)
        {
            GD.Print($"[FurnitureModels] No .glb found under {RootDirectory}. " +
                     $"Everything will use procedural shapes.");
        }
        else
        {
            var byCategory = found.GroupBy(e => e.Category)
                .Select(g => $"{g.Key} ×{g.Count()}")
                .OrderBy(s => s);

            GD.Print($"[FurnitureModels] {found.Count} models: {string.Join(", ", byCategory)}");
        }

        return found;
    }

    private static void ScanDirectory(
        string path, List<ModelEntry> found, HashSet<string> unknownFolders)
    {
        using var dir = DirAccess.Open(path);
        if (dir == null) return;

        dir.ListDirBegin();

        string name;
        while ((name = dir.GetNext()) != "")
        {
            if (name.StartsWith(".")) continue;

            var full = path.TrimSuffix("/") + "/" + name;

            if (dir.CurrentIsDir())
            {
                ScanDirectory(full, found, unknownFolders);
                continue;
            }

            // Godot exports append .import/.remap to resource names; the raw
            // .glb is what GltfDocument wants.
            if (!name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) continue;

            var folder = path.TrimSuffix("/").GetFile();

            if (!FolderAliases.TryGetValue(folder, out var category))
            {
                unknownFolders.Add(folder);
                continue;
            }

            found.Add(new ModelEntry
            {
                Path = full,
                Category = category,
                Style = InferStyle(name)
            });
        }

        dir.ListDirEnd();
    }

    /// <summary>
    /// Read a style out of a filename, if one is named. Matching is loose so
    /// "deco", "art_deco" and "ArtDeco" all land on the same style.
    /// </summary>
    private static FurnitureStyle? InferStyle(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (lower.Contains("baroque") || lower.Contains("rococo") || lower.Contains("gilded"))
            return FurnitureStyle.Baroque;

        if (lower.Contains("deco")) return FurnitureStyle.ArtDeco;

        if (lower.Contains("oriental") || lower.Contains("lacquer") || lower.Contains("shoji"))
            return FurnitureStyle.Oriental;

        if (lower.Contains("bohemian") || lower.Contains("boho")) return FurnitureStyle.Bohemian;
        if (lower.Contains("modern") || lower.Contains("minimal")) return FurnitureStyle.Modern;
        if (lower.Contains("spartan") || lower.Contains("plain")) return FurnitureStyle.Spartan;

        // Victorian reads closest to Baroque of the six styles available.
        if (lower.Contains("victorian")) return FurnitureStyle.Baroque;

        return null;
    }
}
