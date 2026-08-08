using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The people in the building: staff and clients as real rigged models
/// standing in their rooms.
///
/// Replaces the 2D VenuePawnLayer. Positions come from
/// <see cref="VenueSpace"/> so pawns land in the same rooms the geometry
/// draws, and models come from <see cref="CharacterLibrary"/>, which parses
/// the .glb files at runtime because they carry no .import sidecars.
///
/// A pawn whose model fails to load falls back to a simple capsule rather
/// than vanishing — a missing art file should be visible, not silent.
/// </summary>
public partial class VenuePawns3D : Node3D
{
    /// <summary>One person in the building.</summary>
    private sealed class Pawn
    {
        public string Id;
        public Node3D Root;
        public AnimationPlayer Player;
        public Label3D Label;

        public Vector3 Target;
        public bool IsStaff;
        public int Floor;
        public CharacterAnimation State = CharacterAnimation.Idle;
    }

    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnPawnArrivedEventHandler(string id);

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Metres per second a pawn moves toward its target.</summary>
    [Export] public float MoveSpeed { get; set; } = 2.4f;

    /// <summary>How far apart pawns sharing a room stand.</summary>
    [Export] public float FanRadius { get; set; } = 0.55f;

    /// <summary>Show name labels above pawns.</summary>
    [Export] public bool ShowLabels { get; set; } = true;

    /// <summary>Height of the fallback capsule, in metres.</summary>
    [Export] public float FallbackHeight { get; set; } = 1.7f;

    // ── State ──────────────────────────────────────────────────────────

    private readonly Dictionary<string, Pawn> _pawns = new();
    private VenueBuilding _venue;
    private int _focusedFloor;

    public int PawnCount => _pawns.Count;

    public int FocusedFloor
    {
        get => _focusedFloor;
        set
        {
            if (_focusedFloor == value) return;
            _focusedFloor = value;
            ApplyFloorVisibility();
        }
    }

    public void Bind(VenueBuilding venue) => _venue = venue;

    // ── Membership ─────────────────────────────────────────────────────

    /// <summary>Add a person to the building. Re-describes an existing id.</summary>
    public void AddPawn(string id, Vector3I cell, bool isStaff, string label)
    {
        if (string.IsNullOrEmpty(id)) return;

        if (_pawns.TryGetValue(id, out var existing))
        {
            existing.IsStaff = isStaff;
            if (existing.Label != null) existing.Label.Text = label ?? "";
            MovePawn(id, cell);
            return;
        }

        var root = new Node3D { Name = $"Pawn_{id}" };
        AddChild(root);

        var model = BuildBody(id, isStaff, root);

        var pawn = new Pawn
        {
            Id = id,
            Root = root,
            Player = model,
            IsStaff = isStaff,
            Floor = cell.Z,
            Target = ResolveStandingPosition(id, cell)
        };

        if (ShowLabels)
        {
            pawn.Label = new Label3D
            {
                Text = label ?? "",
                FontSize = 48,
                PixelSize = 0.0022f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = isStaff ? IsoTheme.Gold : IsoTheme.TextPrimary,
                OutlineSize = 12,
                Position = new Vector3(0, FallbackHeight + 0.28f, 0),
                NoDepthTest = true
            };

            root.AddChild(pawn.Label);
        }

        root.Position = pawn.Target;
        _pawns[id] = pawn;

        Play(pawn, CharacterAnimation.Idle);
        ApplyFloorVisibility();
    }

    /// <summary>
    /// Build the visible body. Tries a rigged model first; a capsule stands
    /// in when the library has nothing, so the pawn is still on screen.
    /// </summary>
    private AnimationPlayer BuildBody(string id, bool isStaff, Node3D root)
    {
        if (CharacterLibrary.Instance != null)
        {
            // Staff and clients draw from separate pools, so the house's own
            // people are never dressed as patrons.
            var modelId = CharacterLibrary.Instance.PickModelFor(id, forClient: !isStaff);
            var model = modelId == null ? null : CharacterLibrary.Instance.Instantiate(modelId);

            if (model != null)
            {
                root.AddChild(model);
                return CharacterLibrary.FindAnimationPlayer(model);
            }
        }

        root.AddChild(BuildCapsule(isStaff));
        return null;
    }

