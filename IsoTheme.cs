using Godot;
using System;

/// <summary>
/// Shared visual language for the isometric dollhouse: projection maths and
/// palette.
///
/// Everything that draws the venue goes through here. The projection in
/// particular is load-bearing — the building renderer, the pawn layer, the
/// alert bubbles and the build-mode cursor all have to agree on where grid
/// cell (2, 1, 0) lands on screen, and a second copy of this maths anywhere
/// means things drift apart the first time a constant changes.
///
/// The palette is drawn from the reference art: near-black plum ground,
/// warm amber interior light, deep crimson and purple room walls, aged gold
/// for framing and UI trim. Values are placeholders that read correctly in
/// flat colour, and are intended to be replaced by illustrated sprites —
/// keep the *names* stable when that happens.
/// </summary>
public static class IsoTheme
{
    // ── Projection ─────────────────────────────────────────────────────

    /// <summary>Screen width of one grid tile. Classic 2:1 isometric.</summary>
    public const float TileWidth = 96f;

    /// <summary>Screen height of one grid tile. Half the width gives the 2:1 look.</summary>
    public const float TileHeight = 48f;

    /// <summary>
    /// Vertical screen offset between one floor and the next.
    ///
    /// Must exceed the on-screen depth of a whole floor plan or storeys
    /// overlap instead of stacking. A 6×5 plan spans (6+5) × TileHeight/2 =
    /// 264px, so 150 buried floor 1 inside floor 0 and the building read as
    /// a murky blob rather than a dollhouse. This clears the plan plus the
    /// wall height with a little air.
    /// </summary>
    public const float FloorHeight = 200f;

    /// <summary>Height of a drawn wall, in screen pixels.</summary>
    public const float WallHeight = 96f;

    /// <summary>
    /// Project a grid cell to its screen-space centre.
    ///
    /// X and Y run along the two visible ground axes; Z is the floor index
    /// and lifts the cell straight up the screen, which is what produces the
    /// stacked-dollhouse read.
    /// </summary>
    public static Vector2 GridToScreen(Vector3I cell) => new(
        (cell.X - cell.Y) * (TileWidth * 0.5f),
        (cell.X + cell.Y) * (TileHeight * 0.5f) - cell.Z * FloorHeight);

    /// <summary>Project a fractional grid position, for pawns moving between cells.</summary>
    public static Vector2 GridToScreen(float x, float y, int floor) => new(
        (x - y) * (TileWidth * 0.5f),
        (x + y) * (TileHeight * 0.5f) - floor * FloorHeight);

    /// <summary>
    /// Inverse projection: which cell on <paramref name="floor"/> lies under
    /// a screen point. Used for click-to-select and the build cursor.
    /// </summary>
    public static Vector3I ScreenToGrid(Vector2 screen, int floor)
    {
        var lifted = screen.Y + floor * FloorHeight;

        var fx = (screen.X / (TileWidth * 0.5f) + lifted / (TileHeight * 0.5f)) * 0.5f;
        var fy = (lifted / (TileHeight * 0.5f) - screen.X / (TileWidth * 0.5f)) * 0.5f;

        return new Vector3I(Mathf.RoundToInt(fx), Mathf.RoundToInt(fy), floor);
    }

    /// <summary>
    /// The four corners of a room's floor plate, in screen space. Takes the
    /// room's footprint so multi-tile rooms draw as one plate rather than a
    /// grid of separate diamonds.
    /// </summary>
    public static Vector2[] GetFloorPolygon(Vector3I origin, Vector2I size)
    {
        var w = Mathf.Max(1, size.X);
        var d = Mathf.Max(1, size.Y);

        var north = GridToScreen(origin.X, origin.Y, origin.Z);
        var east = GridToScreen(origin.X + w, origin.Y, origin.Z);
        var south = GridToScreen(origin.X + w, origin.Y + d, origin.Z);
        var west = GridToScreen(origin.X, origin.Y + d, origin.Z);

        // Shift up by half a tile so the plate sits under the cell centre.
        var lift = new Vector2(0, -TileHeight * 0.5f);
        return new[] { north + lift, east + lift, south + lift, west + lift };
    }

    /// <summary>
    /// The back-left wall quad for a room, drawn behind its contents.
    /// </summary>
    public static Vector2[] GetLeftWallPolygon(Vector3I origin, Vector2I size)
    {
        var floor = GetFloorPolygon(origin, size);
        var up = new Vector2(0, -WallHeight);

        // West edge: north corner → west corner.
        return new[] { floor[0], floor[3], floor[3] + up, floor[0] + up };
    }

