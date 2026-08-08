using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Turns one <see cref="RoomModule"/> into real 3D geometry: a floor slab, the
/// two back walls that make it read as a cutaway, the trim that stops those
/// walls looking like bare cardboard, and its furniture.
///
/// Kept separate from <see cref="VenueView3D"/> deliberately — the view is
/// about camera, focus and input, and every position, size and height comes
/// from <see cref="VenueSpace"/>; every colour from <see cref="IsoTheme"/>.
/// What a bed looks like lives one level further out again, in
/// <see cref="VenueFurnitureBuilder"/>.
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

    // ── Trim ───────────────────────────────────────────────────────────

    /// <summary>Height of the skirting strip at the foot of a back wall.</summary>
    private const float BaseboardHeight = 0.17f;

    /// <summary>Height of the picture rail at the top of a back wall.</summary>
    private const float CorniceHeight = 0.09f;

    /// <summary>How far trim stands proud of the wall face, per side.</summary>
    private const float TrimProud = 0.035f;

    /// <summary>Width of the darker border band inset around a floor slab.</summary>
    private const float FloorBandWidth = 0.16f;

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

        // Back-of-house rooms get no gilding at all. Security in particular is
        // meant to look like the one part of the building nobody is charged to
        // be in, so its palette is flattened and its trim omitted entirely.
        bool utilitarian = room.Type == RoomType.Security
                        || room.Type == RoomType.Storage
                        || room.Type == RoomType.Service;

        var roomColor = IsoTheme.GetRoomColor(room.Type);
        if (utilitarian) roomColor = Desaturate(roomColor, 0.55f);

        // Only a hint of the room colour. At 30% the floor went fully mauve in
        // a suite and Baroque furniture — which is plum — vanished into it.
        // The room's identity belongs in its walls; floors stay timber so
        // whatever stands on them reads.
        var floorColor = IsoTheme.FloorBoards.Lerp(roomColor, 0.12f);

        // A lounge is the one public room people are supposed to linger in;
        // warming its boards separates it from the suites without a new hue.
        if (room.Type == RoomType.Lounge)
            floorColor = floorColor.Lerp(IsoTheme.FloorBoardsLit, 0.35f);

        // ── Floor slab ─────────────────────────────────────────────────
        // Hung *below* the walking surface so VenueSpace.FloorY stays the
        // plane furniture and pawns stand on.
        var slab = MakeBox(
            new Vector3(centre.X, floorY - VenueSpace.SlabThickness * 0.5f, centre.Z),
            new Vector3(footprint.X, VenueSpace.SlabThickness, footprint.Y),
            floorColor);
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

        BuildFloorBand(root, centre, footprint, floorY, floorColor);

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

        BuildWallTrim(root, centre, footprint, floorY, xEdge, zEdge, wallColor, utilitarian);

        // A bar's identity is the bottle wall behind the counter, not the
        // counter, so the room itself carries one whether it is furnished yet
        // or not.
        if (room.Type == RoomType.Bar)
            BuildBarBackShelf(root, centre, footprint, floorY, xEdge, wallAtMaxX, roomColor);

        // ── Furniture ──────────────────────────────────────────────────
        VenueFurnitureBuilder.Build(room, origin, size, root);

        return root;
    }

    /// <summary>
    /// Skirting along the foot of both back walls and a picture rail along the
    /// top. Both stand slightly proud of the wall face so they catch the light
    /// as separate planes rather than reading as painted-on stripes.
    /// </summary>
    private static void BuildWallTrim(
        Node3D root,
        Vector3 centre,
        Vector2 footprint,
        float floorY,
        float xEdge,
        float zEdge,
        Color wallColor,
        bool utilitarian)
    {
        float depth = VenueSpace.WallThickness + TrimProud * 2f;
        var skirting = wallColor.Darkened(0.45f);

        var baseX = MakeBox(
            new Vector3(xEdge, floorY + BaseboardHeight * 0.5f, centre.Z),
            new Vector3(depth, BaseboardHeight, footprint.Y),
            skirting);
        baseX.Name = "BaseboardX";
        root.AddChild(baseX);

        var baseZ = MakeBox(
            new Vector3(centre.X, floorY + BaseboardHeight * 0.5f, zEdge),
            new Vector3(footprint.X, BaseboardHeight, depth),
            skirting.Darkened(0.10f));
        baseZ.Name = "BaseboardZ";
        root.AddChild(baseZ);

        // Utilitarian rooms stop here — no rail, no gold, nothing decorative.
        if (utilitarian) return;

        float railY = floorY + VenueSpace.WallHeight - CorniceHeight * 0.5f;

        var railX = MakeBox(
            new Vector3(xEdge, railY, centre.Z),
            new Vector3(depth, CorniceHeight, footprint.Y),
            IsoTheme.GoldDim);
        railX.Name = "CorniceX";
        root.AddChild(railX);

        var railZ = MakeBox(
            new Vector3(centre.X, railY, zEdge),
            new Vector3(footprint.X, CorniceHeight, depth),
            IsoTheme.GoldDim.Darkened(0.12f));
        railZ.Name = "CorniceZ";
        root.AddChild(railZ);
    }

    /// <summary>
    /// A slightly darker border band laid on the floor around the slab's edge.
    /// Two adjacent rooms of the same type were previously one continuous sheet
    /// of colour; the band gives each footprint its own outline without needing
    /// a wall on the open sides.
    /// </summary>
    private static void BuildFloorBand(
        Node3D root, Vector3 centre, Vector2 footprint, float floorY, Color floorColor)
    {
        var band = floorColor.Darkened(0.38f);

        float y = floorY + 0.012f;
        const float thickness = 0.024f;

        float halfX = footprint.X * 0.5f - FloorBandWidth * 0.5f;
        float halfZ = footprint.Y * 0.5f - FloorBandWidth * 0.5f;

        var strips = new (string Name, Vector3 At, Vector3 Size)[]
        {
            ("BandZMin", new Vector3(centre.X, y, centre.Z - halfZ),
                new Vector3(footprint.X, thickness, FloorBandWidth)),
            ("BandZMax", new Vector3(centre.X, y, centre.Z + halfZ),
                new Vector3(footprint.X, thickness, FloorBandWidth)),
            ("BandXMin", new Vector3(centre.X - halfX, y, centre.Z),
                new Vector3(FloorBandWidth, thickness, footprint.Y)),
            ("BandXMax", new Vector3(centre.X + halfX, y, centre.Z),
                new Vector3(FloorBandWidth, thickness, footprint.Y))
        };

        foreach (var strip in strips)
        {
            var mesh = MakeBox(strip.At, strip.Size, band);
            mesh.Name = strip.Name;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            root.AddChild(mesh);
        }
    }

    /// <summary>
    /// Bottle shelving along the X back wall of a bar: a dark backing board and
    /// two planks, set in from the wall face so they read in silhouette.
    /// </summary>
    private static void BuildBarBackShelf(
        Node3D root,
        Vector3 centre,
        Vector2 footprint,
        float floorY,
        float xEdge,
        bool wallAtMaxX,
        Color roomColor)
    {
        // Push into the room, away from whichever side the wall sits on.
        float inward = wallAtMaxX ? -1f : 1f;
        float length = Mathf.Max(0.6f, footprint.Y * 0.7f);

        var backing = MakeBox(
            new Vector3(xEdge + inward * 0.09f, floorY + 1.30f, centre.Z),
            new Vector3(0.06f, 1.50f, length),
            roomColor.Darkened(0.62f));
        backing.Name = "BarBacking";
        root.AddChild(backing);

        for (int i = 0; i < 2; i++)
        {
            var plank = MakeBox(
                new Vector3(xEdge + inward * 0.22f, floorY + 0.95f + i * 0.55f, centre.Z),
                new Vector3(0.28f, 0.06f, length),
                roomColor.Lightened(0.14f));

            plank.Name = $"BarShelf{i}";
            root.AddChild(plank);
        }
    }

    /// <summary>
    /// Pull a colour toward its own luminance. Used for the back-of-house
    /// rooms, where a drab palette is the point rather than a compromise.
    /// </summary>
    private static Color Desaturate(Color colour, float amount)
    {
        float luma = colour.R * 0.299f + colour.G * 0.587f + colour.B * 0.114f;
        return colour.Lerp(new Color(luma, luma, luma, colour.A), Mathf.Clamp(amount, 0f, 1f));
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
    public static void ClearModelCache() => VenueFurnitureBuilder.ClearModelCache();

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
