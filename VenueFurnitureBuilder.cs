using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Everything the venue knows about what a piece of furniture <i>looks</i> like.
///
/// Split out of <see cref="VenueRoomBuilder"/> because the two answer different
/// questions: the room builder owns the shell (slab, back walls, trim), and this
/// owns the contents. Both draw exclusively through
/// <see cref="VenueRoomBuilder.MakeBox"/> so every mesh carries its own
/// alpha-capable <see cref="StandardMaterial3D"/> and the floor cutaway can fade
/// it without rebuilding the scene.
///
/// Two sources of geometry, in order of preference:
///
///   1. A real .glb resolved through <see cref="FurnitureModelRegistry"/>,
///      measured and scaled to the category's target height.
///   2. A procedural silhouette built from a handful of boxes.
///
/// The procedural path is not a stopgap — most style/category combinations will
/// never get a bespoke model — so each category is composed to be legible from
/// the fixed isometric angle at roughly 2 m per tile. No category is a single
/// box; silhouette is the only thing that survives at that size.
/// </summary>
public static class VenueFurnitureBuilder
{
    /// <summary>
    /// Parsed models, kept so a house with thirty lamps parses one 37 MB glb
    /// rather than thirty. Instances are <see cref="Node.Duplicate"/>s of these.
    /// </summary>
    private static readonly Dictionary<string, Node3D> _modelCache = new();

    /// <summary>Paths that failed to load, so we do not retry them every frame.</summary>
    private static readonly HashSet<string> _modelFailures = new();

    // ── Layout ─────────────────────────────────────────────────────────

    /// <summary>
    /// Place every piece of a room's furniture into <paramref name="root"/>.
    /// Tolerates a room with no furniture list and null entries inside it.
    /// </summary>
    public static void Build(RoomModule room, Vector3I origin, Vector2I size, Node3D root)
    {
        if (room == null || root == null) return;

        var pieces = room.Furniture;
        if (pieces == null || pieces.Count == 0) return;

        // Several pieces of one category fan out from that category's slot,
        // so three lamps read as three lamps rather than one thick one.
        var seen = new Dictionary<FurnitureCategory, int>();

        foreach (var item in pieces)
        {
            if (item == null) continue;

            int index = seen.TryGetValue(item.Category, out int n) ? n : 0;
            seen[item.Category] = index + 1;

            var slot = GetSlot(item.Category);

            // Wall-mounted and wall-backed pieces only ever slide *along* the
            // wall; nudging them inward would leave a picture floating in air.
            bool pinnedToWall = item.Category == FurnitureCategory.Decor
                             || item.Category == FurnitureCategory.Bar;

            float u = Mathf.Clamp(slot.X + index * 0.17f, 0.12f, 0.88f);
            float v = pinnedToWall
                ? slot.Y
                : Mathf.Clamp(slot.Y + index * 0.11f, 0.12f, 0.88f);

            var at = VenueSpace.RoomPoint(origin, size, u, v);

            var piece = BuildPiece(item, at);
            if (piece == null) continue;

            piece.Name = SafeName($"{item.Category}_{index}");
            root.AddChild(piece);
        }
    }

    /// <summary>Normalised slot inside the footprint for each category.</summary>
    private static Vector2 GetSlot(FurnitureCategory category) => category switch
    {
        FurnitureCategory.Rug => new Vector2(0.50f, 0.55f),
        FurnitureCategory.Decor => new Vector2(0.50f, 0.11f),
        FurnitureCategory.Bed => new Vector2(0.34f, 0.34f),
        FurnitureCategory.Bar => new Vector2(0.50f, 0.24f),
        FurnitureCategory.Vanity => new Vector2(0.78f, 0.28f),
        FurnitureCategory.Screen => new Vector2(0.20f, 0.72f),
        FurnitureCategory.Bath => new Vector2(0.76f, 0.74f),
        FurnitureCategory.Seating => new Vector2(0.64f, 0.60f),
        FurnitureCategory.Lighting => new Vector2(0.16f, 0.18f),
        _ => new Vector2(0.50f, 0.50f)
    };

