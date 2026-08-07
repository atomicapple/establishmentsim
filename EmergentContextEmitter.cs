using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Snapshot DTOs ──────────────────────────────────────────────────────

/// <summary>Serializable snapshot of a staff member for context emission.</summary>
public class StaffSnapshot
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("charisma")]
    public float Charisma { get; set; }

    [JsonPropertyName("negotiation")]
    public float Negotiation { get; set; }

    [JsonPropertyName("discretion")]
    public float Discretion { get; set; }

    [JsonPropertyName("stress")]
    public float Stress { get; set; }

    [JsonPropertyName("satisfaction")]
    public float Satisfaction { get; set; }

    [JsonPropertyName("trauma")]
    public float Trauma { get; set; }

    [JsonPropertyName("isBurningOut")]
    public bool IsBurningOut { get; set; }

    [JsonPropertyName("isQuitRisk")]
    public bool IsQuitRisk { get; set; }
}

/// <summary>Serializable policy directive for context emission.</summary>
public class DirectiveSnapshot
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("tier")]
    public int Tier { get; set; }

    [JsonPropertyName("effects")]
    public string Effects { get; set; }
}

/// <summary>Serializable room layout entry for context emission.</summary>
public class RoomSnapshot
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("position")]
    public int[] Position { get; set; }

    [JsonPropertyName("luxuryScore")]
    public float LuxuryScore { get; set; }

    [JsonPropertyName("discretionRating")]
    public float DiscretionRating { get; set; }
}

/// <summary>Serializable recent break event for context emission.</summary>
public class BreakEventSnapshot
{
    [JsonPropertyName("staffName")]
    public string StaffName { get; set; }

    [JsonPropertyName("eventType")]
    public string EventType { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; }

    [JsonPropertyName("day")]
    public int Day { get; set; }
}

/// <summary>
/// Full game context snapshot emitted to the LLM for narrative generation.
/// </summary>
public class GameContextSnapshot
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; }

    [JsonPropertyName("day")]
    public int Day { get; set; }

    [JsonPropertyName("trigger")]
    public string TriggerCondition { get; set; }

    [JsonPropertyName("metrics")]
    public MetricsSnapshot Metrics { get; set; } = new();

    [JsonPropertyName("staff")]
    public List<StaffSnapshot> Staff { get; set; } = new();

    [JsonPropertyName("activeDirectives")]
    public List<DirectiveSnapshot> ActiveDirectives { get; set; } = new();

    [JsonPropertyName("recentBreakEvents")]
    public List<BreakEventSnapshot> RecentBreakEvents { get; set; } = new();

    [JsonPropertyName("roomCount")]
    public int RoomCount { get; set; }
}

/// <summary>Core game metrics at snapshot time.</summary>
public class MetricsSnapshot
{
    [JsonPropertyName("cash")]
    public double Cash { get; set; }

    [JsonPropertyName("reputation")]
    public float Reputation { get; set; }

    [JsonPropertyName("heat")]
    public float Heat { get; set; }

    [JsonPropertyName("publicSentiment")]
    public float PublicSentiment { get; set; }

    [JsonPropertyName("dayCount")]
    public int DayCount { get; set; }

    [JsonPropertyName("totalRevenue")]
    public double TotalRevenue { get; set; }

    [JsonPropertyName("totalExpenses")]
    public double TotalExpenses { get; set; }

    [JsonPropertyName("netProfit")]
    public double NetProfit { get; set; }

    [JsonPropertyName("totalRaids")]
    public int TotalRaids { get; set; }

    [JsonPropertyName("activePolicyBranch")]
    public string ActivePolicyBranch { get; set; }

    [JsonPropertyName("staffQuitDisabled")]
    public bool StaffQuitDisabled { get; set; }

    [JsonPropertyName("permanentOpexModifier")]
    public double PermanentOpexModifier { get; set; }

    [JsonPropertyName("permanentHeatModifier")]
    public float PermanentHeatModifier { get; set; }
}

// ── EmergentContextEmitter Node ────────────────────────────────────────

