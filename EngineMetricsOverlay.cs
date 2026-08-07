using Godot;
using System;
using System.Collections.Generic;

/// <summary>Developer diagnostic HUD toggleable via ~ key. FPS, Frame Time, Heap, MCP Queue, LLM Latency.</summary>
public partial class EngineMetricsOverlay : CanvasLayer
{
    private bool _visible;
    private float _fps, _frameTimeMs, _llmLatencyMs;
    private long _heapBytes;
    private int _mcpQueueSize;
    private readonly List<float> _fpsHistory = new(), _frameTimeHistory = new();
    private const int MaxHistory = 120;
    private float _elapsed;
    private Control _drawSurface;

    public override void _Ready()
    {
        _drawSurface = new Control();
        _drawSurface.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _drawSurface.MouseFilter = Control.MouseFilterEnum.Ignore;
        _drawSurface.Draw += OnDraw;
        AddChild(_drawSurface);
        Visible = false; _drawSurface.Visible = false;
    }

    public override void _Input(InputEvent evt)
    {
        if (evt is InputEventKey key && key.Keycode == Key.Quoteleft && key.Pressed)
        {
            _visible = !_visible;
            Visible = _visible;
            _drawSurface.Visible = _visible;
        }
    }

    public override void _Process(double delta)
    {
        if (!_visible) return;
        _elapsed += (float)delta;
        if (_elapsed >= 0.25f)
        {
            _elapsed = 0f;
            _fps = (float)Engine.GetFramesPerSecond();
            _frameTimeMs = _fps > 0 ? 1000f / _fps : 0;
            _heapBytes = GC.GetTotalMemory(false);
            var tp = GetTree()?.Root?.FindChild("TaskPoolDispatcher", true, false) as TaskPoolDispatcher;
            _mcpQueueSize = tp?.PendingCount ?? 0;
            _fpsHistory.Add(_fps); if (_fpsHistory.Count > MaxHistory) _fpsHistory.RemoveAt(0);
            _frameTimeHistory.Add(_frameTimeMs); if (_frameTimeHistory.Count > MaxHistory) _frameTimeHistory.RemoveAt(0);
        }
        _drawSurface.QueueRedraw();
    }

    private void OnDraw()
    {
        if (!_visible || _drawSurface == null) return;
        var size = GetViewport().GetVisibleRect().Size;
        float x = size.X - 320, y = 10f; float w = 300f, gh = 60f;
        var font = ThemeDB.FallbackFont;

        _drawSurface.DrawRect(new Rect2(x - 10, 0, 330, size.Y), new Color(0, 0, 0, 0.7f));

        // FPS
        _drawSurface.DrawString(font, new Vector2(x, y), $"FPS: {_fps:F0}", fontSize: 11);
        y += 18;
        _drawSurface.DrawRect(new Rect2(x, y, w, gh), new Color(0.05f, 0.05f, 0.05f), true);
        DrawLineGraph(x, y, w, gh, _fpsHistory, 0, 120, Colors.LimeGreen);
        y += gh + 5;

        // Frame Time
        _drawSurface.DrawString(font, new Vector2(x, y), $"Frame: {_frameTimeMs:F1}ms", fontSize: 11);
        y += 18;
        _drawSurface.DrawRect(new Rect2(x, y, w, gh), new Color(0.05f, 0.05f, 0.05f), true);
        DrawLineGraph(x, y, w, gh, _frameTimeHistory, 0, 50, Colors.Cyan);
        y += gh + 5;

        // Heap
        _drawSurface.DrawString(font, new Vector2(x, y), $"Heap: {_heapBytes / 1024f / 1024f:F1}MB", fontSize: 11);
        y += 25;

        // MCP Queue
        _drawSurface.DrawString(font, new Vector2(x, y), $"MCP Queue: {_mcpQueueSize}", fontSize: 11);
        y += 20;

        // LLM Latency
        _drawSurface.DrawString(font, new Vector2(x, y), $"LLM: {_llmLatencyMs:F1}ms/token", fontSize: 11);
        y += 25;
        _drawSurface.DrawString(font, new Vector2(x, y), "───────────────", fontSize: 11);
        y += 20;
        _drawSurface.DrawString(font, new Vector2(x, y), "~ to hide", fontSize: 11);
    }

    private void DrawLineGraph(float x, float y, float w, float h, List<float> data, float min, float max, Color color)
    {
        if (data.Count < 2) return;
        float range = max - min > 0 ? max - min : 1;
        var pts = new Vector2[data.Count];
        for (int i = 0; i < data.Count; i++)
            pts[i] = new Vector2(x + w * i / (data.Count - 1), y + h - h * Mathf.Clamp((data[i] - min) / range, 0, 1));
        _drawSurface.DrawPolyline(pts, color, 1.5f);
    }

    public void SetLlmLatency(float msPerToken) => _llmLatencyMs = msPerToken;
}
