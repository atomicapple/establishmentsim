using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>
/// Headless simulation stress-test harness. Runs 1,000 automated daily
/// ticks, verifies economic state balance, checks Heat calculation
/// stability, and logs final performance output.
///
/// Usage: godot --headless --path . --script res://SimulationTestRunner.cs
///   or add as scene child and run.
/// </summary>
public partial class SimulationTestRunner : Node
{
    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Number of daily ticks to simulate.</summary>
    public int TickCount { get; set; } = 1000;

    /// <summary>Whether to auto-start the simulation in _Ready().</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Whether to quit the engine after simulation completes.</summary>
    public bool QuitOnComplete { get; set; } = true;

    // ── Simulation State ───────────────────────────────────────────────

    private GameStateManager _gsm;
    private HeatSystem _heatSystem;
    private FinancialLedger _ledger;

    // ── Tracking ───────────────────────────────────────────────────────

    private readonly List<SimulationSnapshot> _history = new();
    private long _memoryBeforeBytes;
    private long _memoryAfterBytes;
    private double _elapsedMs;

    /// <summary>Full tick-by-tick history for analysis.</summary>
    public IReadOnlyList<SimulationSnapshot> History => _history;

    // ── Results ────────────────────────────────────────────────────────
    public SimulationResult Result { get; private set; }

    /// <summary>Snapshot of all tracked metrics at a single tick.</summary>
    public struct SimulationSnapshot
    {
        public int Tick;
        public double Cash;
        public float Heat;
        public float Reputation;
        public float PublicSentiment;
        public float HeatDelta;       // heat change from previous tick
        public double Revenue;
        public double Expenses;
        public double NetProfit;
        public long MemoryBytes;
    }

    /// <summary>Aggregated results after simulation completes.</summary>
    public class SimulationResult
    {
        public int TotalTicks;
        public double ElapsedMs;
        public double TicksPerSecond;

        // Economic balance
        public double FinalCash;
        public double MaxCash;
        public double MinCash;
        public double TotalRevenue;
        public double TotalExpenses;
        public double AvgRevenuePerTick;
        public double AvgExpensesPerTick;

        // Heat stability
        public float FinalHeat;
        public float MaxHeat;
        public float MinHeat;
        public float AvgHeat;
        public float HeatStdDev;        // lower = more stable
        public int HeatOscillationCount; // times heat changed direction
        public bool HeatStabilized;      // did heat converge?

        // Memory
        public long StartMemoryBytes;
        public long EndMemoryBytes;
        public long MemoryDeltaBytes;
        public double AvgMemoryPerTick;
        public bool MemoryLeakDetected;

        // Reputation / Sentiment
        public float FinalReputation;
        public float FinalSentiment;
        public float AvgReputation;

