using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>Revenue categories for the establishment.</summary>
public enum RevenueCategory
{
    ClientFees,
    VIPServices,
    InformationSales
}

/// <summary>Operating expense categories.</summary>
public enum ExpenseCategory
{
    StaffSalaries,
    Bribes,
    RoomUpgrades,
    FacilityMaintenance
}

/// <summary>A single financial transaction recorded in the ledger.</summary>
public struct LedgerEntry
{
    public int Day { get; set; }
    public bool IsRevenue { get; set; }
    public string Category { get; set; }
    public double Amount { get; set; }
    public string Description { get; set; }

    public LedgerEntry(int day, bool isRevenue, string category, double amount, string description)
    {
        Day = day;
        IsRevenue = isRevenue;
        Category = category;
        Amount = amount;
        Description = description;
    }
}

/// <summary>Monthly summary data transfer object for JSON serialization.</summary>
public class MonthlySummary
{
    [JsonPropertyName("month")]
    public int Month { get; set; }

    [JsonPropertyName("totalRevenue")]
    public double TotalRevenue { get; set; }

    [JsonPropertyName("totalExpenses")]
    public double TotalExpenses { get; set; }

    [JsonPropertyName("netProfit")]
    public double NetProfit { get; set; }

    [JsonPropertyName("revenue")]
    public Dictionary<string, double> RevenueBreakdown { get; set; } = new();

    [JsonPropertyName("expenses")]
    public Dictionary<string, double> ExpenseBreakdown { get; set; } = new();

    [JsonPropertyName("entryCount")]
    public int EntryCount { get; set; }

    [JsonPropertyName("dailyAverage")]
    public DailyAverage DailyAverage { get; set; } = new();
}

/// <summary>Per-day averages for the month.</summary>
public class DailyAverage
{
    [JsonPropertyName("revenue")]
    public double Revenue { get; set; }

    [JsonPropertyName("expenses")]
    public double Expenses { get; set; }

    [JsonPropertyName("profit")]
    public double Profit { get; set; }
}

// ── FinancialLedger Node ───────────────────────────────────────────────

/// <summary>
/// Tracks venue revenue and operating expenses across in-game days.
/// Auto-records fixed OPEX on each daily tick and responds to
/// HeatSystem bribe events. Provides GetMonthlySummary() as
/// structured JSON for UI rendering.
///
/// Usage: Add as a child of GameStateManager or any Node.
///   It auto-connects to GameStateManager.OnDailyTick and
///   HeatSystem signals when found in the scene tree.
/// </summary>
public partial class FinancialLedger : Node, ISaveableSystem
{
    // ── Storage ────────────────────────────────────────────────────────
    private readonly List<LedgerEntry> _entries = new();

    /// <summary>All ledger entries in chronological order.</summary>
    public IReadOnlyList<LedgerEntry> Entries => _entries;

    // ── Fixed OPEX Configuration ───────────────────────────────────────
    // These are auto-deducted each daily tick.

    /// <summary>Daily staff salary expense.</summary>
    public double DailyStaffSalaries { get; set; } = 150.0;

    /// <summary>Daily facility maintenance cost.</summary>
    public double DailyFacilityMaintenance { get; set; } = 40.0;

    /// <summary>Daily room upkeep/upgrade amortization.</summary>
    public double DailyRoomUpgrades { get; set; } = 25.0;

