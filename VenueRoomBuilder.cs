using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Turns one <see cref="RoomModule"/> into real 3D geometry: a floor slab, the
/// two back walls that make it read as a cutaway, and its furniture.
///
/// Kept separate from <see cref="VenueView3D"/> deliberately — the view is
/// about camera, focus and input, and every piece of "what does a bed look
/// like" knowledge lives here instead. Every position, size and height comes
/// from <see cref="VenueSpace"/>; every colour from <see cref="IsoTheme"/>.
///
/// Which two edges get walls is derived from <see cref="VenueSpace.CameraBasis"/>
/// rather than hardcoded, so the cutaway stays open toward the viewer if the
/// camera yaw is ever retuned. At the shipped yaw of −45° the camera sits at
/// (−X, +Z), which puts the back walls on the <b>+X</b> and <b>−Z</b> edges.
/// </summary>
public static class VenueRoomBuilder
{
    // ── Picking ────────────────────────────────────────────────────────

    /// <summary>
    /// Physics layer the floor slabs (and the view's per-floor pick plane) sit
    /// on. Layer 5 in the editor's 1-based numbering — chosen well clear of the
    /// gameplay layers 1–4 so a click raycast can mask to it exclusively.
    /// </summary>
    public const uint PickCollisionLayer = 1u << 4;

    /// <summary>Metadata key carrying the floor index on a pickable body.</summary>
    public const string FloorMetaKey = "venue_floor";

    /// <summary>Metadata key carrying a room's origin tile on a pickable body.</summary>
    public const string OriginMetaKey = "venue_room_origin";

    // ── Furniture models ───────────────────────────────────────────────

    /// <summary>
    /// Real models, by the category they stand in for. Anything not listed
    /// falls back to a procedural silhouette.
    /// </summary>
    private static readonly Dictionary<FurnitureCategory, string> ModelPaths = new()
    {
        [FurnitureCategory.Lighting] = "res://Assets/Furniture/Lamps/brass_vintage_table_lamp.glb",
        [FurnitureCategory.Decor] = "res://Assets/Furniture/Mirrors/rococo_gold_mirror.glb",
        [FurnitureCategory.Vanity] = "res://Assets/Furniture/Tables/victorian_mahogany_nighttable.glb"
    };

    /// <summary>Real-world height, in metres, each model is normalised to.</summary>
    private static readonly Dictionary<FurnitureCategory, float> ModelHeights = new()
    {
        [FurnitureCategory.Lighting] = 0.55f,
        [FurnitureCategory.Decor] = 1.10f,
        [FurnitureCategory.Vanity] = 0.70f
    };

    /// <summary>
    /// Parsed models, kept so a house with thirty lamps parses one 37 MB glb
    /// rather than thirty. Instances are <see cref="Node.Duplicate"/>s of these.
    /// </summary>
    private static readonly Dictionary<string, Node3D> _modelCache = new();

    /// <summary>Paths that failed to load, so we do not retry them every frame.</summary>
    private static readonly HashSet<string> _modelFailures = new();

    // ── Room ───────────────────────────────────────────────────────────

