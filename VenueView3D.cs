using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The 3D dollhouse: an orthographic camera at a fixed isometric angle
/// looking at a cutaway building.
///
/// Replaces the 2D IsometricDollhouseView. Geometry comes from
/// <see cref="VenueRoomBuilder"/>, placement from <see cref="VenueSpace"/>,
/// and colour from <see cref="IsoTheme"/>'s palette — the projection half of
/// IsoTheme is 2D and no longer used.
///
/// Floors above the focused one are cut away almost entirely. Without that
/// an upper storey's slab simply covers everything beneath it, and the
/// dollhouse read is the whole point of the view.
/// </summary>
public partial class VenueView3D : Node3D
{
    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnRoomClickedEventHandler(int x, int y, int floor);

    [Signal]
    public delegate void OnEmptyTileClickedEventHandler(int x, int y, int floor);

    [Signal]
    public delegate void OnFocusedFloorChangedEventHandler(int floor);

    // ── Configuration ──────────────────────────────────────────────────

    [Export] public float PanSpeed { get; set; } = 9f;
    [Export] public float ZoomStep { get; set; } = 1.12f;
    [Export] public bool ShowBuildableTiles { get; set; } = true;

    // ── State ──────────────────────────────────────────────────────────

    private VenueBuilding _venue;
    private Camera3D _camera;
    private Node3D _geometry;
    private Node3D _ghosts;
    private Node3D _highlight;

    private readonly Dictionary<int, List<StandardMaterial3D>> _floorMaterials = new();
    private readonly Dictionary<int, List<GeometryInstance3D>> _floorGeometry = new();
    private readonly Dictionary<int, List<Label3D>> _floorLabels = new();
    private readonly Dictionary<int, List<Light3D>> _floorLights = new();

    private int _focusedFloor;
    private Vector3 _cameraPivot;
    private float _cameraSize = VenueSpace.DefaultCameraSize;
    private bool _dragging;

    public VenueBuilding Venue => _venue;
    public Vector3I? SelectedRoomOrigin { get; private set; }
    public Camera3D Camera => _camera;

    public int FocusedFloor
    {
        get => _focusedFloor;
        set
        {
            if (_venue == null) { _focusedFloor = value; return; }

            var clamped = Mathf.Clamp(value, _venue.LowestFloor, _venue.HighestFloor);
            if (clamped == _focusedFloor) return;

            _focusedFloor = clamped;
            ApplyFloorVisibility();
            RebuildGhosts();

            EmitSignal(SignalName.OnFocusedFloorChanged, _focusedFloor);
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildEnvironment();

        _geometry = new Node3D { Name = "Geometry" };
        AddChild(_geometry);

        _ghosts = new Node3D { Name = "Ghosts" };
        AddChild(_ghosts);
    }

    public override void _ExitTree() => VenueRoomBuilder.ClearModelCache();

    /// <summary>
    /// Camera, key light and environment. An orthographic camera has no
    /// perspective falloff, so its distance only needs to clear the geometry
    /// — framing is entirely a function of Size.
    /// </summary>
    private void BuildEnvironment()
    {
        _camera = new Camera3D
        {
            Name = "Camera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = _cameraSize,
            Near = 0.1f,
            Far = 400f,
            Current = true
        };

        AddChild(_camera);
        PlaceCamera();

        // Near-white key. An amber key at full strength multiplies against
        // the room albedo and turns every surface orange, which flattened the
        // crimson/purple distinction between room types into one colour. The
        // warmth belongs in the lamps inside the rooms, not the sun.
        var key = new DirectionalLight3D
        {
            Name = "KeyLight",
            LightColor = new Color("fff2e0"),
            LightEnergy = 1.0f,
            ShadowEnabled = true
        };

        key.Basis = new Basis(Vector3.Up, Mathf.DegToRad(-35f)) *
                    new Basis(Vector3.Right, Mathf.DegToRad(-52f));

        AddChild(key);

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = IsoTheme.Backdrop,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("4a4560"),
            AmbientLightEnergy = 0.55f,

            // Linear rather than Filmic: the filmic curve desaturates and
            // lifts the midtones, which pushed the whole palette toward a
            // uniform orange and lost the room-type colour coding.
            TonemapMode = Godot.Environment.ToneMapper.Linear
        };

        AddChild(new WorldEnvironment { Name = "Environment", Environment = environment });
    }

    // ── Binding ────────────────────────────────────────────────────────

    public void Bind(VenueBuilding venue)
    {
        _venue = venue;

        if (_venue != null)
            _focusedFloor = Mathf.Clamp(_focusedFloor, _venue.LowestFloor, _venue.HighestFloor);

        Refresh();
    }