        // Validation
        public bool AllMetricsInBounds;
        public List<string> Warnings;
        public List<string> Errors;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("    SIMULATION STRESS TEST RESULTS");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"  Ticks:        {TotalTicks}");
            sb.AppendLine($"  Duration:     {ElapsedMs:F0} ms ({TicksPerSecond:F0} ticks/s)");
            sb.AppendLine();
            sb.AppendLine("  ── Economic Balance ──");
            sb.AppendLine($"  Final Cash:   ${FinalCash:F2}");
            sb.AppendLine($"  Cash Range:   ${MinCash:F2} – ${MaxCash:F2}");
            sb.AppendLine($"  Total Revenue: ${TotalRevenue:F2}");
            sb.AppendLine($"  Total Expenses: ${TotalExpenses:F2}");
            sb.AppendLine($"  Net:          ${TotalRevenue - TotalExpenses:F2}");
            sb.AppendLine($"  Avg Rev/Tick: ${AvgRevenuePerTick:F2}");
            sb.AppendLine($"  Avg Exp/Tick: ${AvgExpensesPerTick:F2}");
            sb.AppendLine();
            sb.AppendLine("  ── Heat Stability ──");
            sb.AppendLine($"  Final Heat:   {FinalHeat:F1}/100");
            sb.AppendLine($"  Heat Range:   {MinHeat:F1} – {MaxHeat:F1}");
            sb.AppendLine($"  Avg Heat:     {AvgHeat:F1}");
            sb.AppendLine($"  Std Dev:      {HeatStdDev:F2}");
            sb.AppendLine($"  Oscillations: {HeatOscillationCount}");
            sb.AppendLine($"  Stabilized:   {(HeatStabilized ? "YES ✓" : "NO — review HeatSystem config")}");
            sb.AppendLine();
            sb.AppendLine("  ── Memory ──");
            sb.AppendLine($"  Start:        {StartMemoryBytes / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"  End:          {EndMemoryBytes / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"  Delta:        {MemoryDeltaBytes / 1024.0:F2} KB");
            sb.AppendLine($"  Leak:         {(MemoryLeakDetected ? "⚠ YES" : "NO ✓")}");
            sb.AppendLine();
            sb.AppendLine("  ── Reputation & Sentiment ──");
            sb.AppendLine($"  Final Rep:    {FinalReputation:F1}/100");
            sb.AppendLine($"  Final Sent:   {FinalSentiment:F1}/100");
            sb.AppendLine($"  Avg Rep:      {AvgReputation:F1}");
            sb.AppendLine();
            sb.AppendLine($"  All In Bounds: {(AllMetricsInBounds ? "YES ✓" : "⚠ NO")}");
            if (Warnings.Count > 0)
            {
                sb.AppendLine($"  Warnings ({Warnings.Count}):");
                foreach (var w in Warnings) sb.AppendLine($"    ⚠ {w}");
            }
            if (Errors.Count > 0)
            {
                sb.AppendLine($"  Errors ({Errors.Count}):");
                foreach (var e in Errors) sb.AppendLine($"    ❌ {e}");
            }
            sb.AppendLine("═══════════════════════════════════════════");

            return sb.ToString();
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        GD.Print("[SimTestRunner] Ready.");

        if (AutoStart)
            CallDeferred(nameof(RunSimulation));
    }

    // ── Simulation Execution ───────────────────────────────────────────

    /// <summary>Run the full simulation test.</summary>
    public void RunSimulation()
    {
        GD.Print($"[SimTestRunner] Starting {TickCount}-tick simulation...");

        // Collect garbage before starting
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _memoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);

        var sw = Stopwatch.StartNew();

        // Initialize systems
        InitializeSystems();

        // Run ticks
        for (int i = 0; i < TickCount; i++)
        {
            ExecuteTick(i);
        }

        sw.Stop();
        _elapsedMs = sw.Elapsed.TotalMilliseconds;

        // Collect garbage after
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _memoryAfterBytes = GC.GetTotalMemory(forceFullCollection: true);

        // Compute results
        Result = ComputeResults();

        // Log final output
        GD.Print("\n" + Result.ToString() + "\n");
        GD.Print($"[SimTestRunner] Simulation complete. {Result.TotalTicks} ticks in {Result.ElapsedMs:F0} ms.");

