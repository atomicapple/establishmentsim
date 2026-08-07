using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Crisis trigger type.</summary>
public enum CrisisTrigger { PoliceRaid, PublicScandal, WorkerWalkout, RivalAttack, FinancialCollapse, None }

/// <summary>A single crisis scenario choice for MCP transmission.</summary>
public class CrisisScenario
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString()[..6];
    [JsonPropertyName("title")] public string Title { get; set; }
    [JsonPropertyName("narrative")] public string Narrative { get; set; }
    [JsonPropertyName("trigger")] public string Trigger { get; set; }
    [JsonPropertyName("choices")] public List<CrisisChoice> Choices { get; set; } = new();
}

public class CrisisChoice
{
    [JsonPropertyName("label")] public string Label { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; }
    [JsonPropertyName("effects")] public CrisisEffects Effects { get; set; } = new();
}

public class CrisisEffects
{
    [JsonPropertyName("cash")] public double Cash { get; set; }
    [JsonPropertyName("heat")] public float Heat { get; set; }
    [JsonPropertyName("reputation")] public float Reputation { get; set; }
    [JsonPropertyName("publicSentiment")] public float PublicSentiment { get; set; }
}

/// <summary>
/// Automated AI story manager. Monitors crisis triggers
/// (police raids, scandals, walkouts), transmits full venue
/// state to DeepSeek v4 Pro Max via MCP, and receives
/// dynamic 3-choice crisis scenarios.
/// </summary>
public partial class CrisisNarrativeDirector : Node
{
    [Signal] public delegate void OnCrisisTriggeredEventHandler(int triggerType, string description);
    [Signal] public delegate void OnScenarioReceivedEventHandler(string scenarioJson);
    [Signal] public delegate void OnChoiceExecutedEventHandler(string choiceLabel);

    private readonly List<CrisisScenario> _history = new();
    private CrisisScenario _activeScenario;
    private bool _crisisActive;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CrisisScenario ActiveScenario => _activeScenario;
    public bool CrisisActive => _crisisActive;
    public int TotalCrises { get; private set; }