    private static Node3D BuildPiece(FurnitureItem item, Vector3 at)
    {
        var tint = BaseColour(item);

        var node = TryInstanceModel(item, at, tint) ?? BuildProcedural(item, at, tint);

        // Lighting always carries its own warm bulb, model or not — that glow
        // is what makes the cutaway read as an interior rather than a diorama.
        if (item.Category == FurnitureCategory.Lighting)
            node.AddChild(MakeLampGlow(at, item.IsDilapidated));

        return node;
    }

    private static Node3D BuildProcedural(FurnitureItem item, Vector3 at, Color tint)
    {
        var node = new Node3D { Position = Vector3.Zero };

        switch (item.Category)
        {
            case FurnitureCategory.Bed: BuildBed(node, at, tint, item); break;
            case FurnitureCategory.Seating: BuildSeating(node, at, tint, item); break;
            case FurnitureCategory.Lighting: BuildLamp(node, at, tint, item); break;
            case FurnitureCategory.Rug: BuildRug(node, at, tint, item); break;
            case FurnitureCategory.Decor: BuildDecor(node, at, tint, item); break;
            case FurnitureCategory.Vanity: BuildVanity(node, at, tint, item); break;
            case FurnitureCategory.Screen: BuildScreen(node, at, tint, item); break;
            case FurnitureCategory.Bath: BuildBath(node, at, tint, item); break;
            case FurnitureCategory.Bar: BuildBar(node, at, tint, item); break;
            default: BuildCrate(node, at, tint); break;
        }

        return node;
    }

    // ── Finish ─────────────────────────────────────────────────────────

    /// <summary>
    /// The piece's own colour. Style sets the hue; tier and decay push it
    /// around it. A ruined baroque chaise and a ruined spartan stool should
    /// look like the same kind of ruined, which is why decay desaturates
    /// toward grey before it darkens.
    /// </summary>
    private static Color BaseColour(FurnitureItem item)
    {
        var colour = IsoTheme.GetStyleColor(item.StyleTag);

        if (item.IsDilapidated)
        {
            float luma = colour.R * 0.299f + colour.G * 0.587f + colour.B * 0.114f;
            colour = colour.Lerp(new Color(luma, luma, luma, colour.A), 0.72f);
            return colour.Lerp(IsoTheme.Backdrop, 0.28f);
        }

        // Tier 4–5 warms the whole piece a touch before the gold trim goes on,
        // so the accent reads as part of the object rather than stuck to it.
        return item.Tier >= 4 ? colour.Lerp(IsoTheme.Gold, 0.12f) : colour;
    }

    /// <summary>Whether this piece earns a gold accent. Wrecks never do.</summary>
    private static bool HasTrim(FurnitureItem item) => item.Tier >= 4 && !item.IsDilapidated;

    /// <summary>Tier 5 gets bright gold, tier 4 the dimmer alloy.</summary>
    private static Color TrimColour(FurnitureItem item) =>
        item.Tier >= 5 ? IsoTheme.Gold : IsoTheme.GoldDim;

    /// <summary>Add a gold accent box, but only for pieces that have earned one.</summary>
    private static void AddTrim(Node3D node, FurnitureItem item, Vector3 centre, Vector3 size)
    {
        if (!HasTrim(item)) return;
        node.AddChild(VenueRoomBuilder.MakeBox(centre, size, TrimColour(item)));
    }

    // ── Procedural silhouettes ─────────────────────────────────────────