/// <summary>
/// Bridges deterministic C# simulation state with LLM-driven emergent
/// narrative generation. When critical thresholds are breached
/// (Heat > 70 or any Staff Stress > 85), serializes the full game
/// context to JSON and emits it via stdout in MCP-compatible format
/// to prompt DeepSeek v4 Pro Max for an emergent narrative choice.
///
/// Output format includes a structured JSON snapshot and an LLM prompt
/// template requesting a narrative decision with consequences.
///
/// Usage: Add as a Node. Auto-connects to all relevant systems
///   (GameStateManager, HeatSystem, PsychologicalBreakSystem,
///    PolicyTreeManager, FinancialLedger).
/// </summary>
public partial class EmergentContextEmitter : Node
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a context snapshot is emitted to the LLM.</summary>
    [Signal]
    public delegate void OnContextEmittedEventHandler(
        string trigger, float heat, float maxStaffStress, int day);

    /// <summary>Fired when the emission cooldown expires.</summary>
    [Signal]
    public delegate void OnEmissionCooldownExpiredEventHandler();

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Heat threshold for triggering emission.</summary>
    public float HeatThreshold { get; set; } = 70.0f;

    /// <summary>Staff Stress threshold for triggering emission.</summary>
    public float StaffStressThreshold { get; set; } = 85.0f;

    /// <summary>Minimum in-game days between emissions (prevents spam).</summary>
    public int EmissionCooldownDays { get; set; } = 2;

    /// <summary>Whether to actually emit to stdout (disable for testing).</summary>
    public bool EmitToStdout { get; set; } = true;

    /// <summary>Whether to include the LLM prompt template in the output.</summary>
    public bool IncludePromptTemplate { get; set; } = true;

    /// <summary>Target model name (informational, included in prompt).</summary>
    public string TargetModel { get; set; } = "deepseek-v4-pro-max";

    // ── State ──────────────────────────────────────────────────────────

    private int _lastEmissionDay = int.MinValue;
    private readonly List<BreakEvent> _recentBreaks = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Days until next emission is allowed.</summary>
    public int CooldownRemaining
    {
        get
        {
            int currentDay = GameStateManager.Instance?.DayCount ?? 0;
            int nextAvailable = _lastEmissionDay + EmissionCooldownDays;
            return Math.Max(0, nextAvailable - currentDay);
        }
    }

    /// <summary>Total emissions since session start.</summary>
    public int TotalEmissions { get; private set; }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        if (GameStateManager.Instance != null)
        {
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
            GD.Print("[ContextEmitter] Connected to GameStateManager.OnDailyTick.");
        }

        // Connect to PsychologicalBreakSystem for break event tracking
        CallDeferred(nameof(ConnectToSystems));

        GD.Print($"[ContextEmitter] Initialized. HeatThreshold={HeatThreshold:F0} " +
                 $"StressThreshold={StaffStressThreshold:F0} Cooldown={EmissionCooldownDays}d");
    }

    private void ConnectToSystems()
    {
        var psychBreak = GetTree()?.Root?.FindChild(
            "PsychologicalBreakSystem", recursive: true, owned: false)
            as PsychologicalBreakSystem;

        if (psychBreak != null)
        {
            psychBreak.OnPsychologicalBreak += OnPsychologicalBreak;
            GD.Print("[ContextEmitter] Connected to PsychologicalBreakSystem.");
        }
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Evaluation ───────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float publicSentiment)
    {
        // Cooldown check
        if (CooldownRemaining > 0) return;

        // Check Heat threshold
        if (heat > HeatThreshold)
        {
            EmitContextSnapshot("HeatThreshold", heat, 0f);
            return;
        }

        // Check Staff Stress threshold
        float maxStress = GetMaxStaffStress();
        if (maxStress > StaffStressThreshold)
        {
            EmitContextSnapshot("StaffStressThreshold", heat, maxStress);
            return;
        }
    }

    // ── Event Handlers ─────────────────────────────────────────────────

    private void OnPsychologicalBreak(
        string staffName, int eventType, int traumaSource,
        float stressLevel, float traumaLevel, int day, string narrative)
    {
        // Always emit on psychological break (bypasses cooldown)
        // This is a critical narrative moment
        var gsm = GameStateManager.Instance;
        float heat = gsm?.Heat ?? 0f;

        EmitContextSnapshot("PsychologicalBreak", heat, stressLevel, force: true);

        // Track recent breaks for future snapshots
        _recentBreaks.Add(new BreakEvent
        {
            Staff = null, // We only have the name from the signal
            EventType = (BreakEventType)eventType,
            Source = (TraumaSource)traumaSource,
            StressLevel = stressLevel,
            TraumaLevel = traumaLevel,
            Day = day,
            Narrative = narrative
        });

        // Keep only last 5 breaks
        while (_recentBreaks.Count > 5)
            _recentBreaks.RemoveAt(0);
    }

    // ── Context Snapshot Builder ───────────────────────────────────────

    private GameContextSnapshot BuildSnapshot(string trigger, float heat, float maxStress)
    {
        var gsm = GameStateManager.Instance;

        var snapshot = new GameContextSnapshot
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Day = gsm?.DayCount ?? 0,
            TriggerCondition = trigger,
            Metrics = BuildMetricsSnapshot(gsm),
            Staff = BuildStaffSnapshots(),
            ActiveDirectives = BuildDirectiveSnapshots(),
            RecentBreakEvents = BuildBreakEventSnapshots(),
            RoomCount = GetRoomCount()
        };

        return snapshot;
    }

    private MetricsSnapshot BuildMetricsSnapshot(GameStateManager gsm)
    {
        var ledger = GetTree()?.Root?.FindChild(
            "FinancialLedger", recursive: true, owned: false) as FinancialLedger;

        var heatSystem = GetTree()?.Root?.FindChild(
            "HeatSystem", recursive: true, owned: false) as HeatSystem;

        var policyTree = GetTree()?.Root?.FindChild(
            "PolicyTreeManager", recursive: true, owned: false) as PolicyTreeManager;

        return new MetricsSnapshot
        {
            Cash = gsm?.Cash ?? 0,
            Reputation = gsm?.Reputation ?? 0,
            Heat = gsm?.Heat ?? 0,
            PublicSentiment = gsm?.PublicSentiment ?? 0,
            DayCount = gsm?.DayCount ?? 0,
            TotalRevenue = ledger?.GetTotalRevenue() ?? 0,
            TotalExpenses = ledger?.GetTotalExpenses() ?? 0,
            NetProfit = ledger?.GetNetProfit() ?? 0,
            TotalRaids = heatSystem?.TotalRaidsTriggered ?? 0,
            ActivePolicyBranch = policyTree?.ActiveBranch.ToString() ?? "None",
            StaffQuitDisabled = policyTree?.StaffQuitDisabled ?? false,
            PermanentOpexModifier = policyTree?.PermanentOpexModifier ?? 0,
            PermanentHeatModifier = policyTree?.PermanentHeatModifier ?? 0
        };
    }

    private List<StaffSnapshot> BuildStaffSnapshots()
    {
        var result = new List<StaffSnapshot>();
        var psychBreak = GetTree()?.Root?.FindChild(
            "PsychologicalBreakSystem", recursive: true, owned: false)
            as PsychologicalBreakSystem;

        if (psychBreak == null) return result;

        // Find staff via the psych break system's registry
        // (We access this through the at-risk list as a proxy)
        var atRisk = psychBreak.GetStaffAtRisk();

        // Also try to find all StaffMember resources in the scene
        var allStaff = FindAllStaffMembers();

        foreach (var staff in allStaff)
        {
            result.Add(new StaffSnapshot
            {
                Name = staff.StaffName,
                Role = staff.Role,
                Charisma = staff.Charisma,
                Negotiation = staff.Negotiation,
                Discretion = staff.Discretion,
                Stress = staff.Stress,
                Satisfaction = staff.Satisfaction,
                Trauma = staff.Trauma,
                IsBurningOut = staff.IsBurningOut,
                IsQuitRisk = staff.IsQuitRisk
            });
        }

        return result;
    }

    private List<DirectiveSnapshot> BuildDirectiveSnapshots()
    {
        var result = new List<DirectiveSnapshot>();
        var policyTree = GetTree()?.Root?.FindChild(
            "PolicyTreeManager", recursive: true, owned: false)
            as PolicyTreeManager;

        if (policyTree == null) return result;

        foreach (var policy in policyTree.GetEnactedPolicies())
        {
            result.Add(new DirectiveSnapshot
            {
                Name = policy.PolicyName,
                Tier = policy.Tier,
                Effects = policy.EffectsDescription
            });
        }

        return result;
    }

    private List<BreakEventSnapshot> BuildBreakEventSnapshots()
    {
        return _recentBreaks.Select(b => new BreakEventSnapshot
        {
            StaffName = b.Staff?.StaffName ?? "Unknown",
            EventType = b.EventType.ToString(),
            Source = b.Source.ToString(),
            Day = b.Day
        }).ToList();
    }

    // ── Emission ───────────────────────────────────────────────────────

    /// <summary>
    /// Build and emit the full game context snapshot. Respects cooldown
    /// unless force=true (used for critical events like psychological breaks).
    /// </summary>
    public void EmitContextSnapshot(string trigger, float heat, float maxStress, bool force = false)
    {
        int currentDay = GameStateManager.Instance?.DayCount ?? 0;

        if (!force && CooldownRemaining > 0)
            return;

        _lastEmissionDay = currentDay;
        TotalEmissions++;

        var snapshot = BuildSnapshot(trigger, heat, maxStress);
        var json = JsonSerializer.Serialize(snapshot, _jsonOptions);

        // ── MCP-formatted stdout emission ──────────────────────────
        if (EmitToStdout)
        {
            // MCP boundary marker for tool output
            GD.Print("\n<<<MCP_CONTEXT_SNAPSHOT>>>\n");
            GD.Print(json);
            GD.Print("\n<<<END_MCP_CONTEXT_SNAPSHOT>>>\n");

            if (IncludePromptTemplate)
            {
                var prompt = GeneratePromptTemplate(snapshot);
                GD.Print("\n<<<MCP_LLM_PROMPT>>>\n");
                GD.Print(prompt);
                GD.Print("\n<<<END_MCP_LLM_PROMPT>>>\n");
            }
        }

        // Also emit structured log
        GD.Print($"[ContextEmitter] #{TotalEmissions} Snapshot emitted: " +
                 $"trigger={trigger} heat={heat:F1} maxStress={maxStress:F1} day={currentDay}");

        EmitSignal(SignalName.OnContextEmitted, trigger, heat, maxStress, currentDay);
    }

    // ── LLM Prompt Template ────────────────────────────────────────────

    /// <summary>
    /// Generate a structured prompt for DeepSeek v4 Pro Max requesting
    /// an emergent narrative choice based on the current game state.
    /// </summary>
    private string GeneratePromptTemplate(GameContextSnapshot snapshot)
    {
        var metrics = snapshot.Metrics;
        var sb = new System.Text.StringBuilder();

        // Staff summary
        var staffLines = new System.Text.StringBuilder();
        foreach (var s in snapshot.Staff)
        {
            staffLines.Append("  - ").Append(s.Name).Append(" (").Append(s.Role)
                .Append("): Stress=").Append(s.Stress.ToString("F0"))
                .Append(" Trauma=").Append(s.Trauma.ToString("F0"))
                .Append(" Sat=").Append(s.Satisfaction.ToString("F0"))
                .Append(s.IsBurningOut ? " [BURNOUT]" : "")
                .Append(s.IsQuitRisk ? " [QUIT RISK]" : "")
                .AppendLine();
        }

        // Directives summary
        var dirLines = new System.Text.StringBuilder();
        if (snapshot.ActiveDirectives.Count > 0)
        {
            foreach (var d in snapshot.ActiveDirectives)
                dirLines.Append("  - [").Append(d.Tier).Append("] ").Append(d.Name)
                    .Append(": ").AppendLine(d.Effects);
        }
        else
        {
            dirLines.AppendLine("  (no active directives)");
        }

        // Break events summary
        var breakLines = new System.Text.StringBuilder();
        if (snapshot.RecentBreakEvents.Count > 0)
        {
            foreach (var b in snapshot.RecentBreakEvents)
                breakLines.Append("  - Day ").Append(b.Day).Append(": ").Append(b.StaffName)
                    .Append(" — ").Append(b.EventType).Append(" (source: ").Append(b.Source).AppendLine(")");
        }
        else
        {
            breakLines.AppendLine("  (no recent breaks)");
        }

        sb.Append("You are ").Append(TargetModel).AppendLine(@", an emergent narrative engine for 'Establishment Simulator' —
a management simulation game. Given the following game state snapshot,
generate ONE narrative choice event with 2-3 player options and consequences.

=== GAME STATE (Day ").Append(snapshot.Day).AppendLine(@") ===");
        sb.Append("Trigger: ").AppendLine(snapshot.TriggerCondition);
        sb.AppendLine();
        sb.AppendLine("Financials:");
        sb.Append("  Cash: $").Append(metrics.Cash.ToString("F2"))
            .Append(" | Revenue: $").Append(metrics.TotalRevenue.ToString("F2"))
            .Append(" | Expenses: $").Append(metrics.TotalExpenses.ToString("F2")).AppendLine();
        sb.Append("  Net Profit: $").Append(metrics.NetProfit.ToString("F2"))
            .Append(" | OPEX Modifier: $").Append(metrics.PermanentOpexModifier.ToString("F0"))
            .AppendLine("/day");
        sb.AppendLine();
        sb.AppendLine("Reputation & Risk:");
        sb.Append("  Reputation: ").Append(metrics.Reputation.ToString("F0"))
            .Append("/100 | Public Sentiment: ").Append(metrics.PublicSentiment.ToString("F0"))
            .AppendLine("/100");
        sb.Append("  Heat: ").Append(metrics.Heat.ToString("F0"))
            .Append("/100 | Raids: ").Append(metrics.TotalRaids).AppendLine();
        sb.Append("  Branch: ").Append(metrics.ActivePolicyBranch)
            .Append(" | Staff Quit Locked: ").Append(metrics.StaffQuitDisabled).AppendLine();
        sb.AppendLine();
        sb.Append("Staff (").Append(snapshot.Staff.Count).AppendLine(" members):");
        sb.Append(staffLines);
        sb.AppendLine();
        sb.AppendLine("Active Directives:");
        sb.Append(dirLines);
        sb.AppendLine();
        sb.AppendLine("Recent Break Events:");
        sb.Append(breakLines);

        sb.AppendLine(@"
=== INSTRUCTIONS ===
1. Analyze the state. Identify the most pressing tension (financial, legal, staffing).
2. Generate ONE narrative event that forces the player into a consequential choice.
3. Provide exactly 3 options:
   - Option A: Pragmatic/safe (lower reward, lower risk)
   - Option B: Bold/risky (higher reward, higher risk)
   - Option C: Moral/ethical (reputation/sentiment gain at resource cost)
4. For each option, specify the expected effects on Cash, Heat, Reputation,
   PublicSentiment, and any affected StaffMember's Stress/Satisfaction.

Output as valid JSON with this exact schema:
{
  ""eventTitle"": ""..."",
  ""narrative"": ""..."",
  ""triggerFactor"": """).Append(snapshot.TriggerCondition).AppendLine(@""",
  ""options"": [
    {
      ""label"": ""A: ..."",
      ""description"": ""..."",
      ""effects"": {
        ""cash"": 0.0,
        ""heat"": 0.0,
        ""reputation"": 0.0,
        ""publicSentiment"": 0.0,
        ""staffEffects"": []
      }
    },
    ...
  ]
}");

        return sb.ToString();
    }

    // ── Utility ────────────────────────────────────────────────────────

    private float GetMaxStaffStress()
    {
        float max = 0f;
        var allStaff = FindAllStaffMembers();
        foreach (var staff in allStaff)
            max = Mathf.Max(max, staff.Stress);
        return max;
    }

    private List<StaffMember> FindAllStaffMembers()
    {
        // StaffMember is a Resource, not a Node — StaffRoster is the owner.
        // Previously read PsychologicalBreakSystem.GetStaffAtRisk(), which
        // meant the narrative context layer only ever saw the crisis cases.
        return StaffRoster.Instance?.GetAll().ToList() ?? new List<StaffMember>();
    }

    private int GetRoomCount()
    {
        var venue = GetTree()?.Root?.FindChild(
            "VenueBuilding", recursive: true, owned: false)
            as VenueBuilding;

        return venue?.Rooms?.Count ?? 0;
    }

    /// <summary>Manually trigger a snapshot (for testing or forced events).</summary>
    public void ForceEmitSnapshot(string trigger)
    {
        var gsm = GameStateManager.Instance;
        EmitContextSnapshot(trigger, gsm?.Heat ?? 0f, GetMaxStaffStress(), force: true);
    }

    /// <summary>Get the last emitted snapshot as a JSON string (for debugging).</summary>
    public string GetLastSnapshotJson()
    {
        var gsm = GameStateManager.Instance;
        var snapshot = BuildSnapshot("manual", gsm?.Heat ?? 0f, GetMaxStaffStress());
        return JsonSerializer.Serialize(snapshot, _jsonOptions);
    }

    public override string ToString()
    {
        return $"[ContextEmitter] Emissions={TotalEmissions} " +
               $"Cooldown={CooldownRemaining}d " +
               $"Thresholds: Heat>{HeatThreshold:F0} Stress>{StaffStressThreshold:F0}";
    }
}
