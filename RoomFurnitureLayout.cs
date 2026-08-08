using Godot;
using System.Collections.Generic;
using System.Linq;

/// <summary>How a piece meets the room.</summary>
public enum FurnitureAnchor
{
    /// <summary>Flat on the floor, away from walls. Rugs.</summary>
    FloorCentre,

    /// <summary>On the floor with its back against a wall. Beds, bars, vanities.</summary>
    AgainstWall,

    /// <summary>On the floor, free-standing, turned to face the room. Chairs.</summary>
    Facing,

    /// <summary>Hung on a wall at eye height. Mirrors and pictures.</summary>
    OnWall,

    /// <summary>Standing on another piece of furniture. Table lamps.</summary>
    OnSurface
}

/// <summary>One piece, positioned and turned.</summary>
public class FurniturePlacement
{
    public FurnitureItem Item { get; set; }

    /// <summary>World position of the piece's base.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Rotation about Y, radians. Zero faces +Z.</summary>
    public float Yaw { get; set; }
}

/// <summary>
/// Works out where furniture goes inside a room.
///
/// The previous version gave every category one fixed normalised slot and
/// dropped everything at floor level with no rotation at all. That produced
/// rooms where the bed floated in the middle facing nowhere, the lamp sat on
/// the floor two metres from the nightstand, and the mirror could land over
/// the bed. Objects on a floor, rather than a furnished room.
///
/// The fix is not better coordinates, it is giving pieces a *relationship* to
/// the room and to each other: a bed goes against the back wall, the
/// nightstand goes beside the bed, the lamp goes on the nightstand, the rug
/// goes in front of the bed, and the mirror goes on a side wall precisely
/// because the bed already owns the back one.
/// </summary>
public static class RoomFurnitureLayout
{
    /// <summary>Clearance kept between a piece and the wall behind it.</summary>
    private const float WallMargin = 0.25f;

    /// <summary>Gap left between neighbouring pieces.</summary>
    private const float Gap = 0.18f;

    /// <summary>Height a wall-hung piece is centred at.</summary>
    private const float EyeHeight = 1.55f;

    /// <summary>
    /// Rough footprint of each category, in metres, as (width, depth).
    /// Used for spacing rather than collision — close enough that pieces do
    /// not intersect, cheap enough to run on every rebuild.
    /// </summary>
    /// <summary>
    /// Whether a category should be scaled to its footprint rather than its
    /// height.
    ///
    /// <c>FitScale</c> normalises a model by total height, which is right for
    /// a lamp and badly wrong for anything flat or long. A rug was scaled
    /// until its *thickness* matched 3 cm, leaving its width to fall out of
    /// the model's proportions — which is why rugs came out far too small. A
    /// bed was scaled until the top of its headboard hit 62 cm, putting the
    /// mattress at ankle height.
    /// </summary>
    public static bool FitsByFootprint(FurnitureCategory category) =>
        category is FurnitureCategory.Rug or FurnitureCategory.Bed
                 or FurnitureCategory.Bar or FurnitureCategory.Bath;

    /// <summary>The longest horizontal dimension a category should end up at.</summary>
    public static float FootprintTarget(FurnitureCategory category)
    {
        var footprint = Footprint(category);
        return Mathf.Max(footprint.X, footprint.Y);
    }

    private static Vector2 Footprint(FurnitureCategory category) => category switch
    {
        FurnitureCategory.Bed => new Vector2(1.70f, 2.05f),
        FurnitureCategory.Seating => new Vector2(0.85f, 0.85f),
        FurnitureCategory.Lighting => new Vector2(0.32f, 0.32f),
        FurnitureCategory.Rug => new Vector2(2.20f, 1.50f),
        FurnitureCategory.Decor => new Vector2(0.90f, 0.12f),
        FurnitureCategory.Vanity => new Vector2(0.55f, 0.45f),
        FurnitureCategory.Screen => new Vector2(1.20f, 0.35f),
        FurnitureCategory.Bath => new Vector2(1.60f, 0.80f),
        FurnitureCategory.Bar => new Vector2(2.60f, 0.65f),
        _ => new Vector2(0.6f, 0.6f)
    };