    /// <summary>
    /// Bed — frame, mattress, a blanket covering the foot end, a pillow, and a
    /// headboard standing well above everything at the back (−Z). Roughly
    /// 1.4 × 2.0 m; the headboard is what identifies it at a glance.
    /// </summary>
    private static void BuildBed(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.13f, 0f),
            new Vector3(1.40f, 0.26f, 2.00f), tint.Darkened(0.42f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.35f, 0f),
            new Vector3(1.30f, 0.18f, 1.86f), tint.Lightened(0.14f)));

        // Blanket over the foot two-thirds, deliberately a different value from
        // the mattress so the bed does not read as one flat slab.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.47f, 0.34f),
            new Vector3(1.32f, 0.07f, 1.16f), tint.Darkened(0.26f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.48f, -0.72f),
            new Vector3(0.86f, 0.13f, 0.34f), tint.Lightened(0.52f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.62f, -1.02f),
            new Vector3(1.44f, 1.05f, 0.12f), tint.Darkened(0.34f)));

        AddTrim(node, item, at + new Vector3(0f, 1.13f, -1.02f), new Vector3(1.46f, 0.06f, 0.15f));
    }

    /// <summary>
    /// Seating — cushion, backrest, two arms and four legs. The gap under the
    /// seat is the tell that separates a chair from a crate from above.
    /// </summary>
    private static void BuildSeating(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        var leg = tint.Darkened(0.48f);

        foreach (var (x, z) in new[] { (-0.36f, -0.27f), (0.36f, -0.27f), (-0.36f, 0.27f), (0.36f, 0.27f) })
        {
            node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(x, 0.17f, z),
                new Vector3(0.09f, 0.34f, 0.09f), leg));
        }

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.42f, 0f),
            new Vector3(0.88f, 0.17f, 0.70f), tint.Lightened(0.12f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.60f, -0.29f),
            new Vector3(0.88f, 0.52f, 0.13f), tint.Darkened(0.24f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(-0.43f, 0.48f, 0f),
            new Vector3(0.12f, 0.28f, 0.70f), tint.Darkened(0.14f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0.43f, 0.48f, 0f),
            new Vector3(0.12f, 0.28f, 0.70f), tint.Darkened(0.14f)));

        AddTrim(node, item, at + new Vector3(0f, 0.88f, -0.29f), new Vector3(0.90f, 0.05f, 0.15f));
    }

    /// <summary>
    /// Lighting — weighted base, thin stem, glowing shade, plus the omni light
    /// that actually lifts the room. Kept narrow so the stem stays visible.
    /// </summary>
    private static void BuildLamp(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.03f, 0f),
            new Vector3(0.26f, 0.06f, 0.26f), tint.Darkened(0.45f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.33f, 0f),
            new Vector3(0.05f, 0.54f, 0.05f), tint.Darkened(0.28f)));

        var shadeColour = item.IsDilapidated
            ? IsoTheme.LampWarm.Lerp(IsoTheme.Backdrop, 0.45f)
            : IsoTheme.LampWarm;

        var shade = VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.72f, 0f),
            new Vector3(0.32f, 0.26f, 0.32f), shadeColour);

        if (shade.MaterialOverride is StandardMaterial3D mat)
        {
            mat.EmissionEnabled = true;
            mat.Emission = IsoTheme.LampWarm;
            mat.EmissionEnergyMultiplier = item.IsDilapidated ? 0.5f : 1.4f;
        }

        node.AddChild(shade);

        AddTrim(node, item, at + new Vector3(0f, 0.585f, 0f), new Vector3(0.14f, 0.05f, 0.14f));
    }

    /// <summary>
    /// Rug — two very thin plates, the upper one inset and lighter, so the
    /// silhouette reads as a bordered rug rather than a spill on the boards.
    /// </summary>
    private static void BuildRug(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.014f, 0f),
            new Vector3(1.70f, 0.028f, 1.24f), tint.Darkened(0.30f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.032f, 0f),
            new Vector3(1.32f, 0.026f, 0.90f), tint.Lightened(0.34f)));

        // A third, tighter medallion is the only place a rug can show tier.
        AddTrim(node, item, at + new Vector3(0f, 0.046f, 0f), new Vector3(0.60f, 0.024f, 0.40f));
    }

    /// <summary>
    /// Decor — a framed panel hung at wall height against the −Z back wall,
    /// standing slightly proud of its gold border. Never sits on the floor.
    /// </summary>
    private static void BuildDecor(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        float y = VenueSpace.WallHeight * 0.62f;
        var frame = HasTrim(item) ? IsoTheme.Gold : IsoTheme.GoldDim;

        if (item.IsDilapidated) frame = frame.Lerp(IsoTheme.Backdrop, 0.45f);

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, y, -0.05f),
            new Vector3(0.84f, 0.66f, 0.04f), frame));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, y, 0f),
            new Vector3(0.68f, 0.50f, 0.05f), tint));

        // A slim highlight across the top of the plate keeps it from reading
        // as a flat rectangle under the fixed light.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, y + 0.19f, 0.012f),
            new Vector3(0.66f, 0.10f, 0.03f), tint.Lightened(0.30f)));
    }

    /// <summary>
    /// Vanity — a waist-high cabinet with drawer lines and a lighter top,
    /// plus a small upright mirror standing at the back of the surface.
    /// </summary>
    private static void BuildVanity(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.36f, 0f),
            new Vector3(1.00f, 0.72f, 0.48f), tint));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.76f, 0f),
            new Vector3(1.10f, 0.07f, 0.56f), tint.Lightened(0.30f)));

        // Drawer seams, on the viewer-facing (+Z) side.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.28f, 0.25f),
            new Vector3(0.86f, 0.04f, 0.03f), tint.Darkened(0.45f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.54f, 0.25f),
            new Vector3(0.86f, 0.04f, 0.03f), tint.Darkened(0.45f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 1.16f, -0.20f),
            new Vector3(0.54f, 0.74f, 0.05f), tint.Darkened(0.32f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 1.16f, -0.16f),
            new Vector3(0.42f, 0.60f, 0.03f), tint.Lightened(0.58f)));

        AddTrim(node, item, at + new Vector3(0f, 1.55f, -0.20f), new Vector3(0.56f, 0.05f, 0.07f));
    }

    /// <summary>
    /// Screen — three tall, thin panels angled into a zigzag, so the piece
    /// still has depth when viewed head-on from the isometric camera.
    /// </summary>
    private static void BuildScreen(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        for (int i = 0; i < 3; i++)
        {
            bool middle = i == 1;

            var panel = VenueRoomBuilder.MakeBox(
                at + new Vector3((i - 1) * 0.42f, 0.82f, middle ? -0.11f : 0.11f),
                new Vector3(0.46f, 1.62f, 0.05f),
                middle ? tint : tint.Darkened(0.26f));

            panel.RotateY(Mathf.DegToRad(middle ? -30f : 30f));
            node.AddChild(panel);

            if (!HasTrim(item)) continue;

            var cap = VenueRoomBuilder.MakeBox(
                at + new Vector3((i - 1) * 0.42f, 1.66f, middle ? -0.11f : 0.11f),
                new Vector3(0.48f, 0.06f, 0.07f),
                TrimColour(item));

            cap.RotateY(Mathf.DegToRad(middle ? -30f : 30f));
            node.AddChild(cap);
        }
    }

    /// <summary>
    /// Bath — a rimmed tub shell on four feet with a visibly recessed, darker
    /// interior. The recess is the whole point: a solid box is indistinguishable
    /// from a bed at this scale.
    /// </summary>
    private static void BuildBath(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        var shell = tint.Darkened(0.16f);
        var foot = tint.Darkened(0.50f);

        foreach (var (x, z) in new[] { (-0.55f, -0.30f), (0.55f, -0.30f), (-0.55f, 0.30f), (0.55f, 0.30f) })
        {
            node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(x, 0.05f, z),
                new Vector3(0.13f, 0.10f, 0.13f), foot));
        }

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.22f, 0f),
            new Vector3(1.35f, 0.24f, 0.80f), shell));

        // Rim walls, leaving the interior open from above.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(-0.615f, 0.52f, 0f),
            new Vector3(0.12f, 0.36f, 0.80f), shell));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0.615f, 0.52f, 0f),
            new Vector3(0.12f, 0.36f, 0.80f), shell));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.52f, -0.34f),
            new Vector3(1.11f, 0.36f, 0.12f), shell));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.52f, 0.34f),
            new Vector3(1.11f, 0.36f, 0.12f), shell));

        // Sunk interior — 24 cm below the rim, and much darker.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.40f, 0f),
            new Vector3(1.11f, 0.12f, 0.56f), tint.Darkened(0.66f)));

        AddTrim(node, item, at + new Vector3(0f, 0.71f, 0f), new Vector3(1.39f, 0.04f, 0.84f));
    }

    /// <summary>
    /// Bar — a long counter with a lighter top, a footrail on the viewer side
    /// and a shelved back unit behind it. Length is what tells it from a vanity.
    /// </summary>
    private static void BuildBar(Node3D node, Vector3 at, Color tint, FurnitureItem item)
    {
        const float length = 1.80f;

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.50f, 0f),
            new Vector3(length, 1.00f, 0.58f), tint));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 1.04f, 0f),
            new Vector3(length + 0.14f, 0.08f, 0.72f), tint.Lightened(0.34f)));

        // Footrail, on the open (+Z) side where a customer would stand.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.18f, 0.36f),
            new Vector3(length, 0.06f, 0.06f), IsoTheme.GoldDim));

        // Back unit against the wall behind the counter.
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.85f, -0.56f),
            new Vector3(length, 1.70f, 0.10f), tint.Darkened(0.44f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.80f, -0.44f),
            new Vector3(length - 0.10f, 0.06f, 0.24f), tint.Lightened(0.18f)));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 1.28f, -0.44f),
            new Vector3(length - 0.10f, 0.06f, 0.24f), tint.Lightened(0.18f)));

        AddTrim(node, item, at + new Vector3(0f, 0.94f, 0.30f), new Vector3(length + 0.02f, 0.05f, 0.03f));
    }

    /// <summary>Last-resort shape for a category with no builder. Still not one box.</summary>
    private static void BuildCrate(Node3D node, Vector3 at, Color tint)
    {
        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.28f, 0f),
            new Vector3(0.62f, 0.56f, 0.62f), tint));

        node.AddChild(VenueRoomBuilder.MakeBox(at + new Vector3(0f, 0.58f, 0f),
            new Vector3(0.70f, 0.05f, 0.70f), tint.Lightened(0.25f)));
    }

    private static OmniLight3D MakeLampGlow(Vector3 at, bool dim) => new()
    {
        Name = "Glow",
        Position = at + new Vector3(0f, 0.78f, 0f),
        LightColor = IsoTheme.LampWarm,
        LightEnergy = dim ? 0.55f : 1.2f,
        OmniRange = 3.0f,
        ShadowEnabled = false
    };

    // ── glb models ─────────────────────────────────────────────────────

    /// <summary>Drop the parsed-model cache. Call on scene teardown.</summary>
    public static void ClearModelCache()
    {
        foreach (var model in _modelCache.Values)
        {
            if (model != null && GodotObject.IsInstanceValid(model)) model.QueueFree();
        }

        _modelCache.Clear();
        _modelFailures.Clear();
    }

    /// <summary>
    /// Instance the best real model for a piece, scaled to a sensible size for
    /// a 2 m tile. Returns null when the registry has nothing for the category
    /// or the file could not be parsed, in which case the caller falls back to
    /// the procedural silhouette.
    /// </summary>
    private static Node3D TryInstanceModel(FurnitureItem item, Vector3 at, Color tint)
    {
        FurnitureModelRegistry.ModelEntry entry;

        try
        {
            // The item name is a stable seed, so a given piece keeps the same
            // model for the whole run instead of reshuffling on every rebuild.
            entry = FurnitureModelRegistry.Resolve(item.Category, item.StyleTag, item.ItemName);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VenueFurniture] Model registry failed for {item.Category}: {ex.Message}");
            return null;
        }

        if (entry == null || string.IsNullOrEmpty(entry.Path)) return null;

        var source = LoadModel(entry.Path);
        if (source == null) return null;

        if (source.Duplicate() is not Node3D instance) return null;

        float target = FurnitureModelRegistry.GetTargetHeight(item.Category);
        float scale = FitScale(instance, target);

        instance.Scale = new Vector3(scale, scale, scale);
        instance.Position = item.Category == FurnitureCategory.Decor
            ? at + new Vector3(0f, VenueSpace.WallHeight * 0.45f, 0f)
            : at;

        // A model exported without textures renders flat white and loses all
        // style identity — five different beds become the same pale block.
        // Tint only those; a textured model keeps its own materials, so this
        // stops applying itself the moment the art is re-exported.
        if (!HasAnyTexture(instance)) TintModel(instance, tint);

        // Dilapidated pieces are washed out even when they are real geometry,
        // so decay reads the same way across model and procedural furniture.
        if (item.IsDilapidated) TintModel(instance, IsoTheme.Backdrop.Lerp(tint, 0.35f));

        var wrapper = new Node3D();
        wrapper.AddChild(instance);
        return wrapper;
    }

    /// <summary>
    /// Whether any mesh in a loaded model carries an albedo texture.
    ///
    /// Used to decide whether a model needs a style tint standing in for its
    /// missing material. Checks the surface materials rather than any
    /// override, because that is where a glb's own textures land.
    /// </summary>
    private static bool HasAnyTexture(Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } mesh)
        {
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var material = mesh.GetActiveMaterial(surface);
                if (material is StandardMaterial3D { AlbedoTexture: not null }) return true;
            }
        }

        foreach (var child in node.GetChildren())
            if (HasAnyTexture(child)) return true;

        return false;
    }

    /// <summary>
    /// Parse a glb off disk. These files ship without usable import sidecars,
    /// so <c>GD.Load</c> is not an option — the buffer has to go through
    /// <see cref="GltfDocument"/> by hand.
    /// </summary>
    private static Node3D LoadModel(string path)
    {
        if (_modelCache.TryGetValue(path, out var cached))
            return GodotObject.IsInstanceValid(cached) ? cached : null;

        if (_modelFailures.Contains(path)) return null;

        try
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                GD.PrintErr($"[VenueFurniture] Cannot open '{path}'.");
                _modelFailures.Add(path);
                return null;
            }

            var bytes = file.GetBuffer((long)file.GetLength());
            var basePath = path.GetBaseDir() + "/";

            var document = new GltfDocument();
            var state = new GltfState { BasePath = basePath };

            if (document.AppendFromBuffer(bytes, basePath, state) != Error.Ok)
            {
                GD.PrintErr($"[VenueFurniture] glTF parse failed for '{path}'.");
                _modelFailures.Add(path);
                return null;
            }

            if (document.GenerateScene(state) is not Node3D scene)
            {
                GD.PrintErr($"[VenueFurniture] glTF produced no Node3D for '{path}'.");
                _modelFailures.Add(path);
                return null;
            }

            _modelCache[path] = scene;
            GD.Print($"[VenueFurniture] Loaded model '{path}'.");
            return scene;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VenueFurniture] Model load error for '{path}': {ex.Message}");
            _modelFailures.Add(path);
            return null;
        }
    }

    /// <summary>
    /// Uniform scale that brings a model's own bounding box to
    /// <paramref name="targetHeight"/> metres. Meshy output has no consistent
    /// export scale, so measuring is the only way to get a lamp that is lamp
    /// sized next to a hand-authored 2 m tile.
    /// </summary>
    private static float FitScale(Node3D model, float targetHeight)
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;

        // Measured ignoring the root's own transform, because the caller
        // overwrites that scale with the result.
        foreach (var child in model.GetChildren())
            CollectExtent(child, Transform3D.Identity, ref min, ref max, ref any);

        if (model is MeshInstance3D rootMesh && rootMesh.Mesh != null)
            MergeAabb(Transform3D.Identity, rootMesh.Mesh.GetAabb(), ref min, ref max, ref any);

        if (!any) return 1f;

        float height = Mathf.Max(max.Y - min.Y, 0.0001f);
        return Mathf.Clamp(targetHeight / height, 0.0001f, 10000f);
    }

    private static void CollectExtent(
        Node node, Transform3D accumulated, ref Vector3 min, ref Vector3 max, ref bool any)
    {
        var local = node is Node3D spatial ? accumulated * spatial.Transform : accumulated;

        if (node is MeshInstance3D mesh && mesh.Mesh != null)
            MergeAabb(local, mesh.Mesh.GetAabb(), ref min, ref max, ref any);

        foreach (var child in node.GetChildren())
            CollectExtent(child, local, ref min, ref max, ref any);
    }

    /// <summary>Fold a mesh's local AABB, transformed corner by corner, into a running extent.</summary>
    private static void MergeAabb(
        Transform3D transform, Aabb box, ref Vector3 min, ref Vector3 max, ref bool any)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            var point = transform * (box.Position + new Vector3(
                (corner & 1) == 0 ? 0f : box.Size.X,
                (corner & 2) == 0 ? 0f : box.Size.Y,
                (corner & 4) == 0 ? 0f : box.Size.Z));

            if (!any)
            {
                min = point;
                max = point;
                any = true;
                continue;
            }

            min = new Vector3(Mathf.Min(min.X, point.X), Mathf.Min(min.Y, point.Y), Mathf.Min(min.Z, point.Z));
            max = new Vector3(Mathf.Max(max.X, point.X), Mathf.Max(max.Y, point.Y), Mathf.Max(max.Z, point.Z));
        }
    }

    private static void TintModel(Node node, Color tint)
    {
        if (node is GeometryInstance3D geom)
        {
            geom.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = tint,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha
            };
        }

        foreach (var child in node.GetChildren())
            TintModel(child, tint);
    }

    private static string SafeName(string raw) => raw.Replace('.', '_').Replace(':', '_');
}
