using Godot;
using System;

/// <summary>
/// The one definition of how the venue grid maps into 3D world space.
///
/// Plays the role <see cref="IsoTheme"/>'s projection played for the 2D
/// renderer: the building geometry, the character pawns, the encounter
/// effects and the click raycast all have to agree on exactly where grid
/// cell (2, 1, 0) sits in the world, and a second copy of this maths
/// anywhere means those layers drift apart the first time a constant moves.
///
/// Convention: grid X runs along world +X, grid Y (depth) along world +Z,
/// and the floor index runs up world +Y. Godot is Y-up, so the venue's
/// "floor" axis and the world's vertical axis agree.
/// </summary>
public static class VenueSpace
{
    // ── Scale ──────────────────────────────────────────────────────────

    /// <summary>World size of one grid tile, in metres.</summary>
    public const float TileSize = 2.0f;

    /// <summary>World height from one floor's slab to the next.</summary>
    public const float FloorHeight = 3.2f;

    /// <summary>Thickness of a floor slab.</summary>
    public const float SlabThickness = 0.15f;

    /// <summary>Height of a room's back walls.</summary>
    public const float WallHeight = 2.1f;

    /// <summary>Thickness of a wall panel.</summary>
    public const float WallThickness = 0.12f;

    // ── Grid to world ──────────────────────────────────────────────────

    /// <summary>
    /// World position of the centre of a single grid cell, at floor level.
    /// </summary>
    public static Vector3 CellCenter(Vector3I cell) => new(
        (cell.X + 0.5f) * TileSize,
        cell.Z * FloorHeight,
        (cell.Y + 0.5f) * TileSize);

    /// <summary>
    /// World position of the centre of a room's whole footprint, at floor
    /// level. Rooms are multi-tile, so this is not the same as the centre of
    /// their origin cell.
    /// </summary>
    public static Vector3 RoomCenter(Vector3I origin, Vector2I size) => new(
        (origin.X + Mathf.Max(1, size.X) * 0.5f) * TileSize,
        origin.Z * FloorHeight,
        (origin.Y + Mathf.Max(1, size.Y) * 0.5f) * TileSize);

    /// <summary>
    /// World position of a normalised point inside a room's footprint, where
    /// (0,0) is the origin corner and (1,1) the far corner. Used to place
    /// furniture and to stand people somewhere sensible.
    /// </summary>
    public static Vector3 RoomPoint(Vector3I origin, Vector2I size, float u, float v) => new(
        (origin.X + u * Mathf.Max(1, size.X)) * TileSize,
        origin.Z * FloorHeight,
        (origin.Y + v * Mathf.Max(1, size.Y)) * TileSize);

    /// <summary>World-space footprint of a room, in metres.</summary>
    public static Vector2 RoomFootprint(Vector2I size) =>
        new(Mathf.Max(1, size.X) * TileSize, Mathf.Max(1, size.Y) * TileSize);

    /// <summary>Y coordinate of a floor's walking surface.</summary>
    public static float FloorY(int floor) => floor * FloorHeight;

    // ── World to grid ──────────────────────────────────────────────────

    /// <summary>
    /// Which grid cell on <paramref name="floor"/> contains a world point.
    /// Used to turn a raycast hit back into a room.
    /// </summary>
    public static Vector3I WorldToCell(Vector3 world, int floor) => new(
        Mathf.FloorToInt(world.X / TileSize),
        Mathf.FloorToInt(world.Z / TileSize),
        floor);

    /// <summary>Floor index nearest a world height.</summary>
    public static int WorldToFloor(float worldY) =>
        Mathf.RoundToInt(worldY / FloorHeight);

    // ── Camera ─────────────────────────────────────────────────────────

    /// <summary>
    /// Yaw of the isometric camera. 45° puts the grid corner toward the
    /// viewer, which is what produces the classic diamond read.
    /// </summary>
    public const float CameraYawDegrees = -45f;

    /// <summary>
    /// Pitch of the isometric camera. 30° is the true isometric angle and
    /// keeps floors clearly separated; steeper flattens the building and
    /// hides the interiors, shallower buries lower floors behind upper ones.
    /// </summary>
    public const float CameraPitchDegrees = -30f;

    /// <summary>Default orthographic view size, in metres of world height.</summary>
    public const float DefaultCameraSize = 18f;

    public const float MinCameraSize = 6f;
    public const float MaxCameraSize = 46f;

    /// <summary>
    /// Basis for the isometric camera. Applied as pitch-after-yaw so the
    /// horizon stays level.
    /// </summary>
    public static Basis CameraBasis() =>
        new Basis(Vector3.Up, Mathf.DegToRad(CameraYawDegrees)) *
        new Basis(Vector3.Right, Mathf.DegToRad(CameraPitchDegrees));

    /// <summary>
    /// How far back to place the camera along its own forward axis. An
    /// orthographic camera has no perspective falloff, so this only needs to
    /// clear the geometry rather than frame it.
    /// </summary>
    public const float CameraDistance = 40f;

    // ── Floor visibility ───────────────────────────────────────────────

    /// <summary>
    /// Opacity for a floor relative to the focused one.
    ///
    /// Floors above the focus are cut away almost entirely — that is the
    /// dollhouse conceit, and without it an upper storey's slab simply hides
    /// everything under it. Floors below stay dim but legible so the player
    /// keeps a sense of the whole building.
    /// </summary>
    public static float GetFloorOpacity(int floor, int focusedFloor)
    {
        if (floor == focusedFloor) return 1f;
        if (floor > focusedFloor) return Mathf.Max(0f, 0.10f - (floor - focusedFloor - 1) * 0.05f);

        return Mathf.Max(0.25f, 0.62f - (focusedFloor - floor - 1) * 0.16f);
    }
}