    public static FurnitureAnchor AnchorFor(FurnitureCategory category) => category switch
    {
        FurnitureCategory.Rug => FurnitureAnchor.FloorCentre,
        FurnitureCategory.Decor => FurnitureAnchor.OnWall,
        FurnitureCategory.Lighting => FurnitureAnchor.OnSurface,
        FurnitureCategory.Seating => FurnitureAnchor.Facing,
        _ => FurnitureAnchor.AgainstWall
    };

    /// <summary>
    /// Lay out a room's furniture.
    ///
    /// Order matters and is deliberate: the bed claims the back wall first,
    /// everything else arranges itself around what is already placed.
    /// </summary>
    public static List<FurniturePlacement> Arrange(
        RoomModule room, Vector3I origin, Vector2I size)
    {
        var result = new List<FurniturePlacement>();
        if (room?.Furniture == null || room.Furniture.Count == 0) return result;

        // World-space rect of the room's interior.
        var nearLeft = VenueSpace.RoomPoint(origin, size, 0f, 0f);
        var farRight = VenueSpace.RoomPoint(origin, size, 1f, 1f);

        float xMin = Mathf.Min(nearLeft.X, farRight.X);
        float xMax = Mathf.Max(nearLeft.X, farRight.X);
        float zMin = Mathf.Min(nearLeft.Z, farRight.Z);
        float zMax = Mathf.Max(nearLeft.Z, farRight.Z);
        float floorY = nearLeft.Y;

        float centreX = (xMin + xMax) * 0.5f;

        var pieces = room.Furniture.Where(p => p != null).ToList();

        // ── The bed owns the back wall ─────────────────────────────────
        var beds = pieces.Where(p => p.Category == FurnitureCategory.Bed).ToList();
        var bedFootprint = Footprint(FurnitureCategory.Bed);

        float bedFrontZ = zMin;
        var bedCentres = new List<Vector3>();

        for (int i = 0; i < beds.Count; i++)
        {
            // Several beds share the wall, spread evenly across it.
            float slot = beds.Count == 1 ? 0.5f : (i + 0.5f) / beds.Count;
            float x = Mathf.Lerp(xMin + bedFootprint.X * 0.5f + WallMargin,
                                 xMax - bedFootprint.X * 0.5f - WallMargin, slot);

            float z = zMin + WallMargin + bedFootprint.Y * 0.5f;

            bedCentres.Add(new Vector3(x, floorY, z));
            bedFrontZ = Mathf.Max(bedFrontZ, z + bedFootprint.Y * 0.5f);

            result.Add(new FurniturePlacement
            {
                Item = beds[i],
                Position = new Vector3(x, floorY, z),
                Yaw = 0f          // headboard to -Z, facing into the room
            });
        }

        // ── Nightstands flank the bed ──────────────────────────────────
        var vanities = pieces.Where(p => p.Category == FurnitureCategory.Vanity).ToList();
        var vanityFootprint = Footprint(FurnitureCategory.Vanity);
        var surfaces = new List<Vector3>();

        for (int i = 0; i < vanities.Count; i++)
        {
            Vector3 at;

            if (bedCentres.Count > 0)
            {
                // Alternate sides of the nearest bed, so a pair brackets it.
                var bed = bedCentres[i / 2 % bedCentres.Count];
                float side = i % 2 == 0 ? -1f : 1f;
                float offset = bedFootprint.X * 0.5f + Gap + vanityFootprint.X * 0.5f;

                at = new Vector3(
                    Mathf.Clamp(bed.X + side * offset,
                                xMin + vanityFootprint.X * 0.5f + WallMargin,
                                xMax - vanityFootprint.X * 0.5f - WallMargin),
                    floorY,
                    zMin + WallMargin + vanityFootprint.Y * 0.5f);
            }
            else
            {
                // No bed — a dressing table against the back wall instead.
                float slot = (i + 0.5f) / Mathf.Max(1, vanities.Count);
                at = new Vector3(Mathf.Lerp(xMin + 0.6f, xMax - 0.6f, slot),
                                 floorY,
                                 zMin + WallMargin + vanityFootprint.Y * 0.5f);
            }

            // Remember the top, so a lamp has somewhere to stand.
            surfaces.Add(at + new Vector3(0f, FurnitureModelRegistry.GetTargetHeight(
                FurnitureCategory.Vanity), 0f));

            result.Add(new FurniturePlacement { Item = vanities[i], Position = at, Yaw = 0f });
        }

        // ── Rug in front of the bed ────────────────────────────────────
        var rugs = pieces.Where(p => p.Category == FurnitureCategory.Rug).ToList();
        var rugFootprint = Footprint(FurnitureCategory.Rug);

        for (int i = 0; i < rugs.Count; i++)
        {
            float z = bedCentres.Count > 0
                ? bedFrontZ + Gap + rugFootprint.Y * 0.5f
                : (zMin + zMax) * 0.5f;

            // Extra rugs step further into the room rather than stacking.
            z += i * (rugFootprint.Y + Gap);

            result.Add(new FurniturePlacement
            {
                Item = rugs[i],
                Position = new Vector3(centreX, floorY,
                    Mathf.Min(z, zMax - rugFootprint.Y * 0.5f - WallMargin)),
                Yaw = 0f
            });
        }

        // ── Lamps stand on a surface if there is one ───────────────────
        var lamps = pieces.Where(p => p.Category == FurnitureCategory.Lighting).ToList();

        for (int i = 0; i < lamps.Count; i++)
        {
            var at = i < surfaces.Count
                ? surfaces[i]
                // No table free: a floor lamp in the far corner instead.
                : new Vector3(xMax - WallMargin - 0.3f, floorY, zMax - WallMargin - 0.3f);

            result.Add(new FurniturePlacement { Item = lamps[i], Position = at, Yaw = 0f });
        }

        // ── Seating faces back toward the bed ──────────────────────────
        var seats = pieces.Where(p => p.Category == FurnitureCategory.Seating).ToList();
        var seatFootprint = Footprint(FurnitureCategory.Seating);

        for (int i = 0; i < seats.Count; i++)
        {
            float slot = seats.Count == 1 ? 0.5f : (i + 0.5f) / seats.Count;

            result.Add(new FurniturePlacement
            {
                Item = seats[i],
                Position = new Vector3(
                    Mathf.Lerp(xMin + seatFootprint.X, xMax - seatFootprint.X, slot),
                    floorY,
                    zMax - WallMargin - seatFootprint.Y * 0.5f),
                Yaw = Mathf.Pi     // turned around to look back at the room
            });
        }

        // ── Everything else along the side walls ───────────────────────
        var remaining = pieces.Where(p =>
            p.Category is FurnitureCategory.Screen or FurnitureCategory.Bath
                       or FurnitureCategory.Bar).ToList();

        for (int i = 0; i < remaining.Count; i++)
        {
            var footprint = Footprint(remaining[i].Category);
            bool leftWall = i % 2 == 0;

            result.Add(new FurniturePlacement
            {
                Item = remaining[i],
                Position = new Vector3(
                    leftWall ? xMin + WallMargin + footprint.Y * 0.5f
                             : xMax - WallMargin - footprint.Y * 0.5f,
                    floorY,
                    Mathf.Lerp(zMin + footprint.X, zMax - footprint.X,
                               remaining.Count == 1 ? 0.6f : (i + 0.5f) / remaining.Count)),
                Yaw = leftWall ? Mathf.Pi * 0.5f : -Mathf.Pi * 0.5f
            });
        }

        // ── Mirrors go on a side wall, never over the bed ──────────────
        var decor = pieces.Where(p => p.Category == FurnitureCategory.Decor).ToList();

        for (int i = 0; i < decor.Count; i++)
        {
            // The back wall is the bed's. Hanging a mirror there puts it over
            // the headboard, which was the old behaviour and looked wrong in
            // every screenshot. Side walls first; the front wall only once
            // both sides are taken.
            bool leftWall = i % 2 == 0;
            bool sideWall = i < 2 || bedCentres.Count == 0;

            var at = sideWall
                ? new Vector3(leftWall ? xMin + 0.06f : xMax - 0.06f,
                              floorY + EyeHeight,
                              Mathf.Lerp(zMin, zMax, 0.55f))
                : new Vector3(centreX, floorY + EyeHeight, zMax - 0.06f);

            result.Add(new FurniturePlacement
            {
                Item = decor[i],
                Position = at,
                Yaw = sideWall ? (leftWall ? Mathf.Pi * 0.5f : -Mathf.Pi * 0.5f) : Mathf.Pi
            });
        }

        return result;
    }
}
