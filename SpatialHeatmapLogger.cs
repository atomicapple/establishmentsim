using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>A single spatial event logged to the heatmap.</summary>
public class SpatialEvent
{
    public int Day { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public string EventType { get; set; }  // "room_placed", "client_bottleneck", "crime_incident", "fight", "raid"
    public string Detail { get; set; }
}

/// <summary>
/// Logs player spatial layout choices and operational bottlenecks.
/// Records room placements, client movement issues, and crime locations.
/// Exports aggregated spatial density maps to CSV for balance analysis.
/// </summary>
public partial class SpatialHeatmapLogger : Node
{
    private readonly List<SpatialEvent> _events = new();
    private int[,] _roomDensity;
    private int[,] _bottleneckDensity;
    private int[,] _crimeDensity;
    private int _gridW = 8, _gridH = 6;

    public IReadOnlyList<SpatialEvent> Events => _events;
    public int TotalEvents => _events.Count;

    public override void _Ready()
    {
        _roomDensity = new int[_gridW, _gridH];
        _bottleneckDensity = new int[_gridW, _gridH];
        _crimeDensity = new int[_gridW, _gridH];
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        GD.Print("[SpatialHeatmap] Initialized.");
    }

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        // Auto-detect: log rooms placed in the building
        var vg = GetTree()?.Root?.FindChild("VenueBuilding", true, false) as VenueBuilding;
        if (vg != null)
        {
            foreach (var kvp in vg.Rooms)
            {
                Log(kvp.Key.X, kvp.Key.Y, "room_present", kvp.Value.Type.ToString());
            }
        }

        // Log crime incidents at high-heat rooms
        if (heat > 50 && vg != null)
        {
            foreach (var kvp in vg.Rooms.Where(r => r.Value.DiscretionRating < 30))
            {
                Log(kvp.Key.X, kvp.Key.Y, "crime_risk", $"Heat={heat:F0},Disc={kvp.Value.DiscretionRating:F0}");
            }
        }

        // Log fights as crime events
        if (heat > 70 && vg?.Rooms.Any() == true)
        {
            var rooms = vg.Rooms.ToList();
            for (int i = 0; i < Math.Min(2, rooms.Count); i++)
            {
                var r = rooms[new Random().Next(rooms.Count)];
                Log(r.Key.X, r.Key.Y, "crime_incident", "fight_likely");
            }
        }
    }

    /// <summary>Log a spatial event.</summary>
    public void Log(int x, int y, string eventType, string detail = "")
    {
        var evt = new SpatialEvent
        {
            Day = GameStateManager.Instance?.DayCount ?? 0,
            X = Math.Clamp(x, 0, _gridW - 1),
            Y = Math.Clamp(y, 0, _gridH - 1),
            EventType = eventType,
            Detail = detail
        };
        _events.Add(evt);

        // Update density maps
        if (evt.X < _gridW && evt.Y < _gridH)
        {
            switch (eventType)
            {
                case "room_present": _roomDensity[evt.X, evt.Y]++; break;
                case "client_bottleneck": _bottleneckDensity[evt.X, evt.Y]++; break;
                case "crime_incident": case "crime_risk": case "fight": case "raid":
                    _crimeDensity[evt.X, evt.Y]++; break;
            }
        }
    }

    /// <summary>Log a client movement bottleneck at a position.</summary>
    public void LogBottleneck(int x, int y, string detail) => Log(x, y, "client_bottleneck", detail);

    /// <summary>Log a crime incident at a position.</summary>
    public void LogCrime(int x, int y, string detail) => Log(x, y, "crime_incident", detail);

    // ── CSV Export ──────────────────────────────────────────────────────

    /// <summary>Export all three density maps to a CSV file.</summary>
    public string ExportCsv(string filePath = "user://spatial_heatmap.csv")
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Spatial Heatmap Export");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"# Total events: {_events.Count}");
        sb.AppendLine();

