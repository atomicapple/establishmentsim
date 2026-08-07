using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A single data point on the graph.</summary>
public struct GraphPoint
{
    public int Month;
    public double NetRevenue;
    public double Opex;
    public double BribeExpenses;
}

/// <summary>
/// Dynamic line graph rendered with Godot _Draw() primitives.
/// Plots monthly Net Revenue, OPEX, and Bribe Expenses across
/// a rolling 12-month window with hover tooltips for exact values.
/// </summary>
public partial class LedgerGraphRenderer : Control
{
    private readonly List<GraphPoint> _data = new();
    private int _hoveredIndex = -1;
    private Vector2 _mousePos;
    private float _graphLeft = 60f;
    private float _graphRight = 20f;
    private float _graphTop = 20f;
    private float _graphBottom = 40f;

    private static readonly Color RevenueColor = new(0.2f, 0.8f, 0.3f);
    private static readonly Color OpexColor = new(0.9f, 0.3f, 0.2f);
    private static readonly Color BribeColor = new(0.9f, 0.7f, 0.1f);
    private static readonly Color GridColor = new(0.2f, 0.2f, 0.25f);
    private static readonly Color BgColor = new(0.06f, 0.06f, 0.1f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        GD.Print("[LedgerGraph] Initialized.");
    }

    public override void _GuiInput(InputEvent evt)
    {
        if (evt is InputEventMouseMotion mm)
        {
            _mousePos = mm.Position;
            _hoveredIndex = HitTest(_mousePos);
            QueueRedraw();
        }
    }

    /// <summary>Load data from FinancialLedger into the graph.</summary>
    public void LoadFromLedger(FinancialLedger ledger, int currentMonth)
    {
        _data.Clear();
        int startMonth = Math.Max(1, currentMonth - 11);

        for (int m = startMonth; m <= currentMonth; m++)
        {
            int dayStart = (m - 1) * 30 + 1;
            int dayEnd = m * 30;

            double rev = 0, exp = 0, bribes = 0;
            foreach (var entry in ledger.Entries.Where(e => e.Day >= dayStart && e.Day <= dayEnd))
            {
                if (entry.IsRevenue) rev += entry.Amount;
                else
                {
                    exp += entry.Amount;
                    if (entry.Category == "Bribes") bribes += entry.Amount;
                }
            }

            _data.Add(new GraphPoint { Month = m, NetRevenue = rev - exp + bribes, Opex = exp - bribes, BribeExpenses = bribes });
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        var rect = GetRect();
        if (rect.Size.X < 100 || _data.Count < 2) return;

        // Background
        DrawRect(rect, BgColor);

        float graphW = rect.Size.X - _graphLeft - _graphRight;
        float graphH = rect.Size.Y - _graphTop - _graphBottom;

        // Find data range
        double maxVal = Math.Max(1, _data.Max(p => Math.Max(p.NetRevenue, Math.Max(p.Opex, p.BribeExpenses))));
        double minVal = Math.Min(0, _data.Min(p => Math.Min(p.NetRevenue, Math.Min(p.Opex, p.BribeExpenses))));
        double range = maxVal - minVal;
        if (range < 1) range = 1;

        // Grid lines
        for (int i = 0; i <= 4; i++)
        {
            float y = _graphTop + graphH * (i / 4f);
            DrawLine(new Vector2(_graphLeft, y), new Vector2(rect.Size.X - _graphRight, y), GridColor, 0.5f);
            double val = maxVal - range * (i / 4.0);
            DrawString(ThemeDB.FallbackFont, new Vector2(2, y - 6), $"${val / 1000:F0}K", fontSize: 10);
        }

        // Plot lines
        PlotLine(_data.Select(p => p.NetRevenue).ToList(), RevenueColor, graphW, graphH, minVal, range);
        PlotLine(_data.Select(p => p.Opex).ToList(), OpexColor, graphW, graphH, minVal, range);
        PlotLine(_data.Select(p => p.BribeExpenses).ToList(), BribeColor, graphW, graphH, minVal, range);

        // X-axis labels
        for (int i = 0; i < _data.Count; i++)
        {
            float x = _graphLeft + graphW * i / (_data.Count - 1);
            DrawString(ThemeDB.FallbackFont, new Vector2(x - 10, rect.Size.Y - _graphBottom + 8),
                $"M{_data[i].Month}", fontSize: 9);
        }

        // Hover tooltip
        if (_hoveredIndex >= 0 && _hoveredIndex < _data.Count)
        {
            var pt = _data[_hoveredIndex];
            float hx = _graphLeft + graphW * _hoveredIndex / (_data.Count - 1);
            DrawTooltip(new Vector2(hx, _graphTop - 30), pt);
        }

        // Legend
        DrawLegend(new Vector2(_graphLeft + 10, 4));
    }

    private void PlotLine(List<double> values, Color color, float graphW, float graphH, double minVal, double range)
    {
        if (values.Count < 2) return;

        var points = new Vector2[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            float x = _graphLeft + graphW * i / (values.Count - 1);
            float y = _graphTop + graphH * (float)(1 - (values[i] - minVal) / range);
            points[i] = new Vector2(x, y);
            DrawCircle(points[i], 3, color);
        }

        DrawPolyline(points, color, 2f);
    }

    private void DrawTooltip(Vector2 pos, GraphPoint pt)
    {
        var font = ThemeDB.FallbackFont;
        float w = 180, h = 55;
        var r = new Rect2(pos.X - w / 2, pos.Y - h, w, h);

        DrawRect(r, new Color(0.05f, 0.05f, 0.1f, 0.95f));
        DrawRect(r, new Color(0.4f, 0.4f, 0.5f), false);

        DrawString(font, r.Position + new Vector2(6, 14), $"Month {pt.Month}", fontSize: 11);
        DrawString(font, r.Position + new Vector2(6, 28),
            $"Net Revenue: ${pt.NetRevenue:F0}", fontSize: 10);
        DrawString(font, r.Position + new Vector2(6, 40),
            $"OPEX: ${pt.Opex:F0}", fontSize: 10);
        DrawString(font, r.Position + new Vector2(100, 40),
            $"Bribes: ${pt.BribeExpenses:F0}", fontSize: 10);
    }

    private void DrawLegend(Vector2 pos)
    {
        var font = ThemeDB.FallbackFont;
        float x = pos.X;
        DrawCircle(new Vector2(x + 4, pos.Y + 5), 4, RevenueColor);
        DrawString(font, new Vector2(x + 12, pos.Y), "Net Revenue", fontSize: 10);
        x += 100;
        DrawCircle(new Vector2(x + 4, pos.Y + 5), 4, OpexColor);
        DrawString(font, new Vector2(x + 12, pos.Y), "OPEX", fontSize: 10);
        x += 70;
        DrawCircle(new Vector2(x + 4, pos.Y + 5), 4, BribeColor);
        DrawString(font, new Vector2(x + 12, pos.Y), "Bribes", fontSize: 10);
    }

    private int HitTest(Vector2 pos)
    {
        if (_data.Count < 2) return -1;
        var rect = GetRect();
        float graphW = rect.Size.X - _graphLeft - _graphRight;
        for (int i = 0; i < _data.Count; i++)
        {
            float x = _graphLeft + graphW * i / (_data.Count - 1);
            if (Math.Abs(pos.X - x) < 15) return i;
        }
        return -1;
    }

    public override string ToString() =>
        $"[LedgerGraph] Points={_data.Count}";
}
