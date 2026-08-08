using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Crisis trigger type.</summary>
public enum CrisisTrigger
{
    PoliceRaid,
    PublicScandal,
    WorkerWalkout,
    RivalAttack,
    FinancialCollapse,

    /// <summary>Someone on the roster broke. Fired from the break system.</summary>
    StaffBreakdown,

    /// <summary>The house's name is gone and nobody is coming.</summary>
    ReputationCollapse,

    None
}

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

        // Deferred: these are sibling systems under GameBootstrap and the
        // node names are load-bearing, so the lookup has to happen after the
        // whole set has been constructed rather than during our own _Ready.
        CallDeferred(nameof(SubscribeToEvents));

        GD.Print("[CrisisDirector] Initialized.");
    }

    /// <summary>
    /// Listen for the things that arrive as events rather than as a state a
    /// daily poll can notice.
    ///
    /// A staff member breaking is a moment, not a condition — by the next
    /// tick the break system has already applied its consequences and moved
    /// on, so polling would miss it entirely. Same for a rival making a move.
    /// </summary>
    private void SubscribeToEvents()
    {
        var breaks = GetTree()?.Root?.FindChild(
            "PsychologicalBreakSystem", true, false) as PsychologicalBreakSystem;

        if (breaks != null) breaks.OnPsychologicalBreak += OnStaffBroke;

        var syndicates = GetTree()?.Root?.FindChild(
            "SyndicateRivalAI", true, false) as SyndicateRivalAI;

        if (syndicates != null) syndicates.OnRivalAction += OnRivalMoved;
    }

    /// <summary>
    /// Someone broke. The break system already models four distinct kinds of
    /// collapse with distinct consequences; until now none of them reached
    /// the player as a decision, only as a number moving.
    /// </summary>
    private void OnStaffBroke(
        string staffName, int eventType, int traumaSource,
        float stressLevel, float traumaLevel, int day, string narrative)
    {
        _pendingSubject = staffName;
        RaiseCrisis(CrisisTrigger.StaffBreakdown);
    }

    /// <summary>
    /// A rival did something. <see cref="CrisisTrigger.RivalAttack"/> has been
    /// in the enum since the file was written and has never had a condition
    /// attached to it, so it could not fire under any circumstances.
    /// </summary>
    private void OnRivalMoved(string syndicate, string action, float severity)
    {
        if (severity < RivalSeverityThreshold) return;

        _pendingSubject = syndicate;
        RaiseCrisis(CrisisTrigger.RivalAttack);
    }

    /// <summary>Who or what this crisis is about, for the scenario text.</summary>
    private string _pendingSubject;

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    /// <summary>
    /// Nights that must pass between crises. Without it a running strike
    /// satisfies its trigger every single night, and the player would face
    /// the same decision at every Ledger until they settled it.
    /// </summary>
    public int CooldownDays { get; set; } = 5;

    // ── Trigger thresholds ─────────────────────────────────────────────
    //
    // These were set at catastrophe level — heat above 85, sentiment below
    // 15, the house $500 in debt — and a 50-night run reached none of them.
    // The Ledger is meant to end with a decision often enough to be the
    // game's pacing heartbeat, and it ended with none at all.
    //
    // Measured against the harness rather than guessed. Heat alone gave two
    // crises in fifty nights and no amount of lowering the threshold gave
    // more, because each raid takes 25 heat off and heat accrues 1–2 a night
    // — the system suppressed itself for fifteen nights after every crisis.
    // The answer was more sources, not a lower number: a 50-night run now
    // faces five, across raids, rivals and the press.

    /// <summary>Heat above this raises a raid crisis.</summary>
    public float HeatThreshold { get; set; } = 35f;

    /// <summary>Public feeling below this raises a scandal.</summary>
    public float SentimentThreshold { get; set; } = 35f;

    /// <summary>Cash below this raises a solvency crisis.</summary>
    public double DebtThreshold { get; set; } = 0.0;

    /// <summary>Reputation below this raises a collapse-of-name crisis.</summary>
    public float ReputationThreshold { get; set; } = 30f;

    /// <summary>
    /// How forceful a rival's move has to be to become a crisis.
    ///
    /// <see cref="SyndicateRivalAI"/> logs severity on a 0–10 scale, not 0–1:
    /// intel gathering is 2, turf expansion 5, poaching 6, extortion 8,
    /// counter-sabotage 10. Set at 7, so only the two moves that are actually
    /// aimed at the house interrupt the player — a first pass at 0.5 caught
    /// every routine intel tick and produced eight crises in fifty nights.
    /// </summary>
    public float RivalSeverityThreshold { get; set; } = 7f;

    private int _lastCrisisDay = int.MinValue / 2;

    private void OnDailyTick(double cash, float reputation, float heat, float sentiment)
    {
        if (_crisisActive) return;

        var day = GameStateManager.Instance?.DayCount ?? 0;
        if (day - _lastCrisisDay < CooldownDays) return;

        // Monitor for crisis triggers
        CrisisTrigger? trigger = null;

        // Order is priority. The police outrank everything; a strike outranks
        // a bad week at the bank; the slow structural failures — losing the
        // house's name, running out of money — come last, because they are
        // still true tomorrow and the acute ones are not.
        if (heat > HeatThreshold) trigger = CrisisTrigger.PoliceRaid;
        else if (IsStrikeActive()) trigger = CrisisTrigger.WorkerWalkout;
        else if (sentiment < SentimentThreshold) trigger = CrisisTrigger.PublicScandal;
        else if (reputation < ReputationThreshold) trigger = CrisisTrigger.ReputationCollapse;
        else if (cash < DebtThreshold) trigger = CrisisTrigger.FinancialCollapse;

        if (trigger.HasValue) RaiseCrisis(trigger.Value);
    }

    /// <summary>
    /// The single gate every trigger passes through — polled or event-driven.
    /// Enforces the cooldown and the one-crisis-at-a-time rule in one place,
    /// so a new trigger source cannot accidentally bypass either.
    /// </summary>
    /// <returns>True if a crisis was raised.</returns>
    private bool RaiseCrisis(CrisisTrigger trigger)
    {
        if (_crisisActive) return false;

        var day = GameStateManager.Instance?.DayCount ?? 0;
        if (day - _lastCrisisDay < CooldownDays) return false;

        _lastCrisisDay = day;
        TriggerCrisis(trigger);
        return true;
    }

    /// <summary>
    /// Raise a crisis now, ignoring both the trigger conditions and the
    /// cooldown. The organic route needs heat above 85 or the house in debt,
    /// which no test or capture run can arrange incidentally.
    /// </summary>
    public void ForceCrisis(CrisisTrigger trigger)
    {
        if (_crisisActive) return;

        _lastCrisisDay = GameStateManager.Instance?.DayCount ?? 0;
        TriggerCrisis(trigger);
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
            CrisisTrigger.StaffBreakdown => $"{_pendingSubject ?? "Someone"} has broken down.",
            CrisisTrigger.ReputationCollapse => "The house has lost its name.",
            CrisisTrigger.RivalAttack => $"{_pendingSubject ?? "A rival"} is making a move.",
            _ => "An unknown crisis has occurred."
        };

        // The authored scenario is installed *first*, not as a timeout
        // fallback.
        //
        // This used to set _crisisActive and then print a payload for an
        // out-of-process LLM to answer. Nothing answers it, and _crisisActive
        // is only ever cleared inside ExecuteChoice — which needs a scenario
        // to execute a choice from. So the first crisis latched the director
        // permanently and no crisis ever reached the player. A core loop that
        // stalls when a language model is absent is not a core loop; the
        // authored path has to be the one that runs, with generation layered
        // on top of it.
        _activeScenario = GenerateFallbackScenario(trigger);
        _history.Add(_activeScenario);

        EmitSignal(SignalName.OnCrisisTriggered, (int)trigger, desc);
        EmitSignal(SignalName.OnScenarioReceived,
            JsonSerializer.Serialize(_activeScenario, _jsonOpts));

        // Still offered to anything listening on stdout. If a richer scenario
        // comes back before the player chooses, ParseScenario replaces this
        // one; if nothing ever comes back, the night carries on regardless.
        TransmitMcpPayload(BuildCrisisPayload(trigger));
    }

    /// <summary>
    /// Abandon the current crisis without applying any choice. The screen
    /// calls this if it is dismissed, so a closed window cannot leave the
    /// director latched — the failure mode this whole system had.
    /// </summary>
    public void DismissCrisis()
    {
        if (!_crisisActive) return;

        _crisisActive = false;
        _activeScenario = null;

        GD.Print("[CrisisDirector] Crisis dismissed without a decision.");
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
        if (_activeScenario == null ||
            choiceIndex < 0 || choiceIndex >= _activeScenario.Choices.Count)
            return false;

        var choice = _activeScenario.Choices[choiceIndex];
        var gsm = GameStateManager.Instance;

        if (gsm != null)
        {
            // Through the ledger, so a crisis decision appears in the books
            // as a line the player can find again later.
            var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;

            if (ledger != null && choice.Effects.Cash < 0)
                ledger.RecordExpense(ExpenseCategory.LegalDefense, -choice.Effects.Cash,
                    $"{_activeScenario.Title}: {choice.Label}");
            else if (ledger != null && choice.Effects.Cash > 0)
                ledger.RecordRevenue(RevenueCategory.InformationSales, choice.Effects.Cash,
                    $"{_activeScenario.Title}: {choice.Label}");
            else
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
        _activeScenario = null;

        EmitSignal(SignalName.OnChoiceExecuted, choice.Label);
        GD.Print($"[CrisisDirector] Choice executed: {choice.Label}");
        return true;
    }

    /// <summary>
    /// The authored scenario for a trigger. This is the primary path, not a
    /// timeout fallback, so it has to be worth reading on its own.
    ///
    /// Each one offers the same three shapes — pay it away, brazen it out, or
    /// take the honest loss — but the shapes cost different things depending
    /// on what went wrong, and none of them is free.
    /// </summary>
    public CrisisScenario GenerateFallbackScenario(CrisisTrigger trigger) => trigger switch
    {
        CrisisTrigger.PoliceRaid => new CrisisScenario
        {
            Title = "The Raid",
            Trigger = trigger.ToString(),
            Narrative =
                "They came through the front door at half past one and did not " +
                "pretend to be looking for anything in particular. Two of your " +
                "people are in a van on the street. The sergeant is standing in " +
                "your lobby with his hat still on, waiting to be spoken to.",
            Choices =
            {
                new()
                {
                    Label = "Pay him where he stands",
                    Description = "$700, and the van doors open. Everyone sees you do it.",
                    Effects = new() { Cash = -700, Heat = -25, Reputation = -4, PublicSentiment = -5 }
                },
                new()
                {
                    Label = "Say nothing and let it run",
                    Description = "Costs nothing tonight. They take what they came for.",
                    Effects = new() { Cash = -220, Heat = -8, Reputation = -8, PublicSentiment = -2 }
                },
                new()
                {
                    Label = "Call a lawyer at this hour",
                    Description = "$550 and a long night, but it happens on the record.",
                    Effects = new() { Cash = -550, Heat = -18, Reputation = 4, PublicSentiment = 8 }
                }
            }
        },

        CrisisTrigger.PublicScandal => new CrisisScenario
        {
            Title = "In the Morning Edition",
            Trigger = trigger.ToString(),
            Narrative =
                "A columnist who has never set foot in the house has written " +
                "eleven inches about it anyway. Half of it is wrong and the " +
                "wrong half is the memorable half. By noon three neighbours " +
                "have found the courage to be quoted.",
            Choices =
            {
                new()
                {
                    Label = "Buy the retraction",
                    Description = "$500 to the right desk. It runs on page nine.",
                    Effects = new() { Cash = -500, Heat = 3, Reputation = 2, PublicSentiment = 6 }
                },
                new()
                {
                    Label = "Answer it in public",
                    Description = "Free, and it keeps the story alive another week.",
                    Effects = new() { Cash = 0, Heat = 8, Reputation = 6, PublicSentiment = -6 }
                },
                new()
                {
                    Label = "Close for three nights",
                    Description = "Lose the trade. Let them find something else to write about.",
                    Effects = new() { Cash = -850, Heat = -15, Reputation = -2, PublicSentiment = 14 }
                }
            }
        },

        CrisisTrigger.WorkerWalkout => new CrisisScenario
        {
            Title = "Nobody Came Down",
            Trigger = trigger.ToString(),
            Narrative =
                "The doors are open, the lamps are lit, and the first floor is " +
                "empty. They are all upstairs in one room with the door shut, " +
                "and they have been in there long enough to have agreed on " +
                "something.",
            Choices =
            {
                new()
                {
                    Label = "Go up and listen",
                    Description = "Costs an evening's takings and some of your standing with them.",
                    Effects = new() { Cash = -260, Heat = 0, Reputation = 2, PublicSentiment = 8 }
                },
                new()
                {
                    Label = "Open without them",
                    Description = "Serve who you can. They will remember which you chose.",
                    Effects = new() { Cash = 200, Heat = 5, Reputation = -3, PublicSentiment = -10 }
                },
                new()
                {
                    Label = "Send everyone home paid",
                    Description = "$450 for a night that earns nothing, and no grievance left standing.",
                    Effects = new() { Cash = -450, Heat = -5, Reputation = 3, PublicSentiment = 12 }
                }
            }
        },

        CrisisTrigger.FinancialCollapse => new CrisisScenario
        {
            Title = "The Books Do Not Close",
            Trigger = trigger.ToString(),
            Narrative =
                "Upkeep, wages and the licence renewal all fall in the same " +
                "week, and there is not enough behind the bar to meet any two " +
                "of them. Somebody is going to be told they are not being paid " +
                "on Friday. The only question is who.",
            Choices =
            {
                new()
                {
                    Label = "Borrow from Ash Row",
                    Description = "$2,000 tonight. They will want it back, and they will say when.",
                    Effects = new() { Cash = 2000, Heat = 12, Reputation = -3, PublicSentiment = -4 }
                },
                new()
                {
                    Label = "Sell what is not nailed down",
                    Description = "$900 and the rooms look poorer for it.",
                    Effects = new() { Cash = 900, Heat = 0, Reputation = -5, PublicSentiment = 0 }
                },
                new()
                {
                    Label = "Tell the staff the truth",
                    Description = "Raises nothing. Some of them stay anyway.",
                    Effects = new() { Cash = 0, Heat = -3, Reputation = 4, PublicSentiment = 10 }
                }
            }
        },

        CrisisTrigger.StaffBreakdown => new CrisisScenario
        {
            Title = "She Is Not Coming Down",
            Trigger = trigger.ToString(),
            Narrative =
                $"{_pendingSubject ?? "One of the women"} got halfway through the " +
                "evening and stopped. Not a scene — she simply sat down on the " +
                "stairs and would not be moved, and a client is still waiting " +
                "in a room with the door open, working out how offended to be.",
            Choices =
            {
                new()
                {
                    Label = "Put her in a cab and pay the client off",
                    Description = "$320. She keeps her place and the client keeps quiet.",
                    Effects = new() { Cash = -320, Heat = 0, Reputation = -1, PublicSentiment = 6 }
                },
                new()
                {
                    Label = "Have someone else take the room",
                    Description = "The evening continues. Everyone watches you decide that.",
                    Effects = new() { Cash = 150, Heat = 3, Reputation = 1, PublicSentiment = -12 }
                },
                new()
                {
                    Label = "Shut the floor for the night",
                    Description = "Lose the takings. Nobody is asked to work through it.",
                    Effects = new() { Cash = -400, Heat = -4, Reputation = -2, PublicSentiment = 15 }
                }
            }
        },

        CrisisTrigger.ReputationCollapse => new CrisisScenario
        {
            Title = "An Empty House",
            Trigger = trigger.ToString(),
            Narrative =
                "Three of the four rooms went unused last night and the lamps " +
                "burned anyway. It is not that anyone is angry. It is that the " +
                "house has stopped being somewhere people think of going, and a " +
                "name takes longer to get back than it took to lose.",
            Choices =
            {
                new()
                {
                    Label = "Spend on the room that still gets used",
                    Description = "$600 into the one thing people still come for.",
                    Effects = new() { Cash = -600, Heat = 0, Reputation = 10, PublicSentiment = 2 }
                },
                new()
                {
                    Label = "Cut the rates until they come back",
                    Description = "Cheap tonight, and cheap is what the house becomes known for.",
                    Effects = new() { Cash = -200, Heat = 2, Reputation = 5, PublicSentiment = -6 }
                },
                new()
                {
                    Label = "Wait it out",
                    Description = "Costs nothing. Nothing improves either.",
                    Effects = new() { Cash = 0, Heat = -2, Reputation = 1, PublicSentiment = 0 }
                }
            }
        },

        CrisisTrigger.RivalAttack => new CrisisScenario
        {
            Title = "A Message From Across Town",
            Trigger = trigger.ToString(),
            Narrative =
                $"{_pendingSubject ?? "A rival house"} sent two men to stand " +
                "outside from ten until close. They did not come in and they " +
                "did not do anything. Every client who arrived had to walk past " +
                "them, and roughly half of them did not.",
            Choices =
            {
                new()
                {
                    Label = "Pay the fee they came for",
                    Description = "$650 and they stop. They will be back at the same time next month.",
                    Effects = new() { Cash = -650, Heat = 4, Reputation = -2, PublicSentiment = -3 }
                },
                new()
                {
                    Label = "Put your own men on the door",
                    Description = "$400 and it escalates. Everyone knows where that goes.",
                    Effects = new() { Cash = -400, Heat = 14, Reputation = 5, PublicSentiment = -5 }
                },
                new()
                {
                    Label = "Report it",
                    Description = "Free, and it puts the house on a list it was not on.",
                    Effects = new() { Cash = 0, Heat = 18, Reputation = 2, PublicSentiment = 10 }
                }
            }
        },

        _ => new CrisisScenario
        {
            Title = "A Difficult Night",
            Trigger = trigger.ToString(),
            Narrative = "Something has gone wrong and it will not wait until morning.",
            Choices =
            {
                new()
                {
                    Label = "Spend money on it",
                    Description = "$400, and it goes away quietly.",
                    Effects = new() { Cash = -400, Heat = -8, Reputation = 0 }
                },
                new()
                {
                    Label = "Leave it alone",
                    Description = "Free. It does not go away.",
                    Effects = new() { Cash = 0, Heat = 6, Reputation = -3 }
                }
            }
        }
    };

    private bool IsStrikeActive()
    {
        var um = GetTree()?.Root?.FindChild("UnionizationManager", true, false) as UnionizationManager;
        return um?.StrikeActive ?? false;
    }

    public override string ToString() =>
        $"[CrisisDirector] Crises={TotalCrises} Active={_crisisActive}";
}
