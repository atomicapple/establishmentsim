using Godot;
using System;

/// <summary>
/// Reusable full-screen overlay that dims the entire screen except
/// a pulsing cutout mask around a target Control node. Everything
/// outside the cutout is unclickable. Used by the tutorial system.
/// </summary>
public partial class UIHighlightMask : Control
{
    [Signal] public delegate void OnTargetFocusedEventHandler(string nodeName);

    // ── State ───────────────────────────────────────────────────────
    private Control _targetNode;
    private Rect2 _cutoutRect;
    private float _pulseTime;
    private bool _isActive;

    // ── Styling ─────────────────────────────────────────────────────
    public Color DimColor { get; set; } = new(0, 0, 0, 0.55f);
    public Color BorderColor { get; set; } = new(1f, 0.85f, 0.2f, 0.9f);
    public float BorderWidth { get; set; } = 3f;
    public float CornerRadius { get; set; } = 8f;
    public float CutoutPadding { get; set; } = 10f;
    public float PulseSpeed { get; set; } = 3f;
    public float PulseMinAlpha { get; set; } = 0.55f;
    public float PulseMaxAlpha { get; set; } = 0.85f;

    // ── Cached drawing colors (updated each frame for pulse) ────────
    private Color _currentBorderColor;

    public bool IsActive => _isActive;
    public Control TargetNode => _targetNode;

    public override void _Ready()
    {
        MouseFilter = Control.MouseFilterEnum.Stop; // block all clicks
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        Visible = false;
        GD.Print("[UIHighlightMask] Ready.");
    }

    public override void _Process(double delta)
    {
        if (!_isActive || _targetNode == null) return;

        // Update cutout position (target may have moved)
        UpdateCutoutRect();

        // Pulse animation
        _pulseTime += (float)delta * PulseSpeed;
        float alpha = Mathf.Lerp(PulseMinAlpha, PulseMaxAlpha,
            (Mathf.Sin(_pulseTime) + 1f) / 2f);
        _currentBorderColor = new Color(BorderColor.R, BorderColor.G, BorderColor.B, alpha);

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_isActive) return;

        var size = GetViewport().GetVisibleRect().Size;

        // ── Draw full-screen dim overlay ────────────────────────────
        // We draw the dim as four rectangles around the cutout,
        // rather than using a shader, for maximum compatibility.

        Rect2 cutout = _cutoutRect;

        // Top bar
        DrawRect(new Rect2(0, 0, size.X, cutout.Position.Y), DimColor);
        // Bottom bar
        DrawRect(new Rect2(0, cutout.End.Y, size.X, size.Y - cutout.End.Y), DimColor);
        // Left bar
        DrawRect(new Rect2(0, cutout.Position.Y, cutout.Position.X, cutout.Size.Y), DimColor);
        // Right bar
        DrawRect(new Rect2(cutout.End.X, cutout.Position.Y, size.X - cutout.End.X, cutout.Size.Y), DimColor);

        // ── Draw pulsing border around cutout ───────────────────────
        float r = CornerRadius;
        float bw = BorderWidth;

        // Top border
        DrawRect(new Rect2(cutout.Position.X, cutout.Position.Y - bw, cutout.Size.X, bw), _currentBorderColor);
        // Bottom border
        DrawRect(new Rect2(cutout.Position.X, cutout.End.Y, cutout.Size.X, bw), _currentBorderColor);
        // Left border
        DrawRect(new Rect2(cutout.Position.X - bw, cutout.Position.Y, bw, cutout.Size.Y), _currentBorderColor);
        // Right border
        DrawRect(new Rect2(cutout.End.X, cutout.Position.Y, bw, cutout.Size.Y), _currentBorderColor);

        // ── Draw corner accents (thicker) ───────────────────────────
        float cornerLen = 16f;
        float cornerW = 4f;
        var accentColor = new Color(_currentBorderColor.R, _currentBorderColor.G, _currentBorderColor.B, 1f);