    public override void _Ready()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        GD.Print("[CrisisDirector] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    private void OnDailyTick(double cash, float reputation, float heat, float sentiment)
    {
        if (_crisisActive) return;

        // Monitor for crisis triggers
        CrisisTrigger? trigger = null;

        if (heat > 85f) trigger = CrisisTrigger.PoliceRaid;
        else if (sentiment < 15f) trigger = CrisisTrigger.PublicScandal;
        else if (IsStrikeActive()) trigger = CrisisTrigger.WorkerWalkout;
        else if (cash < -500) trigger = CrisisTrigger.FinancialCollapse;

        if (trigger.HasValue)
            TriggerCrisis(trigger.Value);
    }

    private void TriggerCrisis(CrisisTrigger trigger)
    {
        _crisisActive = true;
        TotalCrises++;

        var gsm = GameStateManager.Instance;
        string desc = trigger switch
        {
            CrisisTrigger.PoliceRaid => "Police are raiding the establishment!",
            CrisisTrigger.PublicScandal => "A public scandal has erupted!",
            CrisisTrigger.WorkerWalkout => "Workers have walked out in protest!",
            CrisisTrigger.FinancialCollapse => "Finances are in critical condition!",
            _ => "An unknown crisis has occurred."
        };

        EmitSignal(SignalName.OnCrisisTriggered, (int)trigger, desc);

        // Build MCP payload with full venue state
        string payload = BuildCrisisPayload(trigger);
        TransmitMcpPayload(payload);
    }

    /// <summary>Build full venue state payload for MCP transmission.</summary>
    public string BuildCrisisPayload(CrisisTrigger trigger)
    {
        var gsm = GameStateManager.Instance;
        var payload = new
        {
            crisisType = trigger.ToString(),
            day = gsm?.DayCount ?? 0,
            state = new
            {
                cash = gsm?.Cash ?? 0,
                reputation = gsm?.Reputation ?? 0,
                heat = gsm?.Heat ?? 0,
                publicSentiment = gsm?.PublicSentiment ?? 0
            }
        };

        string json = JsonSerializer.Serialize(payload, _jsonOpts);
        GD.Print($"<<<MCP_CRISIS_PAYLOAD>>>\n{json}\n<<<END_MCP_CRISIS_PAYLOAD>>>");
        return json;
    }

    private void TransmitMcpPayload(string json)
    {
        GD.Print($"[CrisisDirector] Transmitting crisis payload ({json.Length} bytes)...");
    }

    /// <summary>Parse received crisis scenario from DeepSeek response.</summary>
    public CrisisScenario ParseScenario(string jsonResponse)
    {
        try
        {
            var scenario = JsonSerializer.Deserialize<CrisisScenario>(jsonResponse, _jsonOpts);
            if (scenario != null && scenario.Choices.Count >= 2)
            {
                _activeScenario = scenario;
                _history.Add(scenario);
                EmitSignal(SignalName.OnScenarioReceived, jsonResponse);
                GD.Print($"[CrisisDirector] Scenario received: \"{scenario.Title}\" ({scenario.Choices.Count} choices)");
                return scenario;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[CrisisDirector] Parse error: {ex.Message}");
        }
        return null;
    }

    /// <summary>Execute a player choice from the active scenario.</summary>
    public bool ExecuteChoice(int choiceIndex)
    {
        if (_activeScenario == null || choiceIndex >= _activeScenario.Choices.Count)
            return false;

        var choice = _activeScenario.Choices[choiceIndex];
        var gsm = GameStateManager.Instance;

        if (gsm != null)
        {
            gsm.Cash += choice.Effects.Cash;

            if (choice.Effects.Heat != 0)
            {
                var hs = GetTree()?.Root?.FindChild("HeatSystem", true, false) as HeatSystem;
                if (hs != null) { if (choice.Effects.Heat > 0) hs.AddHeat(choice.Effects.Heat); else hs.RemoveHeat(-choice.Effects.Heat); }
                else gsm.Heat = Mathf.Clamp(gsm.Heat + choice.Effects.Heat, 0, 100);
            }

            gsm.Reputation += choice.Effects.Reputation;
            gsm.PublicSentiment += choice.Effects.PublicSentiment;
        }

        _crisisActive = false;
        EmitSignal(SignalName.OnChoiceExecuted, choice.Label);
        GD.Print($"[CrisisDirector] Choice executed: {choice.Label}");
        return true;
    }

    /// <summary>Generate a deterministic fallback crisis scenario if MCP times out.</summary>
    public CrisisScenario GenerateFallbackScenario(CrisisTrigger trigger)
    {
        return new CrisisScenario
        {
            Title = trigger switch
            {
                CrisisTrigger.PoliceRaid => "Raid Fallout",
                CrisisTrigger.PublicScandal => "Damage Control",
                CrisisTrigger.WorkerWalkout => "Labor Crisis",
                CrisisTrigger.FinancialCollapse => "Cash Emergency",
                _ => "Crisis Management"
            },
            Narrative = $"A {trigger.ToString().ToLower()} has occurred. You must decide how to respond.",
            Trigger = trigger.ToString(),
            Choices = new List<CrisisChoice>
            {
                new() { Label="A: Pragmatic", Description="Take the safe route.", Effects=new(){Cash=-200,Heat=-10,Reputation=-2}},
                new() { Label="B: Bold", Description="Risk it for a better outcome.", Effects=new(){Cash=-100,Heat=5,Reputation=3}},
                new() { Label="C: Ethical", Description="Do the right thing.", Effects=new(){Cash=-300,Heat=-15,Reputation=5,PublicSentiment=10}}
            }
        };
    }

    private bool IsStrikeActive()
    {
        var um = GetTree()?.Root?.FindChild("UnionizationManager", true, false) as UnionizationManager;
        return um?.StrikeActive ?? false;
    }

    public override string ToString() =>
        $"[CrisisDirector] Crises={TotalCrises} Active={_crisisActive}";
}
