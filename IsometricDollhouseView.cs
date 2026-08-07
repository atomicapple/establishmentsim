using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The illustrated-isometric "dollhouse" renderer: a cutaway of the whole
/// <see cref="VenueBuilding"/> seen from a fixed 2:1 isometric angle, with the
/// floors stacked up the screen and every room drawn as a lit interior cell.
///
/// Nothing here is art yet — every shape is a Godot 2D primitive placed by
/// <see cref="IsoTheme"/>. The intent is that each draw call maps one-to-one
/// onto a sprite later: the floor plate becomes a floor tile, the two back
/// walls become wall sprites, the furniture boxes become furniture sprites.
/// Keep the projection maths in <see cref="IsoTheme"/> — this view deliberately
/// owns none of it.
///
/// Usage: add as a child Node2D, call <see cref="Bind"/> with the venue, then
/// <see cref="CenterOnBuilding"/>. Listen to <see cref="SignalName.OnRoomClicked"/>
/// for selection and <see cref="SignalName.OnFocusedFloorChanged"/> for the HUD's
/// floor indicator.
/// </summary>
public partial class IsometricDollhouseView : Node2D
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when the player clicks a room. Floor is the Z coordinate.</summary>
    [Signal]
    public delegate void OnRoomClickedEventHandler(int x, int y, int floor);

    /// <summary>Fired when the player clicks a buildable tile with no room on it.</summary>
    [Signal]
    public delegate void OnEmptyTileClickedEventHandler(int x, int y, int floor);

    /// <summary>Fired whenever the focused floor changes.</summary>
    [Signal]
    public delegate void OnFocusedFloorChangedEventHandler(int floor);

    /// <summary>
    /// Fired when the camera pans or zooms, so sibling layers (pawns, VFX,
    /// alert bubbles) can mirror the transform without polling.
    /// </summary>
    [Signal]
    public delegate void OnCameraChangedEventHandler(Vector2 pan, float zoom);

    // ── Tuning ─────────────────────────────────────────────────────────

    /// <summary>Pixels per second the arrow keys pan the view.</summary>
    [Export] public float PanSpeed { get; set; } = 700f;

    /// <summary>Multiplicative zoom step per wheel notch.</summary>
    [Export] public float ZoomStep { get; set; } = 1.12f;

    [Export] public float MinZoom { get; set; } = 0.30f;
    [Export] public float MaxZoom { get; set; } = 2.60f;

    /// <summary>Font size for room names.</summary>
    [Export] public int LabelFontSize { get; set; } = 12;

    /// <summary>Font size for the Appointment readout under a room name.</summary>
    [Export] public int ScoreFontSize { get; set; } = 10;

    /// <summary>Outline weight of the cutaway cells — this is the graphic-novel edge.</summary>
    [Export] public float OutlineWidth { get; set; } = 3f;

    /// <summary>Whether unoccupied tiles on the focused floor draw as ghost outlines.</summary>
    [Export] public bool ShowBuildableTiles { get; set; } = true;

    // ── State ──────────────────────────────────────────────────────────

    private VenueBuilding _venue;
    private int _focusedFloor;
    private Vector3I? _selected;
    private bool _isPanning;
    private float _zoom = 1f;

    /// <summary>The model being rendered, or null before <see cref="Bind"/>.</summary>
    public VenueBuilding Venue => HasVenue ? _venue : null;

    private bool HasVenue => _venue != null && GodotObject.IsInstanceValid(_venue);

    /// <summary>
    /// Floor the player is reading right now. Everything else draws at
    /// <see cref="IsoTheme.UnfocusedFloorAlpha"/>. Clamped to the venue's
    /// built range.
    /// </summary>
    public int FocusedFloor
    {
        get => _focusedFloor;
        set
        {
            int clamped = ClampFloor(value);
            if (clamped == _focusedFloor) return;

            _focusedFloor = clamped;
            EmitSignal(SignalName.OnFocusedFloorChanged, _focusedFloor);
            QueueRedraw();
        }
    }

    /// <summary>Origin tile of the highlighted room, or null when nothing is selected.</summary>
    public Vector3I? SelectedRoomOrigin => _selected;

    /// <summary>Current camera zoom factor. Clamped to [MinZoom, MaxZoom].</summary>
    public float Zoom
    {
        get => _zoom;
        set => SetZoom(value, GetViewportRect().Size * 0.5f);
    }

    /// <summary>Current camera pan, in screen pixels. This is the node's own position.</summary>
    public Vector2 PanOffset
    {
        get => Position;
        set
        {
            Position = value;
            EmitSignal(SignalName.OnCameraChanged, Position, _zoom);
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        Scale = new Vector2(_zoom, _zoom);
        QueueRedraw();
    }

    /// <summary>
    /// Attach the model. Safe to call with null — the view simply draws
    /// nothing until a real venue arrives.
    /// </summary>
    public void Bind(VenueBuilding venue)
    {
        _venue = venue;
        _selected = null;
        _focusedFloor = ClampFloor(_focusedFloor);
        QueueRedraw();
    }

    /// <summary>Re-read the model and repaint. Cheap — call it from any change signal.</summary>
    public void Refresh()
    {
        _focusedFloor = ClampFloor(_focusedFloor);

        // A room may have been demolished out from under the selection.
        if (_selected.HasValue && FindRoomCovering(_selected.Value) == null)
            _selected = null;

        QueueRedraw();
    }

    // ── Floor focus ────────────────────────────────────────────────────

    /// <summary>Focus one storey higher, clamped to the top built floor.</summary>
    public void FocusUp() => FocusedFloor = _focusedFloor + 1;

    /// <summary>Focus one storey lower, clamped to the bottom built floor.</summary>
    public void FocusDown() => FocusedFloor = _focusedFloor - 1;

    private int ClampFloor(int floor)
    {
        if (!HasVenue) return floor;
        return Mathf.Clamp(floor, _venue.LowestFloor, _venue.HighestFloor);
    }

    // ── Selection ──────────────────────────────────────────────────────

    /// <summary>Highlight the room whose origin is this tile. Null clears.</summary>
    public void SelectRoom(Vector3I? origin)
    {
        _selected = origin;
        QueueRedraw();
    }

    /// <summary>Drop the gold selection outline.</summary>
    public void ClearSelection() => SelectRoom(null);

    /// <summary>
    /// Resolve a screen point to the room under it on the focused floor.
    /// Returns null over empty tiles or outside the plan.
    /// </summary>
    public RoomModule GetRoomAtScreenPoint(Vector2 screenPoint)
    {
        var local = ToLocal(screenPoint);
        return FindRoomCovering(IsoTheme.ScreenToGrid(local, _focusedFloor));
    }

    private RoomModule FindRoomCovering(Vector3I tile)
    {
        if (!HasVenue) return null;

        foreach (var room in _venue.Rooms.Values)
        {
            if (room != null && room.Covers(tile)) return room;
        }

        return null;
    }

    // ── Camera ─────────────────────────────────────────────────────────

    /// <summary>
    /// Frame the whole building in the viewport: centres the union of every
    /// floor plate and wall top. Falls back to the world origin when there is
    /// nothing to look at.
    /// </summary>
    public void CenterOnBuilding()
    {
        var bounds = GetBuildingBounds();
        var viewportCenter = GetViewportRect().Size * 0.5f;

        PanOffset = viewportCenter - bounds.GetCenter() * _zoom;
        QueueRedraw();
    }

    /// <summary>Local-space bounding box of everything this view draws.</summary>
    public Rect2 GetBuildingBounds()
    {
        var points = new List<Vector2>();

        if (HasVenue)
        {
            foreach (var room in _venue.Rooms.Values)
            {
                if (room == null) continue;
                CollectCellExtent(points, room.GridPosition, room.Size);
            }

            foreach (int floor in _venue.Floors)
            {
                CollectCellExtent(
                    points,
                    new Vector3I(0, 0, floor),
                    new Vector2I(_venue.FloorWidth, _venue.FloorDepth));
            }
        }

        if (points.Count == 0) return new Rect2(Vector2.Zero, Vector2.Zero);

        float minX = points.Min(p => p.X);
        float maxX = points.Max(p => p.X);
        float minY = points.Min(p => p.Y);
        float maxY = points.Max(p => p.Y);

        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    private static void CollectCellExtent(List<Vector2> into, Vector3I origin, Vector2I size)
    {
        var plate = IsoTheme.GetFloorPolygon(origin, size);
        var up = new Vector2(0, -IsoTheme.WallHeight);

        foreach (var corner in plate)
        {
            into.Add(corner);
            into.Add(corner + up);
        }
    }

    /// <summary>Set the zoom, keeping the world point under <paramref name="anchor"/> fixed.</summary>
    public void SetZoom(float zoom, Vector2 anchor)
    {
        float clamped = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(clamped, _zoom)) return;

        // World point currently under the anchor, in this node's local space.
        var world = (anchor - Position) / _zoom;

        _zoom = clamped;
        Scale = new Vector2(_zoom, _zoom);
        Position = anchor - world * _zoom;

        EmitSignal(SignalName.OnCameraChanged, Position, _zoom);
        QueueRedraw();
    }

    // ── Input ──────────────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse)
        {
            HandleMouseButton(mouse);
            return;
        }

        if (@event is InputEventMouseMotion motion && _isPanning)
        {
            PanOffset += motion.Relative;
        }
    }

    private void HandleMouseButton(InputEventMouseButton mouse)
    {
        switch (mouse.ButtonIndex)
        {
            case MouseButton.Middle:
                _isPanning = mouse.Pressed;
                break;

            case MouseButton.WheelUp when mouse.Pressed:
                SetZoom(_zoom * ZoomStep, mouse.Position);
                break;

            case MouseButton.WheelDown when mouse.Pressed:
                SetZoom(_zoom / ZoomStep, mouse.Position);
                break;

            case MouseButton.Left when mouse.Pressed:
                HandleClick(mouse.Position);
                break;
        }
    }

    private void HandleClick(Vector2 screenPoint)
    {
        if (!HasVenue) return;

        var tile = IsoTheme.ScreenToGrid(ToLocal(screenPoint), _focusedFloor);
        var room = FindRoomCovering(tile);

        if (room != null)
        {
            _selected = room.GridPosition;
            QueueRedraw();

            EmitSignal(SignalName.OnRoomClicked,
                room.GridPosition.X, room.GridPosition.Y, room.GridPosition.Z);
            return;
        }

        if (_venue.IsInBounds(tile))
            EmitSignal(SignalName.OnEmptyTileClicked, tile.X, tile.Y, tile.Z);
    }

    public override void _Process(double delta)
    {
        var direction = Vector2.Zero;

        if (Input.IsPhysicalKeyPressed(Key.Left)) direction.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.Right)) direction.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.Up)) direction.Y += 1f;
        if (Input.IsPhysicalKeyPressed(Key.Down)) direction.Y -= 1f;

        if (direction == Vector2.Zero) return;

        PanOffset += direction.Normalized() * PanSpeed * (float)delta;
    }

    // ── Drawing ────────────────────────────────────────────────────────

    /// <summary>
    /// One painter's-algorithm pass over the whole building. Ghost tiles and
    /// rooms go into the same sorted list so a room on a lower floor can never
    /// paint over the buildable grid of the floor above it.
    /// </summary>
    public override void _Draw()
    {
        if (!HasVenue) return;

        var queue = new List<(int Depth, RoomModule? Room, Vector3I Tile)>();

        foreach (var room in _venue.Rooms.Values)
        {
            if (room == null) continue;
            queue.Add((IsoTheme.GetDepthKey(room.GridPosition), room, room.GridPosition));
        }

        if (ShowBuildableTiles)
        {
            foreach (var tile in _venue.GetEmptyTiles(_focusedFloor))
                queue.Add((IsoTheme.GetDepthKey(tile), null, tile));
        }

        queue.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        foreach (var entry in queue)
        {
            if (entry.Room == null) DrawBuildableTile(entry.Tile);
            else DrawRoom(entry.Room);
        }
    }

    private void DrawBuildableTile(Vector3I tile)
    {
        var plate = IsoTheme.GetFloorPolygon(tile, Vector2I.One);
        // Kept very faint: these span the whole floor plan, so at any real
        // opacity the empty grid out-competes the rooms for attention.
        DrawPolyline(Close(plate), Fade(IsoTheme.GoldDim, 0.16f), 1.5f, antialiased: true);
    }

    private void DrawRoom(RoomModule room)
    {
        float alpha = room.Floor == _focusedFloor ? 1f : IsoTheme.UnfocusedFloorAlpha;

        var origin = room.GridPosition;
        var size = room.Size;

        var baseColor = IsoTheme.GetRoomColor(room.Type);
        var plate = IsoTheme.GetFloorPolygon(origin, size);

        // Back walls first: they sit behind everything standing on the plate.
        // Left wall catches less light than the right one, which is what sells
        // the corner as a corner rather than a flat colour field.
        DrawColoredPolygon(
            IsoTheme.GetLeftWallPolygon(origin, size),
            Fade(baseColor.Darkened(0.45f), alpha));

        DrawColoredPolygon(
            IsoTheme.GetRightWallPolygon(origin, size),
            Fade(baseColor.Darkened(0.28f), alpha));

        // Lit floor: warm boards, brighter for rooms that actually earn.
        bool lit = _venue.IsRevenueGenerating(room);
        var floorColor = lit ? IsoTheme.FloorBoardsLit : IsoTheme.FloorBoards;
        DrawColoredPolygon(plate, Fade(floorColor.Lerp(baseColor, 0.35f), alpha));

        // A warm pool of lamplight in the middle of the plate.
        if (lit) DrawLampPool(plate, alpha);

        DrawFurniture(room, alpha);

        // Heavy cutaway outline over the top of the contents.
        var outline = Fade(IsoTheme.CellOutline, alpha);
        DrawPolyline(Close(plate), outline, OutlineWidth, antialiased: true);
        DrawPolyline(Close(IsoTheme.GetLeftWallPolygon(origin, size)), outline,
            OutlineWidth * 0.7f, antialiased: true);
        DrawPolyline(Close(IsoTheme.GetRightWallPolygon(origin, size)), outline,
            OutlineWidth * 0.7f, antialiased: true);

        if (_selected.HasValue && _selected.Value == origin)
            DrawSelection(origin, size);

        DrawRoomLabel(room, plate, alpha);
    }

    private void DrawLampPool(Vector2[] plate, float alpha)
    {
        var center = Centroid(plate);
        float radius = Mathf.Max(12f, (plate[1].X - plate[3].X) * 0.18f);

        DrawCircle(center, radius, Fade(IsoTheme.LampGlow, 0.10f * alpha));
        DrawCircle(center, radius * 0.55f, Fade(IsoTheme.LampWarm, 0.13f * alpha));
    }

    /// <summary>
    /// Placeholder furnishings, laid out on a simple grid inside the plate.
    /// Each piece is a little extruded box tinted by its style; dilapidated
    /// pieces wash out toward the wall colour so a decayed room reads as
    /// decayed at a glance, without needing the tooltip.
    /// </summary>
    private void DrawFurniture(RoomModule room, float alpha)
    {
        var pieces = room.Furniture;
        if (pieces == null || pieces.Count == 0) return;

        int width = Mathf.Max(1, room.Size.X);
        int depth = Mathf.Max(1, room.Size.Y);

        int columns = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(pieces.Count)), 1, width * 2);
        int rows = Mathf.Max(1, Mathf.CeilToInt(pieces.Count / (float)columns));

        for (int i = 0; i < pieces.Count; i++)
        {
            var item = pieces[i];
            if (item == null) continue;

            int column = i % columns;
            int row = i / columns;

            float fx = room.GridPosition.X + (column + 0.5f) * (width / (float)columns) - 0.5f;
            float fy = room.GridPosition.Y + (row + 0.5f) * (depth / (float)rows) - 0.5f;

            var anchor = IsoTheme.GridToScreen(fx, fy, room.GridPosition.Z);

            var tint = IsoTheme.GetStyleColor(item.StyleTag);
            float pieceAlpha = alpha;

            if (item.IsDilapidated)
            {
                tint = tint.Lerp(IsoTheme.FacadeShadow, 0.55f);
                pieceAlpha *= 0.5f;
            }

            DrawFurniturePiece(anchor, item, tint, pieceAlpha, columns, rows);
        }
    }

    private void DrawFurniturePiece(
        Vector2 anchor, FurnitureItem item, Color tint, float alpha, int columns, int rows)
    {
        // Footprint shrinks as the room gets busier so pieces never merge into
        // one blob; height varies by category so the silhouettes stay readable.
        float halfWidth = Mathf.Max(6f, IsoTheme.TileWidth * 0.22f / Mathf.Max(1, columns * 0.6f));
        float halfDepth = Mathf.Max(3f, IsoTheme.TileHeight * 0.22f / Mathf.Max(1, rows * 0.6f));
        float height = GetPieceHeight(item.Category);

        var top = new Vector2[]
        {
            anchor + new Vector2(0, -halfDepth - height),
            anchor + new Vector2(halfWidth, -height),
            anchor + new Vector2(0, halfDepth - height),
            anchor + new Vector2(-halfWidth, -height)
        };

        if (height > 0.5f)
        {
            // Two visible side faces, darker than the top.
            DrawColoredPolygon(
                new[] { top[3], top[2], top[2] + new Vector2(0, height), top[3] + new Vector2(0, height) },
                Fade(tint.Darkened(0.45f), alpha));

            DrawColoredPolygon(
                new[] { top[2], top[1], top[1] + new Vector2(0, height), top[2] + new Vector2(0, height) },
                Fade(tint.Darkened(0.28f), alpha));
        }

        DrawColoredPolygon(top, Fade(tint, alpha));
        DrawPolyline(Close(top), Fade(IsoTheme.CellOutline, alpha * 0.8f), 1.2f, antialiased: true);
    }

    private static float GetPieceHeight(FurnitureCategory category) => category switch
    {
        FurnitureCategory.Rug => 0f,
        FurnitureCategory.Bed => 8f,
        FurnitureCategory.Bath => 10f,
        FurnitureCategory.Seating => 12f,
        FurnitureCategory.Vanity => 18f,
        FurnitureCategory.Bar => 20f,
        FurnitureCategory.Decor => 16f,
        FurnitureCategory.Screen => 26f,
        FurnitureCategory.Lighting => 30f,
        _ => 14f
    };

    private void DrawSelection(Vector3I origin, Vector2I size)
    {
        var plate = IsoTheme.GetFloorPolygon(origin, size);
        var up = new Vector2(0, -IsoTheme.WallHeight);

        DrawPolyline(Close(plate), IsoTheme.Gold, OutlineWidth + 1.5f, antialiased: true);

        // Gold uprights at the two visible back corners, so a selected room
        // reads as selected even when its plate is mostly hidden by furniture.
        DrawLine(plate[0], plate[0] + up, IsoTheme.Gold, 2f, antialiased: true);
        DrawLine(plate[1], plate[1] + up, IsoTheme.GoldDim, 2f, antialiased: true);
        DrawLine(plate[3], plate[3] + up, IsoTheme.GoldDim, 2f, antialiased: true);
    }

    private void DrawRoomLabel(RoomModule room, Vector2[] plate, float alpha)
    {
        var font = ThemeDB.FallbackFont;
        if (font == null) return;

        var center = Centroid(plate);
        float boxWidth = Mathf.Max(72f, (plate[1].X - plate[3].X) * 0.9f);
        var textOrigin = new Vector2(center.X - boxWidth * 0.5f, center.Y + 2f);

        DrawString(font, textOrigin, room.RoomName,
            HorizontalAlignment.Center, boxWidth, LabelFontSize,
            modulate: Fade(IsoTheme.TextPrimary, alpha));

        float appointment = Mathf.Clamp(room.AppointmentScore, 0f, 100f);

        DrawString(font, textOrigin + new Vector2(0, ScoreFontSize + 4f),
            $"Appt {appointment:F0}",
            HorizontalAlignment.Center, boxWidth, ScoreFontSize,
            modulate: Fade(IsoTheme.GetScoreColor(appointment), alpha));
    }

    // ── Small helpers ──────────────────────────────────────────────────

    private static Color Fade(Color color, float alpha) =>
        new(color.R, color.G, color.B, color.A * Mathf.Clamp(alpha, 0f, 1f));

    /// <summary>Repeat the first point so DrawPolyline closes the shape.</summary>
    private static Vector2[] Close(Vector2[] polygon)
    {
        var closed = new Vector2[polygon.Length + 1];
        Array.Copy(polygon, closed, polygon.Length);
        closed[^1] = polygon[0];
        return closed;
    }

    private static Vector2 Centroid(Vector2[] polygon)
    {
        var sum = Vector2.Zero;
        foreach (var point in polygon) sum += point;
        return sum / Mathf.Max(1, polygon.Length);
    }

    public override string ToString() =>
        $"[IsometricDollhouseView] focus F{_focusedFloor}, zoom {_zoom:F2}, " +
        $"rooms {(HasVenue ? _venue.Rooms.Count : 0)}, " +
        $"selection {(_selected.HasValue ? _selected.Value.ToString() : "none")}";
}
