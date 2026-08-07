using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The people layer of the dollhouse: staff and clients drawn as small
/// stylised figures standing inside rooms.
///
/// This layer deliberately knows nothing about the venue model or about
/// pathfinding. Callers push pawns at grid cells and the layer eases them
/// there; whoever owns the simulation decides who is where and when. Positions
/// come from <see cref="IsoTheme.GridToScreen"/> so pawns land in the same
/// projection as the building underneath them.
///
/// Usage: add as a sibling drawn *after* <see cref="IsometricDollhouseView"/>,
/// mirror that view's transform onto this node, and keep
/// <see cref="FocusedFloor"/> in step with it.
/// </summary>
public partial class VenuePawnLayer : Node2D
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a pawn finishes easing into its target cell.</summary>
    [Signal]
    public delegate void OnPawnArrivedEventHandler(string id, int x, int y, int floor);

    // ── Tuning ─────────────────────────────────────────────────────────

    /// <summary>How quickly a pawn eases toward its cell. Higher is snappier.</summary>
    [Export] public float MoveSpeed { get; set; } = 6f;

    /// <summary>Screen height of a pawn's body, before the head.</summary>
    [Export] public float PawnHeight { get; set; } = 30f;

    /// <summary>Half-width of a pawn's body at the shoulders.</summary>
    [Export] public float PawnWidth { get; set; } = 8f;

    /// <summary>Radius of the ring pawns fan out along when they share a cell.</summary>
    [Export] public float FanRadius { get; set; } = 26f;

    /// <summary>Font size for pawn labels. Zero or less hides them.</summary>
    [Export] public int LabelFontSize { get; set; } = 9;

    /// <summary>Whether to draw the pawn labels at all.</summary>
    [Export] public bool ShowLabels { get; set; } = true;

    // ── Pawn record ────────────────────────────────────────────────────

    private sealed class Pawn
    {
        public string Id = "";
        public Vector3I Cell;
        public bool IsStaff;
        public string Label = "";

        /// <summary>Current interpolated screen position, in this node's local space.</summary>
        public Vector2 Position;

        /// <summary>Where the pawn is easing to, including its fan-out offset.</summary>
        public Vector2 Target;

        public bool Settled;
    }

    private readonly Dictionary<string, Pawn> _pawns = new();
    private readonly List<Pawn> _drawOrder = new();
    private int _focusedFloor;
    private bool _orderDirty = true;

    /// <summary>Number of pawns currently on the layer.</summary>
    public int PawnCount => _pawns.Count;

    /// <summary>
    /// Floor the player is reading. Pawns on other floors dim to
    /// <see cref="IsoTheme.UnfocusedFloorAlpha"/> so they match the building.
    /// </summary>
    public int FocusedFloor
    {
        get => _focusedFloor;
        set
        {
            if (value == _focusedFloor) return;
            _focusedFloor = value;
            QueueRedraw();
        }
    }

    // ── Pawn management ────────────────────────────────────────────────

    /// <summary>
    /// Add a pawn, or re-describe one that already exists under this id.
    /// A new pawn appears at its cell rather than sliding in from the origin.
    /// </summary>
    public void AddPawn(string id, Vector3I cell, bool isStaff, string label)
    {
        if (string.IsNullOrEmpty(id)) return;

        if (_pawns.TryGetValue(id, out var existing))
        {
            existing.Cell = cell;
            existing.IsStaff = isStaff;
            existing.Label = label ?? "";
        }
        else
        {
            _pawns[id] = new Pawn
            {
                Id = id,
                Cell = cell,
                IsStaff = isStaff,
                Label = label ?? "",
                Position = IsoTheme.GridToScreen(cell),
                Target = IsoTheme.GridToScreen(cell),
                Settled = true
            };
        }

        Invalidate();
    }

    /// <summary>Send a pawn to another cell. Unknown ids are ignored.</summary>
    public void MovePawn(string id, Vector3I cell)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_pawns.TryGetValue(id, out var pawn)) return;
        if (pawn.Cell == cell) return;

        pawn.Cell = cell;
        pawn.Settled = false;

        Invalidate();
    }

    /// <summary>Remove a pawn. Unknown ids are ignored.</summary>
    public void RemovePawn(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_pawns.Remove(id)) return;

        Invalidate();
    }

    /// <summary>Drop every pawn — use between nights or on load.</summary>
    public void ClearPawns()
    {
        if (_pawns.Count == 0) return;

        _pawns.Clear();
        Invalidate();
    }

    /// <summary>Whether a pawn with this id is on the layer.</summary>
    public bool HasPawn(string id) => !string.IsNullOrEmpty(id) && _pawns.ContainsKey(id);

    /// <summary>The cell a pawn is heading for, or null if the id is unknown.</summary>
    public Vector3I? GetPawnCell(string id) =>
        !string.IsNullOrEmpty(id) && _pawns.TryGetValue(id, out var pawn) ? pawn.Cell : null;

    /// <summary>Ids of every pawn standing in (or heading for) a cell.</summary>
    public List<string> GetPawnsInCell(Vector3I cell)
    {
        var found = new List<string>();
        foreach (var pawn in _pawns.Values)
            if (pawn.Cell == cell) found.Add(pawn.Id);
        return found;
    }

    private void Invalidate()
    {
        _orderDirty = true;
        RecalculateTargets();
        QueueRedraw();
    }

    /// <summary>
    /// Fan every pawn out around its cell centre so a room holding four people
    /// reads as four people rather than one thick smudge. The ring is squashed
    /// on Y to sit in the isometric plane.
    /// </summary>
    private void RecalculateTargets()
    {
        var groups = new Dictionary<Vector3I, List<Pawn>>();

        foreach (var pawn in _pawns.Values)
        {
            if (!groups.TryGetValue(pawn.Cell, out var bucket))
            {
                bucket = new List<Pawn>();
                groups[pawn.Cell] = bucket;
            }
            bucket.Add(pawn);
        }

        foreach (var (cell, bucket) in groups)
        {
            var center = IsoTheme.GridToScreen(cell);

            // Sorting by id keeps the fan stable frame to frame — otherwise
            // dictionary order shuffles and everyone jitters between slots.
            bucket.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));

            for (int i = 0; i < bucket.Count; i++)
            {
                var pawn = bucket[i];

                if (bucket.Count == 1)
                {
                    pawn.Target = center;
                    continue;
                }

                float angle = Mathf.Tau * i / bucket.Count;
                float radius = FanRadius * Mathf.Min(1f, 0.45f + bucket.Count * 0.12f);

                pawn.Target = center + new Vector2(
                    Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius * 0.5f);
            }
        }
    }

    // ── Motion ─────────────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_pawns.Count == 0) return;

        // Frame-rate independent exponential ease: no pathfinding, just a
        // smooth glide toward wherever the simulation says the pawn belongs.
        float weight = 1f - Mathf.Exp(-Mathf.Max(0.01f, MoveSpeed) * (float)delta);
        bool moved = false;

        foreach (var pawn in _pawns.Values)
        {
            if (pawn.Position.IsEqualApprox(pawn.Target))
            {
                if (!pawn.Settled)
                {
                    pawn.Position = pawn.Target;
                    pawn.Settled = true;
                    moved = true;

                    EmitSignal(SignalName.OnPawnArrived,
                        pawn.Id, pawn.Cell.X, pawn.Cell.Y, pawn.Cell.Z);
                }
                continue;
            }

            pawn.Position = pawn.Position.Lerp(pawn.Target, weight);
            moved = true;
        }

        if (moved) QueueRedraw();
    }

    // ── Drawing ────────────────────────────────────────────────────────

    public override void _Draw()
    {
        if (_pawns.Count == 0) return;

        if (_orderDirty)
        {
            _drawOrder.Clear();
            _drawOrder.AddRange(_pawns.Values);
            _orderDirty = false;
        }

        // Painter's algorithm against the same key the building uses, with the
        // interpolated Y as the tie-break so pawns sharing a cell overlap
        // front-to-back correctly.
        _drawOrder.Sort(static (a, b) =>
        {
            int byCell = IsoTheme.GetDepthKey(a.Cell).CompareTo(IsoTheme.GetDepthKey(b.Cell));
            return byCell != 0 ? byCell : a.Position.Y.CompareTo(b.Position.Y);
        });

        foreach (var pawn in _drawOrder)
            DrawPawn(pawn);
    }

    private void DrawPawn(Pawn pawn)
    {
        float alpha = pawn.Cell.Z == _focusedFloor ? 1f : IsoTheme.UnfocusedFloorAlpha;

        var feet = pawn.Position;
        var body = pawn.IsStaff ? IsoTheme.Gold : IsoTheme.LampWarm;
        var trim = pawn.IsStaff ? IsoTheme.GoldDim : IsoTheme.LampGlow;

        // Contact shadow, so the figure sits on the floor plate instead of
        // floating above it.
        DrawShadow(feet, alpha);

        float half = Mathf.Max(2f, PawnWidth);
        float height = Mathf.Max(8f, PawnHeight);

        // Staff stand taller and square-shouldered; clients are shorter and
        // tapered. Silhouette alone should tell them apart at any zoom.
        float shoulder = pawn.IsStaff ? half : half * 0.75f;
        float hip = pawn.IsStaff ? half * 0.85f : half;
        float torso = pawn.IsStaff ? height : height * 0.86f;

        var torsoPolygon = new[]
        {
            feet + new Vector2(-hip, 0f),
            feet + new Vector2(hip, 0f),
            feet + new Vector2(shoulder, -torso),
            feet + new Vector2(-shoulder, -torso)
        };

        DrawColoredPolygon(torsoPolygon, Fade(body, alpha));
        DrawPolyline(Close(torsoPolygon), Fade(IsoTheme.CellOutline, alpha), 1.4f, antialiased: true);

        // A trim band: a collar on staff, a sash on clients.
        float bandY = pawn.IsStaff ? -torso * 0.82f : -torso * 0.45f;
        DrawLine(
            feet + new Vector2(-shoulder, bandY),
            feet + new Vector2(shoulder, bandY),
            Fade(trim, alpha), 2.5f, antialiased: true);

        float headRadius = half * 0.85f;
        var head = feet + new Vector2(0f, -torso - headRadius * 0.85f);

        DrawCircle(head, headRadius, Fade(body.Lightened(0.25f), alpha));
        DrawCircle(head, headRadius, Fade(IsoTheme.CellOutline, alpha), filled: false, width: 1.4f,
            antialiased: true);

        DrawPawnLabel(pawn, head, headRadius, alpha);
    }

    private void DrawShadow(Vector2 feet, float alpha)
    {
        var shadow = new Vector2[12];
        float rx = Mathf.Max(4f, PawnWidth * 1.3f);
        float ry = rx * 0.5f;

        for (int i = 0; i < shadow.Length; i++)
        {
            float angle = Mathf.Tau * i / shadow.Length;
            shadow[i] = feet + new Vector2(Mathf.Cos(angle) * rx, Mathf.Sin(angle) * ry);
        }

        DrawColoredPolygon(shadow, Fade(IsoTheme.CellOutline, 0.35f * alpha));
    }

    private void DrawPawnLabel(Pawn pawn, Vector2 head, float headRadius, float alpha)
    {
        if (!ShowLabels || LabelFontSize <= 0 || string.IsNullOrEmpty(pawn.Label)) return;

        var font = ThemeDB.FallbackFont;
        if (font == null) return;

        const float boxWidth = 96f;
        var origin = new Vector2(
            head.X - boxWidth * 0.5f,
            head.Y - headRadius - 4f);

        DrawString(font, origin, pawn.Label,
            HorizontalAlignment.Center, boxWidth, LabelFontSize,
            modulate: Fade(pawn.IsStaff ? IsoTheme.TextPrimary : IsoTheme.TextMuted, alpha));
    }

    // ── Small helpers ──────────────────────────────────────────────────

    private static Color Fade(Color color, float alpha) =>
        new(color.R, color.G, color.B, color.A * Mathf.Clamp(alpha, 0f, 1f));

    private static Vector2[] Close(Vector2[] polygon)
    {
        var closed = new Vector2[polygon.Length + 1];
        Array.Copy(polygon, closed, polygon.Length);
        closed[^1] = polygon[0];
        return closed;
    }

    public override string ToString() =>
        $"[VenuePawnLayer] {_pawns.Count} pawns, focus F{_focusedFloor}";
}