    /// <summary>The back-right wall quad for a room.</summary>
    public static Vector2[] GetRightWallPolygon(Vector3I origin, Vector2I size)
    {
        var floor = GetFloorPolygon(origin, size);
        var up = new Vector2(0, -WallHeight);

        // North edge: north corner → east corner.
        return new[] { floor[0], floor[1], floor[1] + up, floor[0] + up };
    }

    /// <summary>
    /// Painter's-algorithm sort key. Rooms further "back" and lower down
    /// must draw first. Floor dominates so an upper storey always paints
    /// over the one beneath it.
    /// </summary>
    public static int GetDepthKey(Vector3I cell) =>
        cell.Z * 10000 + (cell.X + cell.Y) * 100 + cell.X;

    // ── Palette ────────────────────────────────────────────────────────

    /// <summary>Page background behind the whole building.</summary>
    public static readonly Color Backdrop = new("140d18");

    /// <summary>Masonry of the building exterior.</summary>
    public static readonly Color Facade = new("2e2434");
    public static readonly Color FacadeShadow = new("1d1622");

    /// <summary>Heavy outline that gives the cutaway cells their graphic-novel edge.</summary>
    public static readonly Color CellOutline = new("0a0710");

    /// <summary>Aged gold used for framing, trim and selection.</summary>
    public static readonly Color Gold = new("c9a227");
    public static readonly Color GoldDim = new("7d6518");

    /// <summary>Warm interior light, for lamps and lit floors.</summary>
    public static readonly Color LampWarm = new("ffca7a");
    public static readonly Color LampGlow = new("ff9d4d");

    /// <summary>Timber floor. Lit plates are noticeably warmer so a furnished,
    /// working room reads as inhabited at a glance.</summary>
    public static readonly Color FloorBoards = new("6b4a33");
    public static readonly Color FloorBoardsLit = new("9c6b45");

    /// <summary>Text.</summary>
    public static readonly Color TextPrimary = new("f2e6d8");
    public static readonly Color TextMuted = new("9c8b7d");

    /// <summary>Status colours, also used by the encounter cloud.</summary>
    public static readonly Color Good = new("7fb069");
    public static readonly Color Warning = new("e0a458");
    public static readonly Color Danger = new("c1524e");

    // ── Room colours ───────────────────────────────────────────────────

    /// <summary>
    /// Wall colour per room type. Suites get the rich crimsons and purples of
    /// the reference art; service and basement spaces are deliberately drab
    /// so the eye goes where the money is.
    /// </summary>
    /// Values are pitched well above the backdrop: the first pass sat only a
    /// few percent brighter than the page and the whole building vanished
    /// into it. Walls are shaded down from these, so they need headroom.
    public static Color GetRoomColor(RoomType type) => type switch
    {
        RoomType.VIPSuite => new Color("a8365f"),
        RoomType.PrivateSuite => new Color("8f4374"),
        RoomType.Lounge => new Color("7a3c6f"),
        RoomType.Bar => new Color("8a4f5e"),
        RoomType.VIPEntrance => new Color("6f4c8c"),
        RoomType.Security => new Color("46536a"),
        RoomType.Medical => new Color("4a6569"),
        RoomType.Service => new Color("54495d"),
        RoomType.Storage => new Color("47424f"),
        RoomType.Soundproofing => new Color("5b4d66"),
        _ => new Color("3a3244")
    };

    /// <summary>Accent colour per furniture style, for placeholder pieces.</summary>
    public static Color GetStyleColor(FurnitureStyle style) => style switch
    {
        FurnitureStyle.Baroque => new Color("8b3a5e"),
        FurnitureStyle.ArtDeco => new Color("caa15b"),
        FurnitureStyle.Oriental => new Color("9d4b3a"),
        FurnitureStyle.Bohemian => new Color("7a6a3f"),
        FurnitureStyle.Modern => new Color("55606b"),
        FurnitureStyle.Spartan => new Color("6b6560"),
        _ => new Color("5a5560")
    };

    /// <summary>
    /// Colour ramp for a 0–100 quality score: Appointment, satisfaction,
    /// condition. Red through amber to green.
    /// </summary>
    public static Color GetScoreColor(float score01To100)
    {
        var t = Mathf.Clamp(score01To100 / 100f, 0f, 1f);
        return t < 0.5f
            ? Danger.Lerp(Warning, t * 2f)
            : Warning.Lerp(Good, (t - 0.5f) * 2f);
    }

    /// <summary>
    /// How strongly a floor is dimmed when it is not the focused one. Lets
    /// the player read one storey at a time without hiding the others.
    /// </summary>
    public const float UnfocusedFloorAlpha = 0.35f;
}