        // Top-left
        DrawRect(new Rect2(cutout.Position.X - bw, cutout.Position.Y - bw, cornerLen, cornerW), accentColor);
        DrawRect(new Rect2(cutout.Position.X - bw, cutout.Position.Y - bw, cornerW, cornerLen), accentColor);
        // Top-right
        DrawRect(new Rect2(cutout.End.X - cornerLen, cutout.Position.Y - bw, cornerLen, cornerW), accentColor);
        DrawRect(new Rect2(cutout.End.X, cutout.Position.Y - bw, cornerW, cornerLen), accentColor);
        // Bottom-left
        DrawRect(new Rect2(cutout.Position.X - bw, cutout.End.Y, cornerLen, cornerW), accentColor);
        DrawRect(new Rect2(cutout.Position.X - bw, cutout.End.Y - cornerLen, cornerW, cornerLen), accentColor);
        // Bottom-right
        DrawRect(new Rect2(cutout.End.X - cornerLen, cutout.End.Y, cornerLen, cornerW), accentColor);
        DrawRect(new Rect2(cutout.End.X, cutout.End.Y - cornerLen, cornerW, cornerLen), accentColor);

        // ── Draw arrow pointing to target (bottom-center of cutout) ──
        Vector2 arrowBase = new(cutout.GetCenter().X, cutout.End.Y + 6f);
        float arrowH = 14f;
        float arrowHalfW = 8f;
        var arrowPts = new Vector2[]
        {
            new(arrowBase.X - arrowHalfW, arrowBase.Y),
            new(arrowBase.X + arrowHalfW, arrowBase.Y),
            new(arrowBase.X, arrowBase.Y + arrowH)
        };
        DrawPolygon(arrowPts, new Color[] { accentColor, accentColor, accentColor });
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>Focus the mask on a specific target control.</summary>
    public void FocusOnNode(Control targetNode)
    {
        _targetNode = targetNode;
        UpdateCutoutRect();
        _isActive = true;
        Visible = true;
        _pulseTime = 0f;

        EmitSignal(SignalName.OnTargetFocused, targetNode?.Name ?? "null");
        GD.Print($"[UIHighlightMask] Focused on: {targetNode?.Name ?? "null"}");
    }

    /// <summary>Focus on a node found by name in the scene tree.</summary>
    public void FocusOnNodeName(string nodeName)
    {
        var target = GetTree()?.Root?.FindChild(nodeName, true, false) as Control;
        if (target != null)
            FocusOnNode(target);
        else
            GD.Print($"[UIHighlightMask] Node '{nodeName}' not found.");
    }

    /// <summary>Clear the highlight and hide the mask.</summary>
    public void Clear()
    {
        _isActive = false;
        _targetNode = null;
        Visible = false;
        QueueRedraw();
    }

    /// <summary>Check if a screen position falls within the cutout (clickable zone).</summary>
    public bool IsInCutout(Vector2 screenPos)
    {
        return _isActive && _cutoutRect.HasPoint(screenPos);
    }

    public override void _GuiInput(InputEvent evt)
    {
        // Only allow clicks within the cutout zone
        if (evt is InputEventMouseButton mb && mb.Pressed)
        {
            Vector2 pos = mb.Position;
            if (!IsInCutout(pos) && !IsInCutout(pos + _cutoutRect.Position))
            {
                // Click outside cutout — consume and block
                AcceptEvent();
            }
        }
    }

    // ── Internal ────────────────────────────────────────────────────────

    private void UpdateCutoutRect()
    {
        if (_targetNode == null || !IsInstanceValid(_targetNode))
        {
            _cutoutRect = new Rect2(100, 100, 200, 100); // fallback
            return;
        }

        var globalPos = _targetNode.GlobalPosition;
        var size = _targetNode.Size;

        _cutoutRect = new Rect2(
            globalPos.X - CutoutPadding,
            globalPos.Y - CutoutPadding,
            size.X + CutoutPadding * 2,
            size.Y + CutoutPadding * 2);
    }

    public override string ToString() =>
        $"[UIHighlightMask] Active={_isActive} Target={_targetNode?.Name ?? "none"}";
}
