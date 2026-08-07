using Godot;
using System.Collections.Generic;

public enum RoomAlertType { RoomDirty, SecurityDispute, VIPActive, MaintenanceNeeded, FireHazard, StaffDistress }

public class RoomAlert
{
    /// <summary>Room tile the alert belongs to. Z is the floor index.</summary>
    public Vector3I GridPosition;
    public RoomAlertType Type;
    public float Duration;
    public float TimeRemaining;
    public Label Label;
}

/// <summary>
/// Floating status icons pinned to rooms in the isometric venue view.
///
/// Positions are computed with a flat 2D isometric projection rather than
/// unprojected from a 3D camera — the venue renders as illustrated 2D
/// sprites, so there is no camera to ask.
/// </summary>
public partial class RoomAlertOverlay : Control
{
    private readonly Dictionary<Vector3I, List<RoomAlert>> _alerts = new();

    /// <summary>Screen-space origin the isometric floorplan is drawn from.</summary>
    [Export] public Vector2 ViewOrigin { get; set; } = new(480f, 220f);

    /// <summary>Half-width / half-height of one isometric floor tile, in pixels.</summary>
    [Export] public Vector2 TileHalfExtents { get; set; } = new(48f, 24f);

    /// <summary>Vertical pixel offset between one floor and the next.</summary>
    [Export] public float FloorHeight { get; set; } = 96f;

    private static readonly Dictionary<RoomAlertType, (string icon, Color color)> AlertStyles = new()
    {
        [RoomAlertType.RoomDirty]        = ("🧹", new Color(0.7f, 0.6f, 0.3f)),
        [RoomAlertType.SecurityDispute]  = ("🛡️", new Color(0.9f, 0.3f, 0.2f)),
        [RoomAlertType.VIPActive]        = ("👑", new Color(1f, 0.85f, 0.2f)),
        [RoomAlertType.MaintenanceNeeded] = ("🔧", new Color(0.5f, 0.5f, 0.6f)),
        [RoomAlertType.FireHazard]       = ("🔥", new Color(1f, 0.3f, 0.1f)),
        [RoomAlertType.StaffDistress]    = ("😰", new Color(0.9f, 0.5f, 0.3f)),
    };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        GD.Print("[RoomAlertOverlay] Initialized.");
    }

    public override void _Process(double delta)
    {
        foreach (var kvp in _alerts)
        {
            foreach (var alert in kvp.Value)
            {
                alert.TimeRemaining -= (float)delta;

                if (alert.Label != null)
                {
                    var screenPos = GridToScreen(kvp.Key);
                    alert.Label.Position = screenPos - alert.Label.Size / 2;
                    alert.Label.Scale = Vector2.One;
                }

                if (alert.Label != null)
                {
                    float pulse = 1f + Mathf.Sin((float)Time.GetTicksMsec() / 500f) * 0.15f;
                    alert.Label.Modulate = new Color(1, 1, 1, Mathf.Min(1f, alert.TimeRemaining / 2f));
                    alert.Label.Scale *= pulse;
                }
            }
        }

        foreach (var kvp in _alerts)
            kvp.Value.RemoveAll(a => a.TimeRemaining <= 0);
    }

    public void AddAlert(Vector3I gridPos, RoomAlertType type, float duration = 10f)
    {
        if (!AlertStyles.TryGetValue(type, out var style)) return;

        var label = new Label();
        label.Text = style.icon;
        label.AddThemeFontSizeOverride("font_size", 20);
        label.AddThemeColorOverride("font_color", style.color);
        label.Modulate = style.color;
        AddChild(label);

        var alert = new RoomAlert
        {
            GridPosition = gridPos, Type = type,
            Duration = duration, TimeRemaining = duration,
            Label = label
        };

        if (!_alerts.ContainsKey(gridPos))
            _alerts[gridPos] = new List<RoomAlert>();
        _alerts[gridPos].Add(alert);

        GD.Print($"[RoomAlertOverlay] Alert at {gridPos}: {type} ({duration}s)");
    }

    public void ClearAlert(Vector3I gridPos, RoomAlertType type)
    {
        if (!_alerts.TryGetValue(gridPos, out var alerts)) return;
        foreach (var a in alerts.FindAll(x => x.Type == type))
            a.Label?.QueueFree();
        alerts.RemoveAll(a => a.Type == type);
    }

    public void ClearAll()
    {
        foreach (var kvp in _alerts)
            foreach (var a in kvp.Value)
                a.Label?.QueueFree();
        _alerts.Clear();
    }

    /// <summary>
    /// Flat isometric projection: X/Y are floorplan tiles, Z is the floor
    /// index and simply lifts the icon by one storey.
    /// </summary>
    private Vector2 GridToScreen(Vector3I gridPos)
    {
        float sx = (gridPos.X - gridPos.Y) * TileHalfExtents.X;
        float sy = (gridPos.X + gridPos.Y) * TileHalfExtents.Y - gridPos.Z * FloorHeight;
        return ViewOrigin + new Vector2(sx, sy);
    }
}