        if (QuitOnComplete)
        {
            GD.Print("[SimTestRunner] Quitting engine.");
            GetTree()?.Quit();
        }
    }

    // ── System Initialization ──────────────────────────────────────────

    private void InitializeSystems()
    {
        // Use the autoload GameStateManager — do NOT create a duplicate.
        _gsm = GameStateManager.Instance;
        if (_gsm == null)
        {
            GD.PrintErr("[SimTestRunner] GameStateManager.Instance is null — aborting.");
            return;
        }

        // Create and add support systems as children of this node.
        _heatSystem = new HeatSystem();
        _heatSystem.Name = "HeatSystem";
        AddChild(_heatSystem);

        _ledger = new FinancialLedger();
        _ledger.Name = "FinancialLedger";
        AddChild(_ledger);

        // Seed initial revenue so HeatSystem has something to work with.
        _ledger.RecordRevenue(RevenueCategory.ClientFees, 500, "Initial seed: client fees");
        _ledger.RecordRevenue(RevenueCategory.VIPServices, 300, "Initial seed: VIP services");

        // Set a client tier so heat is generated.
        _heatSystem.SetClientTier(1); // mid-tier

        GD.Print("[SimTestRunner] Systems initialized: GSM (autoload), HeatSystem, FinancialLedger.");
    }

    // ── Tick Execution ─────────────────────────────────────────────────

    private void ExecuteTick(int tickIndex)
    {
        // Snapshot before tick
        double cashBefore = _gsm?.Cash ?? 0;
        float heatBefore = _gsm?.Heat ?? 0;

        // Manually invoke the daily tick on GameStateManager.
        // This triggers all connected OnDailyTick listeners (FinancialLedger, HeatSystem, etc.)
        _gsm?.InvokeDailyTick();

        // Simulate client revenue on MOST ticks to roughly balance the $215/day auto-OPEX.
        // Revenue: ~$200-500 every tick (instead of every 3).
        if (tickIndex % 1 == 0)
        {
            double revenue = 200 + (GD.Randi() % 300); // $200–$500
            _ledger.RecordRevenue(RevenueCategory.ClientFees, revenue,
                $"Tick {tickIndex}: clients");

            // Occasionally add VIP revenue
            if (tickIndex % 10 == 0)
            {
                double vipRevenue = 500 + (GD.Randi() % 500);
                _ledger.RecordRevenue(RevenueCategory.VIPServices, vipRevenue,
                    $"Tick {tickIndex}: VIP night");
            }

            // Adjust client tier based on heat
            float currentHeat = _gsm?.Heat ?? 0;
            if (currentHeat > 50)
                _heatSystem.SetClientTier(2); // VIP tier
            else if (currentHeat > 25)
                _heatSystem.SetClientTier(1); // mid tier
            else
                _heatSystem.SetClientTier(0); // common tier
        }

        // Occasionally trigger a bribe to test heat mitigation
        float heatNow = _gsm?.Heat ?? 0;
        if (tickIndex % 50 == 0 && heatNow > 40)
        {
            double bribeAmount = 200 + (GD.Randi() % 300);
            _heatSystem.BribePrecinctCaptain(bribeAmount);
        }

        // Snapshot after tick
        double cashAfter = _gsm?.Cash ?? 0;
        float heatAfter = _gsm?.Heat ?? 0;

        // Record snapshot
        _history.Add(new SimulationSnapshot
        {
            Tick = tickIndex,
            Cash = cashAfter,
            Heat = heatAfter,
            Reputation = _gsm?.Reputation ?? 0,
            PublicSentiment = _gsm?.PublicSentiment ?? 0,
            HeatDelta = heatAfter - heatBefore,
            Revenue = _ledger?.GetTotalRevenue() ?? 0,
            Expenses = _ledger?.GetTotalExpenses() ?? 0,
            NetProfit = _ledger?.GetNetProfit() ?? 0,
            MemoryBytes = GC.GetTotalMemory(forceFullCollection: false)
        });
    }

    // ── Result Computation ─────────────────────────────────────────────

    private SimulationResult ComputeResults()
    {
        var r = new SimulationResult
        {
            TotalTicks = _history.Count,
            ElapsedMs = _elapsedMs,
            TicksPerSecond = _history.Count / (_elapsedMs / 1000.0),
            Warnings = new List<string>(),
            Errors = new List<string>()
        };

        if (_history.Count == 0) return r;

        // ── Economic Balance ─────────────────────────────────────────
        r.FinalCash = _history[^1].Cash;
        r.MaxCash = _history.Max(s => s.Cash);
        r.MinCash = _history.Min(s => s.Cash);
        r.TotalRevenue = _history[^1].Revenue;
        r.TotalExpenses = _history[^1].Expenses;
        r.AvgRevenuePerTick = r.TotalRevenue / r.TotalTicks;
        r.AvgExpensesPerTick = r.TotalExpenses / r.TotalTicks;

        if (r.FinalCash < 0)
            r.Errors.Add($"Cash went negative: ${r.FinalCash:F2}");
        if (r.MinCash < -1000)
            r.Errors.Add($"Cash severely negative at min: ${r.MinCash:F2}");

        // ── Heat Stability ───────────────────────────────────────────
        r.FinalHeat = _history[^1].Heat;
        r.MaxHeat = _history.Max(s => s.Heat);
        r.MinHeat = _history.Min(s => s.Heat);

        // Average
        r.AvgHeat = _history.Average(s => s.Heat);

        // Standard deviation (lower = more stable)
        float sumSq = _history.Sum(s => (s.Heat - r.AvgHeat) * (s.Heat - r.AvgHeat));
        r.HeatStdDev = Mathf.Sqrt(sumSq / _history.Count);

        // Oscillation count (direction changes in heat)
        r.HeatOscillationCount = 0;
        int? lastDirection = null;
        for (int i = 1; i < _history.Count; i++)
        {
            float delta = _history[i].Heat - _history[i - 1].Heat;
            int dir = delta > 0.01f ? 1 : delta < -0.01f ? -1 : 0;
            if (dir != 0 && lastDirection.HasValue && dir != lastDirection.Value)
                r.HeatOscillationCount++;
            if (dir != 0)
                lastDirection = dir;
        }

        // Stabilized: last 50 ticks have std dev < 1.0
        if (_history.Count >= 50)
        {
            var last50 = _history.Skip(_history.Count - 50).Select(s => s.Heat).ToList();
            float last50Avg = last50.Average();
            float last50Sq = last50.Sum(v => (v - last50Avg) * (v - last50Avg));
            float last50Std = Mathf.Sqrt(last50Sq / 50);
            r.HeatStabilized = last50Std < 1.0f && r.HeatOscillationCount < _history.Count * 0.05;
        }

        if (!r.HeatStabilized)
            r.Warnings.Add($"Heat did not stabilize (last 50 std dev too high or too many oscillations)");
        if (r.MaxHeat > 95)
            r.Warnings.Add($"Heat exceeded 95 ({r.MaxHeat:F1}) — risk of runaway police intervention");
        if (r.HeatStdDev > 20)
            r.Warnings.Add($"High heat volatility (std dev = {r.HeatStdDev:F1})");

        // ── Memory ───────────────────────────────────────────────────
        r.StartMemoryBytes = _memoryBeforeBytes;
        r.EndMemoryBytes = _memoryAfterBytes;
        r.MemoryDeltaBytes = r.EndMemoryBytes - r.StartMemoryBytes;
        r.AvgMemoryPerTick = _history.Average(s => (double)s.MemoryBytes);

        // Leak detection: if memory grows more than 10 MB across the run
        r.MemoryLeakDetected = r.MemoryDeltaBytes > 10 * 1024 * 1024;
        if (r.MemoryLeakDetected)
            r.Errors.Add($"Potential memory leak: +{r.MemoryDeltaBytes / 1024.0 / 1024.0:F2} MB");

        // ── Reputation & Sentiment ────────────────────────────────────
        r.FinalReputation = _history[^1].Reputation;
        r.FinalSentiment = _history[^1].PublicSentiment;
        r.AvgReputation = _history.Average(s => s.Reputation);

        if (r.FinalReputation < 10)
            r.Warnings.Add($"Reputation dangerously low: {r.FinalReputation:F1}");
        if (r.FinalSentiment < 10)
            r.Warnings.Add($"Public sentiment dangerously low: {r.FinalSentiment:F1}");

        // ── Bounds validation ─────────────────────────────────────────
        r.AllMetricsInBounds = true;
        foreach (var s in _history)
        {
            if (s.Heat < 0 || s.Heat > 100) { r.AllMetricsInBounds = false; break; }
            if (s.Reputation < 0 || s.Reputation > 100) { r.AllMetricsInBounds = false; break; }
            if (s.PublicSentiment < 0 || s.PublicSentiment > 100) { r.AllMetricsInBounds = false; break; }
        }
        if (!r.AllMetricsInBounds)
            r.Errors.Add("Some metrics went out of [0,100] bounds during simulation");

        // ── Economic balance check ────────────────────────────────────
        // Revenue should roughly cover expenses over the long run
        double netPosition = r.TotalRevenue - r.TotalExpenses;
        double burnRate = netPosition / r.TotalTicks;
        r.Warnings.Add($"Net position: ${netPosition:F2} (burn rate: ${burnRate:F2}/tick)");

        return r;
    }

    // ── Utility ────────────────────────────────────────────────────────

    public override string ToString()
    {
        return $"[SimTestRunner] Ticks={_history.Count} " +
               $"Mem={(Result?.EndMemoryBytes - Result?.StartMemoryBytes) / 1024 ?? 0}KB delta";
    }
}

/// <summary>
/// Extension to allow manual tick invocation on GameStateManager for testing.
/// </summary>
public partial class GameStateManager
{
    /// <summary>Manually trigger the daily tick for headless simulation.</summary>
    public void InvokeDailyTick()
    {
        _dayCount++;

        // Replicate OnTimerTimeout logic
        EmitSignal(SignalName.OnDailyTick, _cash, _reputation, _heat, _publicSentiment);

        GD.Print($"[SimTestRunner] Day {_dayCount} tick — " +
                 $"Cash=${_cash:F2} Rep={_reputation:F1} Heat={_heat:F1} Sentiment={_publicSentiment:F1}");
    }
}