    /// <summary>
    /// Build the geometry for one room. Returns a <see cref="Node3D"/> whose
    /// children are positioned in absolute world space (the container itself
    /// stays at the origin), so it can be parented anywhere without shifting.
    /// Returns null only for a null room.
    /// </summary>
    public static Node3D BuildRoom(RoomModule room)
    {
        if (room == null) return null;

        var origin = room.GridPosition;
        var size = new Vector2I(Mathf.Max(1, room.Size.X), Mathf.Max(1, room.Size.Y));
        var footprint = VenueSpace.RoomFootprint(size);
        var centre = VenueSpace.RoomCenter(origin, size);
        float floorY = VenueSpace.FloorY(origin.Z);

        var root = new Node3D { Name = SafeName($"Room_{origin.X}_{origin.Y}_F{origin.Z}") };

        var roomColor = IsoTheme.GetRoomColor(room.Type);

        // ── Floor slab ─────────────────────────────────────────────────
        // Hung *below* the walking surface so VenueSpace.FloorY stays the
        // plane furniture and pawns stand on.
        var slab = MakeBox(
            new Vector3(centre.X, floorY - VenueSpace.SlabThickness * 0.5f, centre.Z),
            new Vector3(footprint.X, VenueSpace.SlabThickness, footprint.Y),
            IsoTheme.FloorBoards.Lerp(roomColor, 0.30f));
        slab.Name = "Slab";
        root.AddChild(slab);

        // Collision so the view's click raycast has something to hit.
        var body = new StaticBody3D
        {
            Name = "SlabBody",
            CollisionLayer = PickCollisionLayer,
            CollisionMask = 0
        };
        body.SetMeta(FloorMetaKey, origin.Z);
        body.SetMeta(OriginMetaKey, origin);
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D
            {
                Size = new Vector3(footprint.X, VenueSpace.SlabThickness, footprint.Y)
            }
        });
        slab.AddChild(body);

        // ── Back walls ─────────────────────────────────────────────────
        var (wallAtMaxX, wallAtMaxZ) = BackWallEdges();
        var wallColor = roomColor.Darkened(0.38f);

        float wallY = floorY + VenueSpace.WallHeight * 0.5f;
        float halfWall = VenueSpace.WallThickness * 0.5f;

        float xEdge = wallAtMaxX
            ? centre.X + footprint.X * 0.5f - halfWall
            : centre.X - footprint.X * 0.5f + halfWall;

        float zEdge = wallAtMaxZ
            ? centre.Z + footprint.Y * 0.5f - halfWall
            : centre.Z - footprint.Y * 0.5f + halfWall;

        var wallX = MakeBox(
            new Vector3(xEdge, wallY, centre.Z),
            new Vector3(VenueSpace.WallThickness, VenueSpace.WallHeight, footprint.Y),
            wallColor);
        wallX.Name = "WallX";
        root.AddChild(wallX);

        var wallZ = MakeBox(
            new Vector3(centre.X, wallY, zEdge),
            new Vector3(footprint.X, VenueSpace.WallHeight, VenueSpace.WallThickness),
            wallColor.Darkened(0.12f));
        wallZ.Name = "WallZ";
        root.AddChild(wallZ);

        // ── Furniture ──────────────────────────────────────────────────
        BuildFurniture(room, origin, size, root);

        return root;
    }

    /// <summary>
    /// Which edges of a footprint face away from the camera, and therefore
    /// carry the back walls. Derived from the camera basis so the cutaway can
    /// never end up walled shut against the viewer.
    /// </summary>
    public static (bool AtMaxX, bool AtMaxZ) BackWallEdges()
    {
        // Camera looks along −basis.Z; the far edges are the ones it points at.
        var forward = -VenueSpace.CameraBasis().Z;
        return (forward.X > 0f, forward.Z > 0f);
    }

    /// <summary>The footprint corner nearest the camera, in normalised (u, v).</summary>
    public static Vector2 FrontCornerUV()
    {
        var (atMaxX, atMaxZ) = BackWallEdges();
        return new Vector2(atMaxX ? 0f : 1f, atMaxZ ? 0f : 1f);
    }

    // ── Buildable-tile ghosts ──────────────────────────────────────────

    /// <summary>
    /// A faint outline plate for an unbuilt tile. Purely decorative — no
    /// collision, since the view picks empty tiles off its own floor plane.
    /// </summary>
    public static Node3D BuildGhostTile(Vector3I tile)
    {
        var centre = VenueSpace.CellCenter(tile);
        var colour = IsoTheme.GoldDim;
        colour.A = 0.16f;

        var ghost = MakeBox(
            new Vector3(centre.X, centre.Y + 0.02f, centre.Z),
            new Vector3(VenueSpace.TileSize * 0.92f, 0.02f, VenueSpace.TileSize * 0.92f),
            colour);

        ghost.Name = SafeName($"Ghost_{tile.X}_{tile.Y}");

        if (ghost.MaterialOverride is StandardMaterial3D mat)
        {
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.EmissionEnabled = false;
        }

        return ghost;
    }

    // ── Fade targets ───────────────────────────────────────────────────

    /// <summary>
    /// Walk a built subtree and collect everything the floor cutaway needs to
    /// dim. Procedural pieces fade through their material alpha; imported glb
    /// meshes keep their own textured materials and fade through
    /// <see cref="GeometryInstance3D.Transparency"/> instead.
    /// </summary>
    public static void CollectFadeTargets(
        Node node,
        List<StandardMaterial3D> materials,
        List<GeometryInstance3D> geometry,
        List<Label3D> labels,
        List<Light3D> lights)
    {
        if (node == null) return;

        switch (node)
        {
            case Label3D label:
                labels?.Add(label);
                break;

            case Light3D light:
                lights?.Add(light);
                break;

            case GeometryInstance3D geom:
                if (geom.MaterialOverride is StandardMaterial3D mat) materials?.Add(mat);
                else geometry?.Add(geom);
                break;
        }

        foreach (var child in node.GetChildren())
            CollectFadeTargets(child, materials, geometry, labels, lights);
    }

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

    // ── Furniture layout ───────────────────────────────────────────────

    private static void BuildFurniture(RoomModule room, Vector3I origin, Vector2I size, Node3D root)
    {
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
            float u = Mathf.Clamp(slot.X + index * 0.17f, 0.12f, 0.88f);
            float v = Mathf.Clamp(slot.Y + index * 0.11f, 0.12f, 0.88f);

            var at = VenueSpace.RoomPoint(origin, size, u, v);

            var tint = IsoTheme.GetStyleColor(item.StyleTag);
            if (item.IsDilapidated) tint = tint.Lerp(IsoTheme.Backdrop, 0.55f);

            var piece = BuildPiece(item, at, tint);
            if (piece == null) continue;

            piece.Name = SafeName($"{item.Category}_{index}");
            root.AddChild(piece);
        }
    }

    /// <summary>Normalised slot inside the footprint for each category.</summary>
    private static Vector2 GetSlot(FurnitureCategory category) => category switch
    {
        FurnitureCategory.Rug => new Vector2(0.50f, 0.55f),
        FurnitureCategory.Decor => new Vector2(0.50f, 0.12f),
        FurnitureCategory.Bed => new Vector2(0.34f, 0.30f),
        FurnitureCategory.Bar => new Vector2(0.50f, 0.24f),
        FurnitureCategory.Vanity => new Vector2(0.78f, 0.28f),
        FurnitureCategory.Screen => new Vector2(0.20f, 0.72f),
        FurnitureCategory.Bath => new Vector2(0.76f, 0.74f),
        FurnitureCategory.Seating => new Vector2(0.64f, 0.60f),
        FurnitureCategory.Lighting => new Vector2(0.16f, 0.18f),
        _ => new Vector2(0.50f, 0.50f)
    };

    private static Node3D BuildPiece(FurnitureItem item, Vector3 at, Color tint)
    {
        var model = TryInstanceModel(item, at, tint);
        if (model != null) return model;

        var node = new Node3D { Position = Vector3.Zero };

        switch (item.Category)
        {
            case FurnitureCategory.Bed: BuildBed(node, at, tint); break;
            case FurnitureCategory.Seating: BuildSeating(node, at, tint); break;
            case FurnitureCategory.Rug: BuildRug(node, at, tint); break;
            case FurnitureCategory.Bath: BuildBath(node, at, tint); break;
            case FurnitureCategory.Screen: BuildScreen(node, at, tint); break;
            case FurnitureCategory.Bar: BuildCounter(node, at, tint, 1.6f); break;
            case FurnitureCategory.Vanity: BuildCounter(node, at, tint, 0.9f); break;
            case FurnitureCategory.Decor: BuildDecor(node, at, tint); break;
            case FurnitureCategory.Lighting: BuildLamp(node, at, tint); break;
            default:
                node.AddChild(MakeBox(
                    at + new Vector3(0, 0.30f, 0),
                    new Vector3(0.6f, 0.6f, 0.6f), tint));
                break;
        }

        // Lighting always carries its own warm bulb, model or not — that glow
        // is what makes the cutaway read as an interior rather than a diorama.
        if (item.Category == FurnitureCategory.Lighting)
            node.AddChild(MakeLampGlow(at, item.IsDilapidated));

        return node;
    }

    // ── Procedural silhouettes ─────────────────────────────────────────

    private static void BuildBed(Node3D node, Vector3 at, Color tint)
    {
        node.AddChild(MakeBox(at + new Vector3(0f, 0.26f, 0f),
            new Vector3(1.10f, 0.34f, 1.70f), tint));

        // Headboard, toward the back of the room (−Z).
        node.AddChild(MakeBox(at + new Vector3(0f, 0.55f, -0.88f),
            new Vector3(1.14f, 0.90f, 0.12f), tint.Darkened(0.35f)));

        // Pillow, to break the slab up.
        node.AddChild(MakeBox(at + new Vector3(0f, 0.48f, -0.62f),
            new Vector3(0.80f, 0.12f, 0.32f), tint.Lightened(0.45f)));
    }

    private static void BuildSeating(Node3D node, Vector3 at, Color tint)
    {
        node.AddChild(MakeBox(at + new Vector3(0f, 0.24f, 0f),
            new Vector3(0.90f, 0.30f, 0.70f), tint));

        node.AddChild(MakeBox(at + new Vector3(0f, 0.55f, -0.31f),
            new Vector3(0.90f, 0.62f, 0.14f), tint.Darkened(0.28f)));
    }

    private static void BuildRug(Node3D node, Vector3 at, Color tint)
    {
        node.AddChild(MakeBox(at + new Vector3(0f, 0.015f, 0f),
            new Vector3(1.60f, 0.03f, 1.20f), tint.Darkened(0.15f)));

        node.AddChild(MakeBox(at + new Vector3(0f, 0.035f, 0f),
            new Vector3(1.10f, 0.03f, 0.78f), tint.Lightened(0.30f)));
    }

    private static void BuildBath(Node3D node, Vector3 at, Color tint)
    {
        node.AddChild(MakeBox(at + new Vector3(0f, 0.26f, 0f),
            new Vector3(1.30f, 0.52f, 0.75f), tint.Darkened(0.20f)));

        // Water/interior, sunk into the rim.
        node.AddChild(MakeBox(at + new Vector3(0f, 0.48f, 0f),
            new Vector3(1.06f, 0.06f, 0.54f), new Color("2b4b57")));
    }

    private static void BuildScreen(Node3D node, Vector3 at, Color tint)
    {
        for (int i = 0; i < 3; i++)
        {
            var panel = MakeBox(
                at + new Vector3((i - 1) * 0.38f, 0.75f, Mathf.Abs(i - 1) * 0.14f),
                new Vector3(0.40f, 1.50f, 0.05f),
                i == 1 ? tint : tint.Darkened(0.30f));

            panel.RotateY(Mathf.DegToRad((i - 1) * 32f));
            node.AddChild(panel);
        }
    }

    private static void BuildCounter(Node3D node, Vector3 at, Color tint, float lengthFactor)
    {
        float length = 1.20f * lengthFactor;

        node.AddChild(MakeBox(at + new Vector3(0f, 0.50f, 0f),
            new Vector3(length, 1.00f, 0.55f), tint));

        node.AddChild(MakeBox(at + new Vector3(0f, 1.03f, 0f),
            new Vector3(length + 0.10f, 0.06f, 0.65f), tint.Lightened(0.28f)));
    }

    private static void BuildDecor(Node3D node, Vector3 at, Color tint)
    {
        // Hung on the back wall rather than standing on the floor.
        node.AddChild(MakeBox(at + new Vector3(0f, VenueSpace.WallHeight * 0.62f, 0f),
            new Vector3(0.70f, 0.52f, 0.06f), tint));

        node.AddChild(MakeBox(at + new Vector3(0f, VenueSpace.WallHeight * 0.62f, -0.035f),
            new Vector3(0.78f, 0.60f, 0.03f), IsoTheme.Gold));
    }

    private static void BuildLamp(Node3D node, Vector3 at, Color tint)
    {
        node.AddChild(MakeBox(at + new Vector3(0f, 0.03f, 0f),
            new Vector3(0.24f, 0.06f, 0.24f), tint.Darkened(0.40f)));

        node.AddChild(MakeBox(at + new Vector3(0f, 0.32f, 0f),
            new Vector3(0.06f, 0.52f, 0.06f), tint.Darkened(0.25f)));

        var shade = MakeBox(at + new Vector3(0f, 0.68f, 0f),
            new Vector3(0.34f, 0.24f, 0.34f), IsoTheme.LampWarm);

        if (shade.MaterialOverride is StandardMaterial3D mat)
        {
            mat.EmissionEnabled = true;
            mat.Emission = IsoTheme.LampWarm;
            mat.EmissionEnergyMultiplier = 1.4f;
        }

        node.AddChild(shade);
    }

    private static OmniLight3D MakeLampGlow(Vector3 at, bool dim) => new()
    {
        Name = "Glow",
        Position = at + new Vector3(0f, 0.75f, 0f),
        LightColor = IsoTheme.LampWarm,
        LightEnergy = dim ? 0.55f : 1.25f,
        OmniRange = 4.5f,
        ShadowEnabled = false
    };

    // ── glb models ─────────────────────────────────────────────────────

    /// <summary>
    /// Instance the real model for a category, scaled to a sensible size for a
    /// 2 m tile. Returns null when there is no model for the category or the
    /// file could not be parsed, in which case the caller falls back to a box.
    /// </summary>
    private static Node3D TryInstanceModel(FurnitureItem item, Vector3 at, Color tint)
    {
        if (!ModelPaths.TryGetValue(item.Category, out var path)) return null;

        var source = LoadModel(path);
        if (source == null) return null;

        if (source.Duplicate() is not Node3D instance) return null;

        float target = ModelHeights.TryGetValue(item.Category, out float h) ? h : 0.8f;
        float scale = FitScale(instance, target);

        instance.Scale = new Vector3(scale, scale, scale);
        instance.Position = item.Category == FurnitureCategory.Decor
            ? at + new Vector3(0f, VenueSpace.WallHeight * 0.45f, 0f)
            : at;

        // Dilapidated pieces are washed out even when they are real geometry,
        // so decay reads the same way across model and procedural furniture.
        if (item.IsDilapidated) TintModel(instance, IsoTheme.Backdrop.Lerp(tint, 0.35f));

        var wrapper = new Node3D();
        wrapper.AddChild(instance);
        return wrapper;
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
                GD.PrintErr($"[VenueRoomBuilder] Cannot open '{path}'.");
                _modelFailures.Add(path);
                return null;
            }

            var bytes = file.GetBuffer((long)file.GetLength());
            var basePath = path.GetBaseDir() + "/";

            var document = new GltfDocument();
            var state = new GltfState { BasePath = basePath };

            if (document.AppendFromBuffer(bytes, basePath, state) != Error.Ok)
            {
                GD.PrintErr($"[VenueRoomBuilder] glTF parse failed for '{path}'.");
                _modelFailures.Add(path);
                return null;
            }

            if (document.GenerateScene(state) is not Node3D scene)
            {
                GD.PrintErr($"[VenueRoomBuilder] glTF produced no Node3D for '{path}'.");
                _modelFailures.Add(path);
                return null;
            }

            _modelCache[path] = scene;
            GD.Print($"[VenueRoomBuilder] Loaded model '{path}'.");
            return scene;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[VenueRoomBuilder] Model load error for '{path}': {ex.Message}");
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

    // ── Primitives ─────────────────────────────────────────────────────

    /// <summary>
    /// A box mesh with its own <see cref="StandardMaterial3D"/>. Every material
    /// is alpha-capable so the floor cutaway can fade it without rebuilding.
    /// </summary>
    public static MeshInstance3D MakeBox(Vector3 centre, Vector3 size, Color colour)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = colour,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.85f,
            Metallic = 0.0f
        };

        return new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            Position = centre,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On
        };
    }

    private static string SafeName(string raw) => raw.Replace('.', '_').Replace(':', '_');
}