    private MeshInstance3D BuildCapsule(bool isStaff)
    {
        var mesh = new CapsuleMesh
        {
            Radius = 0.22f,
            Height = FallbackHeight
        };

        var material = new StandardMaterial3D
        {
            AlbedoColor = isStaff ? IsoTheme.Gold : new Color("8a7f9c"),
            Roughness = 0.8f
        };

        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            Position = new Vector3(0, FallbackHeight * 0.5f, 0)
        };
    }

    public void MovePawn(string id, Vector3I cell)
    {
        if (!_pawns.TryGetValue(id, out var pawn)) return;

        pawn.Floor = cell.Z;
        pawn.Target = ResolveStandingPosition(id, cell);

        Play(pawn, CharacterAnimation.Walk);
        ApplyFloorVisibility();
    }

    public void RemovePawn(string id)
    {
        if (!_pawns.TryGetValue(id, out var pawn)) return;

        pawn.Root?.QueueFree();
        _pawns.Remove(id);
    }

    public void ClearPawns()
    {
        foreach (var pawn in _pawns.Values) pawn.Root?.QueueFree();
        _pawns.Clear();
    }

    public bool HasPawn(string id) => !string.IsNullOrEmpty(id) && _pawns.ContainsKey(id);

    /// <summary>Put a pawn into a specific animation state, e.g. talking during an encounter.</summary>
    public void SetPawnState(string id, CharacterAnimation state)
    {
        if (_pawns.TryGetValue(id, out var pawn)) Play(pawn, state);
    }

    // ── Placement ──────────────────────────────────────────────────────

    /// <summary>
    /// Where a pawn stands inside a room. Occupants are fanned around the
    /// room centre so several people in one suite do not share a spot.
    /// </summary>
    private Vector3 ResolveStandingPosition(string id, Vector3I cell)
    {
        var room = _venue?.GetRoom(cell);

        var centre = room != null
            ? VenueSpace.RoomCenter(room.GridPosition, room.Size)
            : VenueSpace.CellCenter(cell);

        // Deterministic offset from the id, so a pawn does not jump around
        // when its neighbours change.
        var hash = 0;
        foreach (var c in id) hash = (hash * 31 + c) & 0x7FFFFFFF;

        var angle = hash % 360 * Mathf.Pi / 180f;

        return centre + new Vector3(
            Mathf.Cos(angle) * FanRadius, 0f, Mathf.Sin(angle) * FanRadius);
    }

    // ── Frame update ───────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_pawns.Count == 0) return;

        var step = MoveSpeed * (float)delta;

        foreach (var pawn in _pawns.Values)
        {
            if (pawn.Root == null) continue;

            var current = pawn.Root.Position;
            var offset = pawn.Target - current;
            var distance = offset.Length();

            if (distance < 0.02f)
            {
                if (pawn.State == CharacterAnimation.Walk)
                {
                    Play(pawn, CharacterAnimation.Idle);
                    EmitSignal(SignalName.OnPawnArrived, pawn.Id);
                }
                continue;
            }

            pawn.Root.Position = current + offset.Normalized() * Mathf.Min(step, distance);

            // Face the direction of travel, keeping upright.
            var flat = new Vector3(offset.X, 0f, offset.Z);
            if (flat.LengthSquared() > 0.0001f)
                pawn.Root.LookAt(pawn.Root.Position + flat.Normalized(), Vector3.Up);
        }
    }

    // ── Presentation ───────────────────────────────────────────────────

    private static void Play(Pawn pawn, CharacterAnimation state)
    {
        pawn.State = state;
        if (pawn.Player == null) return;

        var clip = CharacterLibrary.ResolveAnimation(pawn.Player, state);

        if (string.IsNullOrEmpty(clip))
        {
            // The rig has nothing for this state. Freeze a locomotion clip on
            // an early frame so the figure stands still, rather than running
            // on the spot or collapsing into a T-pose. Meshy's current
            // exports only carry Running and Walking, so this is the normal
            // path for idle, not an edge case.
            var pose = CharacterLibrary.ResolveStandInPose(pawn.Player);
            if (string.IsNullOrEmpty(pose)) return;

            pawn.Player.Play(pose);
            pawn.Player.Seek(0.0, update: true);
            pawn.Player.SpeedScale = 0f;
            return;
        }

        pawn.Player.SpeedScale = 1f;

        if (pawn.Player.CurrentAnimation == clip) return;

        pawn.Player.Play(clip);

        var animation = pawn.Player.GetAnimation(clip);
        if (animation != null) animation.LoopMode = Animation.LoopModeEnum.Linear;
    }

    /// <summary>
    /// Hide pawns on floors the camera has cut away, so people do not appear
    /// to stand on top of the building.
    /// </summary>
    private void ApplyFloorVisibility()
    {
        foreach (var pawn in _pawns.Values)
        {
            if (pawn.Root == null) continue;

            var opacity = VenueSpace.GetFloorOpacity(pawn.Floor, _focusedFloor);
            pawn.Root.Visible = opacity > 0.2f;

            if (pawn.Label != null) pawn.Label.Visible = pawn.Floor == _focusedFloor;
        }
    }

    public override string ToString() => $"[VenuePawns3D] {_pawns.Count} pawns";
}