        // Room density
        sb.AppendLine("## Room Placement Density");
        sb.Append("Y\\X,");
        for (int x = 0; x < _gridW; x++) sb.Append($"{x},");
        sb.AppendLine();
        for (int y = 0; y < _gridH; y++)
        {
            sb.Append($"{y},");
            for (int x = 0; x < _gridW; x++)
                sb.Append($"{_roomDensity[x, y]},");
            sb.AppendLine();
        }
        sb.AppendLine();

        // Bottleneck density
        sb.AppendLine("## Client Bottleneck Density");
        sb.Append("Y\\X,");
        for (int x = 0; x < _gridW; x++) sb.Append($"{x},");
        sb.AppendLine();
        for (int y = 0; y < _gridH; y++)
        {
            sb.Append($"{y},");
            for (int x = 0; x < _gridW; x++)
                sb.Append($"{_bottleneckDensity[x, y]},");
            sb.AppendLine();
        }
        sb.AppendLine();

        // Crime density
        sb.AppendLine("## Crime Incident Density");
        sb.Append("Y\\X,");
        for (int x = 0; x < _gridW; x++) sb.Append($"{x},");
        sb.AppendLine();
        for (int y = 0; y < _gridH; y++)
        {
            sb.Append($"{y},");
            for (int x = 0; x < _gridW; x++)
                sb.Append($"{_crimeDensity[x, y]},");
            sb.AppendLine();
        }
        sb.AppendLine();

        // Event log
        sb.AppendLine("## Full Event Log");
        sb.AppendLine("Day,X,Y,EventType,Detail");
        foreach (var e in _events.TakeLast(500))
            sb.AppendLine($"{e.Day},{e.X},{e.Y},{e.EventType},\"{e.Detail}\"");

        // Write
        try
        {
            using var f = Godot.FileAccess.Open(filePath, Godot.FileAccess.ModeFlags.Write);
            f.StoreString(sb.ToString());
            GD.Print($"[SpatialHeatmap] CSV exported: {filePath} ({sb.Length} bytes)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpatialHeatmap] Export error: {ex.Message}");
        }

        return sb.ToString();
    }

    /// <summary>Get the hottest cell (most events) for a given type.</summary>
    public Vector2I GetHottestCell(string eventType)
    {
        int maxVal = 0; Vector2I best = Vector2I.Zero;
        var map = eventType switch
        {
            "room_present" => _roomDensity,
            "client_bottleneck" => _bottleneckDensity,
            _ => _crimeDensity
        };
        for (int x = 0; x < _gridW; x++)
            for (int y = 0; y < _gridH; y++)
                if (map[x, y] > maxVal) { maxVal = map[x, y]; best = new Vector2I(x, y); }
        return best;
    }

    /// <summary>Get summary analytics.</summary>
    public string GetAnalytics()
    {
        int totalRooms = _roomDensity.Cast<int>().Sum();
        int totalBottlenecks = _bottleneckDensity.Cast<int>().Sum();
        int totalCrime = _crimeDensity.Cast<int>().Sum();
        var hotRoom = GetHottestCell("room_present");
        var hotBottle = GetHottestCell("client_bottleneck");
        var hotCrime = GetHottestCell("crime_incident");

        return $"=== Spatial Analytics ===\n" +
               $"Room placements: {totalRooms} (hottest: {hotRoom})\n" +
               $"Bottlenecks: {totalBottlenecks} (hottest: {hotBottle})\n" +
               $"Crime incidents: {totalCrime} (hottest: {hotCrime})\n" +
               $"Total events: {_events.Count}";
    }

    public void Clear()
    {
        _events.Clear();
        Array.Clear(_roomDensity, 0, _roomDensity.Length);
        Array.Clear(_bottleneckDensity, 0, _bottleneckDensity.Length);
        Array.Clear(_crimeDensity, 0, _crimeDensity.Length);
    }

    public override string ToString() =>
        $"[SpatialHeatmap] Events={_events.Count} Grid={_gridW}×{_gridH}";
}