    /// <summary>Rebuild all room geometry from the model.</summary>
    public void Refresh()
    {
        if (_geometry == null) return;

        foreach (var child in _geometry.GetChildren()) child.QueueFree();

        _floorMaterials.Clear();
        _floorGeometry.Clear();
        _floorLabels.Clear();
        _floorLights.Clear();

        _highlight = null;

        if (_venue != null)
        {
            foreach (var room in _venue.Rooms.Values.Where(r => r != null))
            {
                var node = VenueRoomBuilder.BuildRoom(room);
                if (node == null) continue;

                _geometry.AddChild(node);
                RegisterFadeTargets(room.Floor, node);
            }
        }

        // A stale selection can outlive a rebuild if the room was removed.
        if (SelectedRoomOrigin.HasValue && _venue?.GetRoomByOrigin(SelectedRoomOrigin.Value) == null)
            SelectedRoomOrigin = null;

        ApplyFloorVisibility();
        RebuildGhosts();
        RebuildHighlight();
    }

    private void RegisterFadeTargets(int floor, Node node)
    {
        if (!_floorMaterials.ContainsKey(floor))
        {
            _floorMaterials[floor] = new List<StandardMaterial3D>();
            _floorGeometry[floor] = new List<GeometryInstance3D>();
            _floorLabels[floor] = new List<Label3D>();
            _floorLights[floor] = new List<Light3D>();
        }

        VenueRoomBuilder.CollectFadeTargets(
            node, _floorMaterials[floor], _floorGeometry[floor],
            _floorLabels[floor], _floorLights[floor]);
    }

    // ── Floor cutaway ──────────────────────────────────────────────────

    /// <summary>
    /// Apply per-floor opacity. Procedural pieces fade through their material
    /// alpha; imported glb meshes keep their own textured materials and fade
    /// through GeometryInstance3D.Transparency instead.
    /// </summary>
    private void ApplyFloorVisibility()
    {
        foreach (var floor in _floorMaterials.Keys)
        {
            var opacity = VenueSpace.GetFloorOpacity(floor, _focusedFloor);
            var hidden = opacity <= 0.02f;

            foreach (var material in _floorMaterials[floor])
            {
                var colour = material.AlbedoColor;
                material.AlbedoColor = new Color(colour.R, colour.G, colour.B, opacity);
            }

            foreach (var geometry in _floorGeometry[floor])
            {
                geometry.Transparency = 1f - opacity;
                geometry.Visible = !hidden;
            }

            // Labels and lights only belong to the floor being read; leaving
            // lamps burning on a cut-away storey lights rooms from nowhere.
            foreach (var label in _floorLabels[floor])
                label.Visible = floor == _focusedFloor;

            foreach (var light in _floorLights[floor])
                light.Visible = !hidden;
        }
    }

    public void FocusUp() => FocusedFloor = _focusedFloor + 1;
    public void FocusDown() => FocusedFloor = _focusedFloor - 1;

    // ── Ghost tiles ────────────────────────────────────────────────────

    private void RebuildGhosts()
    {
        if (_ghosts == null) return;

        foreach (var child in _ghosts.GetChildren()) child.QueueFree();
        if (!ShowBuildableTiles || _venue == null) return;

        foreach (var tile in _venue.GetEmptyTiles(_focusedFloor))
        {
            var ghost = VenueRoomBuilder.BuildGhostTile(tile);
            if (ghost != null) _ghosts.AddChild(ghost);
        }
    }

    // ── Selection ──────────────────────────────────────────────────────

    public void SelectRoom(Vector3I? origin)
    {
        SelectedRoomOrigin = origin;
        RebuildHighlight();
    }

    public void ClearSelection() => SelectRoom(null);

    private void RebuildHighlight()
    {
        _highlight?.QueueFree();
        _highlight = null;

        if (!SelectedRoomOrigin.HasValue || _venue == null) return;

        var room = _venue.GetRoomByOrigin(SelectedRoomOrigin.Value);
        if (room == null) return;

        var footprint = VenueSpace.RoomFootprint(room.Size);
        var centre = VenueSpace.RoomCenter(room.GridPosition, room.Size);

        var box = VenueRoomBuilder.MakeBox(
            centre + new Vector3(0, 0.02f, 0),
            new Vector3(footprint.X + 0.06f, 0.04f, footprint.Y + 0.06f),
            IsoTheme.Gold);

        _highlight = box;
        _geometry.AddChild(box);
    }

    // ── Input ──────────────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton button when button.Pressed:
                HandleMouseButton(button);
                break;