    // ── JSON Serializer Options ─────────────────────────────────────────
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Connect to daily tick for auto-OPEX recording
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
            GD.Print("[FinancialLedger] Connected to GameStateManager.OnDailyTick.");
        }
        else
        {
            GD.PrintErr("[FinancialLedger] GameStateManager.Instance is null.");
        }

        // Listen for bribes from HeatSystem (if present in scene)
        CallDeferred(nameof(FindAndConnectHeatSystem));

        GD.Print($"[FinancialLedger] Initialized. Entries={_entries.Count}");
    }

    private void FindAndConnectHeatSystem()
    {
        var heatSystem = GetTree()?.Root?.FindChild("HeatSystem", recursive: true, owned: false) as HeatSystem;
        if (heatSystem != null)
        {
            heatSystem.OnBribePaid += OnBribePaid;
            heatSystem.OnLegalDefenseExecuted += OnLegalDefenseExecuted;
            GD.Print("[FinancialLedger] Connected to HeatSystem bribe/legal events.");
        }
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Tick Handler ─────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float publicSentiment)
    {
        int day = GameStateManager.Instance?.DayCount ?? 0;

        var salaries = GetDailySalaries();
        var policyOpex = GetPolicyOpexModifier();

        // Policy OPEX lands on facility maintenance — Workforce Protection
        // raises the cost of running the house, Operational Freedom lowers it.
        // PolicyTreeManager.PermanentOpexModifier was previously computed on
        // enactment and never actually charged to anyone.
        var maintenance = Math.Max(0.0, DailyFacilityMaintenance + policyOpex);

        RecordExpense(ExpenseCategory.StaffSalaries, salaries,
            $"Day {day} staff salaries");
        RecordExpense(ExpenseCategory.FacilityMaintenance, maintenance,
            $"Day {day} facility maintenance");
        RecordExpense(ExpenseCategory.RoomUpgrades, DailyRoomUpgrades,
            $"Day {day} room upkeep amortization");

        GD.Print($"[FinancialLedger] Day {day}: OPEX ${salaries + maintenance + DailyRoomUpgrades:F0} " +
                 $"(salaries ${salaries:F0}, maintenance ${maintenance:F0}, upkeep ${DailyRoomUpgrades:F0}). " +
                 $"Entries: {_entries.Count}");
    }

    /// <summary>
    /// Daily wage bill, derived from the actual roster rather than a flat
    /// constant. StaffMember.Salary is monthly, so it is amortized over a
    /// 30-day month to match the rest of the ledger's month length.
    ///
    /// Falls back to <see cref="DailyStaffSalaries"/> only when no roster
    /// exists — with an empty house the bill is correctly zero, so hiring is
    /// a real ongoing commitment rather than a one-off fee.
    /// </summary>
    private double GetDailySalaries()
    {
        var roster = StaffRoster.Instance;
        if (roster == null) return DailyStaffSalaries;

        var monthly = roster.GetTotalSalaries();
        var daily = monthly / 30.0;

        // Wage demands track the city-wide economy.
        var macro = GetTree()?.Root?.FindChild("MacroEconomyEngine", true, false) as MacroEconomyEngine;
        if (macro != null) daily *= macro.WageDemandMultiplier;

        return Math.Max(0.0, daily);
    }

    /// <summary>Additive daily OPEX from enacted policy. Zero when no policy tree is present.</summary>
    private double GetPolicyOpexModifier()
    {
        var policies = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        return policies?.PermanentOpexModifier ?? 0.0;
    }

    // ── Event Handlers ─────────────────────────────────────────────────

    private void OnBribePaid(double cost, float heatReduction)
    {
        int day = GameStateManager.Instance?.DayCount ?? 0;
        RecordExpense(ExpenseCategory.Bribes, cost,
            $"Day {day} precinct captain bribe (heat -{heatReduction:F1})");
    }

    private void OnLegalDefenseExecuted(double cost, float heatReduction)
    {
        int day = GameStateManager.Instance?.DayCount ?? 0;
        RecordExpense(ExpenseCategory.Bribes, cost,
            $"Day {day} legal defense fees (heat -{heatReduction:F1})");
    }

    // ── Recording Methods ──────────────────────────────────────────────

    /// <summary>Record a revenue transaction.</summary>
    public void RecordRevenue(RevenueCategory category, double amount, string description)
    {
        if (amount <= 0) return;

        int day = GameStateManager.Instance?.DayCount ?? 0;
        _entries.Add(new LedgerEntry(day, true, category.ToString(), amount, description));

        // Apply revenue to global cash
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash += amount;

        GD.Print($"[FinancialLedger] +REVENUE [{category}] ${amount:F2} — {description}");
    }

    /// <summary>Record an expense transaction.</summary>
    public void RecordExpense(ExpenseCategory category, double amount, string description)
    {
        if (amount <= 0) return;

        int day = GameStateManager.Instance?.DayCount ?? 0;
        _entries.Add(new LedgerEntry(day, false, category.ToString(), amount, description));

        // Deduct from global cash
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= amount;

        GD.Print($"[FinancialLedger] -EXPENSE [{category}] ${amount:F2} — {description}");
    }

    // ── Query Methods ──────────────────────────────────────────────────

    /// <summary>Total revenue across all time.</summary>
    public double GetTotalRevenue() => _entries.Where(e => e.IsRevenue).Sum(e => e.Amount);

    /// <summary>Total expenses across all time.</summary>
    public double GetTotalExpenses() => _entries.Where(e => !e.IsRevenue).Sum(e => e.Amount);

    /// <summary>Net profit (revenue − expenses).</summary>
    public double GetNetProfit() => GetTotalRevenue() - GetTotalExpenses();

    /// <summary>Entries filtered to a specific day.</summary>
    public IEnumerable<LedgerEntry> GetEntriesForDay(int day) =>
        _entries.Where(e => e.Day == day);

    /// <summary>Revenue for a specific day.</summary>
    public double GetRevenueForDay(int day) =>
        _entries.Where(e => e.Day == day && e.IsRevenue).Sum(e => e.Amount);

    /// <summary>Expenses for a specific day.</summary>
    public double GetExpensesForDay(int day) =>
        _entries.Where(e => e.Day == day && !e.IsRevenue).Sum(e => e.Amount);

    // ── Monthly Summary ────────────────────────────────────────────────

    /// <summary>
    /// Generate a structured JSON monthly summary suitable for
    /// rendering in the financial UI ledger.
    /// Assumes 30 days per in-game month for simplicity.
    /// </summary>
    /// <param name="month">Month number (1-based). Day range = [(month-1)*30+1, month*30].</param>
    /// <returns>JSON string with revenue/expense breakdown, net profit, and daily averages.</returns>
    public string GetMonthlySummary(int month)
    {
        int dayStart = (month - 1) * 30 + 1;
        int dayEnd = month * 30;

        var monthEntries = _entries.Where(e => e.Day >= dayStart && e.Day <= dayEnd).ToList();
        int activeDays = monthEntries.Select(e => e.Day).Distinct().Count();

        var summary = new MonthlySummary
        {
            Month = month,
            EntryCount = monthEntries.Count
        };

        double totalRevenue = 0;
        double totalExpenses = 0;

        foreach (var entry in monthEntries)
        {
            if (entry.IsRevenue)
            {
                totalRevenue += entry.Amount;
                var key = entry.Category;
                summary.RevenueBreakdown.TryGetValue(key, out var existing);
                summary.RevenueBreakdown[key] = existing + entry.Amount;
            }
            else
            {
                totalExpenses += entry.Amount;
                var key = entry.Category;
                summary.ExpenseBreakdown.TryGetValue(key, out var existing);
                summary.ExpenseBreakdown[key] = existing + entry.Amount;
            }
        }

        summary.TotalRevenue = totalRevenue;
        summary.TotalExpenses = totalExpenses;
        summary.NetProfit = totalRevenue - totalExpenses;

        if (activeDays > 0)
        {
            summary.DailyAverage = new DailyAverage
            {
                Revenue = totalRevenue / activeDays,
                Expenses = totalExpenses / activeDays,
                Profit = (totalRevenue - totalExpenses) / activeDays
            };
        }

        return JsonSerializer.Serialize(summary, _jsonOptions);
    }

    /// <summary>
    /// Get monthly summary for the current in-game month
    /// based on current day count.
    /// </summary>
    public string GetCurrentMonthSummary()
    {
        int day = GameStateManager.Instance?.DayCount ?? 0;
        int month = day == 0 ? 1 : (day - 1) / 30 + 1;
        return GetMonthlySummary(month);
    }

    /// <summary>
    /// Get a summary of all-time totals as JSON.
    /// </summary>
    public string GetAllTimeSummary()
    {
        var summary = new MonthlySummary
        {
            Month = 0, // 0 = all-time
            EntryCount = _entries.Count,
            TotalRevenue = GetTotalRevenue(),
            TotalExpenses = GetTotalExpenses(),
            NetProfit = GetNetProfit()
        };

        foreach (var entry in _entries.Where(e => e.IsRevenue))
        {
            summary.RevenueBreakdown.TryGetValue(entry.Category, out var existing);
            summary.RevenueBreakdown[entry.Category] = existing + entry.Amount;
        }

        foreach (var entry in _entries.Where(e => !e.IsRevenue))
        {
            summary.ExpenseBreakdown.TryGetValue(entry.Category, out var existing);
            summary.ExpenseBreakdown[entry.Category] = existing + entry.Amount;
        }

        int activeDays = _entries.Select(e => e.Day).Distinct().Count();
        if (activeDays > 0)
        {
            summary.DailyAverage = new DailyAverage
            {
                Revenue = summary.TotalRevenue / activeDays,
                Expenses = summary.TotalExpenses / activeDays,
                Profit = summary.NetProfit / activeDays
            };
        }

        return JsonSerializer.Serialize(summary, _jsonOptions);
    }

    // ── Bulk Recording ─────────────────────────────────────────────────

    /// <summary>
    /// Record a batch of client payments for a night's operation.
    /// </summary>
    public void RecordClientNight(
        int commonClients,
        int midClients,
        int vipClients,
        double commonFee,
        double midFee,
        double vipFee)
    {
        var day = GameStateManager.Instance?.DayCount ?? 0;
        double total = 0;

        if (commonClients > 0)
        {
            double amount = commonClients * commonFee;
            RecordRevenue(RevenueCategory.ClientFees, amount,
                $"Day {day}: {commonClients} common clients × ${commonFee:F0}");
            total += amount;
        }

        if (midClients > 0)
        {
            double amount = midClients * midFee;
            RecordRevenue(RevenueCategory.ClientFees, amount,
                $"Day {day}: {midClients} mid-tier clients × ${midFee:F0}");
            total += amount;
        }

        if (vipClients > 0)
        {
            double amount = vipClients * vipFee;
            RecordRevenue(RevenueCategory.VIPServices, amount,
                $"Day {day}: {vipClients} VIP clients × ${vipFee:F0}");
            total += amount;
        }

        GD.Print($"[FinancialLedger] Recorded client night: ${total:F2} total from " +
                 $"{commonClients + midClients + vipClients} clients.");
    }

    public override string ToString()
    {
        return $"[FinancialLedger] Entries={_entries.Count} " +
               $"Revenue=${GetTotalRevenue():F2} Expenses=${GetTotalExpenses():F2} " +
               $"Net=${GetNetProfit():F2}";
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "ledger";

    /// <summary>Days of ledger detail kept in the save file.</summary>
    [Export] public int PersistedHistoryDays { get; set; } = 90;

    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        // Individual entries are kept rather than daily totals, because the
        // Ledger screen breaks revenue and expenses down by category. Trimmed
        // to a rolling window so a long campaign's save file stays bounded.
        var currentDay = GameStateManager.Instance?.DayCount ?? 0;
        var cutoff = currentDay - PersistedHistoryDays;

        var entries = new System.Text.Json.Nodes.JsonArray();
        foreach (var e in _entries)
        {
            if (e.Day < cutoff) continue;

            entries.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["day"] = e.Day,
                ["isRevenue"] = e.IsRevenue,
                ["category"] = e.Category,
                ["amount"] = e.Amount,
                ["description"] = e.Description
            });
        }

        return new System.Text.Json.Nodes.JsonObject { ["entries"] = entries };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state?["entries"] is not System.Text.Json.Nodes.JsonArray entries) return;

        _entries.Clear();

        foreach (var node in entries)
        {
            if (node is not System.Text.Json.Nodes.JsonObject entry) continue;

            _entries.Add(new LedgerEntry
            {
                Day = (int?)entry["day"] ?? 0,
                IsRevenue = (bool?)entry["isRevenue"] ?? false,
                Category = (string)entry["category"] ?? "Unknown",
                Amount = (double?)entry["amount"] ?? 0.0,
                Description = (string)entry["description"] ?? ""
            });
        }

        GD.Print($"[FinancialLedger] Restored {_entries.Count} entries " +
                 $"(net ${GetNetProfit():F0}).");
    }
}