            case InputEventMouseMotion motion when _dragging:
                PanByScreenDelta(motion.Relative);
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton button)
    {
        switch (button.ButtonIndex)
        {
            case MouseButton.Left:
                PickAt(button.Position);
                break;

            case MouseButton.Middle:
                _dragging = true;
                break;

            case MouseButton.WheelUp:
                SetCameraSize(_cameraSize / ZoomStep);
                break;

            case MouseButton.WheelDown:
                SetCameraSize(_cameraSize * ZoomStep);
                break;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Middle })
            _dragging = false;
    }

    public override void _Process(double delta)
    {
        var move = Vector2.Zero;

        if (Input.IsPhysicalKeyPressed(Key.Left)) move.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.Right)) move.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.Up)) move.Y -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.Down)) move.Y += 1f;

        if (move == Vector2.Zero) return;

        PanByWorld(move.Normalized() * PanSpeed * (float)delta);
    }

    /// <summary>
    /// Raycast into the floor slabs. Hits carry the floor and room origin as
    /// metadata, so the room is resolved without a second spatial lookup.
    /// </summary>
    private void PickAt(Vector2 screenPosition)
    {
        if (_camera == null || _venue == null) return;

        var from = _camera.ProjectRayOrigin(screenPosition);
        var to = from + _camera.ProjectRayNormal(screenPosition) * 1000f;

        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null) return;

        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollisionMask = VenueRoomBuilder.PickCollisionLayer;

        var hit = space.IntersectRay(query);

        if (hit.Count > 0 && hit.TryGetValue("collider", out var colliderValue) &&
            colliderValue.As<Node>() is { } collider)
        {
            var origin = FindOriginMeta(collider);
            if (origin.HasValue)
            {
                var room = _venue.GetRoomByOrigin(origin.Value);
                if (room != null && room.Floor == _focusedFloor)
                {
                    SelectRoom(origin);
                    EmitSignal(SignalName.OnRoomClicked,
                        origin.Value.X, origin.Value.Y, origin.Value.Z);
                    return;
                }
            }
        }

        // Nothing solid: fall through to the focused floor's plane so empty
        // tiles are still clickable for building.
        var plane = new Plane(Vector3.Up, VenueSpace.FloorY(_focusedFloor));
        var point = plane.IntersectsRay(from, _camera.ProjectRayNormal(screenPosition));

        if (point == null) return;

        var tile = VenueSpace.WorldToCell(point.Value, _focusedFloor);
        if (!_venue.IsInBounds(tile)) return;

        ClearSelection();
        EmitSignal(SignalName.OnEmptyTileClicked, tile.X, tile.Y, tile.Z);
    }

    private static Vector3I? FindOriginMeta(Node node)
    {
        for (var current = node; current != null; current = current.GetParent())
        {
            if (current.HasMeta(VenueRoomBuilder.OriginMetaKey))
                return (Vector3I)current.GetMeta(VenueRoomBuilder.OriginMetaKey);
        }

        return null;
    }

    // ── Camera ─────────────────────────────────────────────────────────

    private void PlaceCamera()
    {
        if (_camera == null) return;

        var basis = VenueSpace.CameraBasis();
        _camera.Basis = basis;
        _camera.Position = _cameraPivot + basis.Z * VenueSpace.CameraDistance;
    }

    public void SetCameraSize(float size)
    {
        _cameraSize = Mathf.Clamp(size, VenueSpace.MinCameraSize, VenueSpace.MaxCameraSize);
        if (_camera != null) _camera.Size = _cameraSize;
    }

    /// <summary>Move the camera across its own screen plane, not world axes.</summary>
    private void PanByScreenDelta(Vector2 delta)
    {
        var basis = VenueSpace.CameraBasis();
        var scale = _cameraSize / 600f;

        PanByWorld(-basis.X * delta.X * scale + -basis.Y * delta.Y * scale);
    }

    private void PanByWorld(Vector2 planar) =>
        PanByWorld(new Vector3(planar.X, 0f, planar.Y));

    private void PanByWorld(Vector3 offset)
    {
        _cameraPivot += offset;
        PlaceCamera();
    }

    /// <summary>Frame the whole building, adjusting for how tall it has grown.</summary>
    public void CenterOnBuilding()
    {
        if (_venue == null || _venue.Rooms.Count == 0)
        {
            _cameraPivot = Vector3.Zero;
            PlaceCamera();
            return;
        }

        var bounds = GetBuildingBoundsXZ();
        var midFloor = (_venue.LowestFloor + _venue.HighestFloor) * 0.5f;

        _cameraPivot = new Vector3(
            bounds.Position.X + bounds.Size.X * 0.5f,
            midFloor * VenueSpace.FloorHeight + VenueSpace.WallHeight * 0.5f,
            bounds.Position.Y + bounds.Size.Y * 0.5f);

        // Enough vertical room for the whole stack plus the plan's own depth.
        var floors = Mathf.Max(1, _venue.FloorCount);
        var neededHeight = floors * VenueSpace.FloorHeight + VenueSpace.WallHeight * 2f;
        var neededWidth = Mathf.Max(bounds.Size.X, bounds.Size.Y) * 1.4f;

        SetCameraSize(Mathf.Max(neededHeight, neededWidth) * 1.05f);
        PlaceCamera();
    }

    /// <summary>World-space XZ extent of the built floors.</summary>
    public Rect2 GetBuildingBoundsXZ()
    {
        if (_venue == null) return new Rect2();

        var width = _venue.FloorWidth * VenueSpace.TileSize;
        var depth = _venue.FloorDepth * VenueSpace.TileSize;

        return new Rect2(0f, 0f, width, depth);
    }

    public override string ToString() =>
        $"[VenueView3D] floor {_focusedFloor}, size {_cameraSize:F1}";
}
