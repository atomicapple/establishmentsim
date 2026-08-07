using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A single milestone within a narrative arc.</summary>
public class ArcMilestone
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public int Order { get; set; }
    public bool IsComplete { get; set; }
    public int CompletedDay { get; set; }
    public Func<bool> CompletionCondition { get; set; }
    public Action OnComplete { get; set; }
}

/// <summary>A full narrative arc spanning a playthrough.</summary>
public class NarrativeArc
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsComplete { get; set; }
    public int StartedDay { get; set; }
    public int CompletedDay { get; set; }
    public List<ArcMilestone> Milestones { get; set; } = new();
    public string EndingTitle { get; set; }
    public string EndingDescription { get; set; }
    public int CurrentMilestoneIndex { get; set; }
    public string Branch { get; set; }  // "good", "neutral", "bad"
}

/// <summary>
/// Tracks multi-stage storyline arcs across entire playthroughs.
/// Evaluates game state on weekly ticks to advance milestones,
/// spawn unique high-stakes events, and alter campaign endings.
/// </summary>
public partial class NarrativeArcTracker : Node, ISaveableSystem
{
    [Signal] public delegate void OnArcStartedEventHandler(string arcId, string title);
    [Signal] public delegate void OnMilestoneCompletedEventHandler(string arcId, string milestoneTitle, int order);
    [Signal] public delegate void OnArcCompletedEventHandler(string arcId, string endingTitle, string branch);
    [Signal] public delegate void OnHighStakesEventTriggeredEventHandler(string arcId, string eventDescription);

    private readonly List<NarrativeArc> _arcs = new();
    private readonly List<NarrativeArc> _completedArcs = new();
    private int _lastWeeklyCheck;

    public IReadOnlyList<NarrativeArc> ActiveArcs => _arcs.Where(a => a.IsActive && !a.IsComplete).ToList();
    public IReadOnlyList<NarrativeArc> CompletedArcs => _completedArcs;
    public int TotalArcsCompleted => _completedArcs.Count;

    public override void _Ready()
    {
        BuildDefaultArcs();
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        GD.Print($"[NarrativeArcTracker] Initialized with {_arcs.Count} story arcs.");
    }

    private void BuildDefaultArcs()
    {
        // ── Arc 1: Mayoral Campaign ─────────────────────────────────
        _arcs.Add(new NarrativeArc
        {
            Id = "mayoral_campaign", Title = "The Mayoral Campaign",
            Description = "A city election is approaching. Political favors and public sentiment will determine who controls the city.",
            Branch = "neutral",
            Milestones = new List<ArcMilestone>
            {
                new() { Id="mc_1", Order=1, Title="Political Awareness", Description="Learn of the upcoming mayoral election.",
                    CompletionCondition = () => (GameStateManager.Instance?.DayCount ?? 0) >= 50,
                    OnComplete = () => GD.Print("[NarrativeArc] Mayoral campaign: Election rumors begin.") },
                new() { Id="mc_2", Order=2, Title="Back a Candidate", Description="Choose a mayoral candidate to support.",
                    CompletionCondition = () => (GameStateManager.Instance?.Reputation ?? 0) >= 40,
                    OnComplete = () => GD.Print("[NarrativeArc] Mayoral campaign: Candidate chosen.") },
                new() { Id="mc_3", Order=3, Title="Campaign Finance", Description="Fund the campaign with $5,000 in contributions.",
                    CompletionCondition = () => FindLedgerNet() >= 5000,
                    OnComplete = () => GD.Print("[NarrativeArc] Mayoral campaign: Campaign funded.") },
                new() { Id="mc_4", Order=4, Title="Election Day", Description="The election results depend on Public Sentiment and Political Favors.",
                    CompletionCondition = () => (GameStateManager.Instance?.DayCount ?? 0) >= 200,
                    OnComplete = () => ResolveMayoralElection() }
            }
        });

        // ── Arc 2: Syndicate War ────────────────────────────────────
        _arcs.Add(new NarrativeArc
        {
            Id = "syndicate_war", Title = "Syndicate War",
            Description = "Rival factions are mobilizing. Territory, power, and survival are at stake.",
            Branch = "neutral",
            Milestones = new List<ArcMilestone>
            {
                new() { Id="sw_1", Order=1, Title="Rising Tensions", Description="Rival aggression surpasses 50%.",
                    CompletionCondition = () => GetRivalAggression() > 50f,
                    OnComplete = () => GD.Print("[NarrativeArc] Syndicate war: Tensions rising.") },
                new() { Id="sw_2", Order=2, Title="First Blood", Description="Survive a hostile takeover attempt.",
                    CompletionCondition = () => (GameStateManager.Instance?.Heat ?? 0) > 70f && (GameStateManager.Instance?.DayCount ?? 0) > 30,
                    OnComplete = () => TriggerHighStakesEvent("sw_2", "A rival crew attempts to take over Iron Row. Security must respond.") },
                new() { Id="sw_3", Order=3, Title="Territory Control", Description="Own at least 3 districts.",
                    CompletionCondition = () => GetOwnedDistrictCount() >= 3,
                    OnComplete = () => GD.Print("[NarrativeArc] Syndicate war: Territory secured.") },
                new() { Id="sw_4", Order=4, Title="War Conclusion", Description="Eliminate rival power or negotiate peace.",
                    CompletionCondition = () => GetRivalAggression() < 20f || (GameStateManager.Instance?.DayCount ?? 0) >= 300,
                    OnComplete = () => ResolveSyndicateWar() }
            }
        });

        // ── Arc 3: Federal Investigation ────────────────────────────
        _arcs.Add(new NarrativeArc
        {
            Id = "federal_investigation", Title = "Federal Investigation",
            Description = "The Feds are sniffing around. High Heat and public scandals attract federal attention.",
            Branch = "neutral",
            Milestones = new List<ArcMilestone>
            {
                new() { Id="fi_1", Order=1, Title="Rumors of Investigation", Description="Heat exceeds 60 and reputation is low.",
                    CompletionCondition = () => (GameStateManager.Instance?.Heat ?? 0) > 60f && (GameStateManager.Instance?.Reputation ?? 100) < 30f,
                    OnComplete = () => GD.Print("[NarrativeArc] Federal investigation: Rumors begin.") },
                new() { Id="fi_2", Order=2, Title="Subpoena Arrives", Description="A federal subpoena is issued. Must hire legal defense.",
                    CompletionCondition = () => (GameStateManager.Instance?.DayCount ?? 0) >= (GetArc("federal_investigation")?.StartedDay ?? 0) + 30,
                    OnComplete = () => TriggerHighStakesEvent("fi_2", "Federal agents serve a subpoena. Legal defense costs $3,000.") },
                new() { Id="fi_3", Order=3, Title="Evidence Suppression", Description="Burn Capital Favors or bribe to suppress evidence.",
                    CompletionCondition = () => GetDAFavor() > 60f || GetBurnCount() >= 3,
                    OnComplete = () => GD.Print("[NarrativeArc] Federal investigation: Evidence suppressed.") },
                new() { Id="fi_4", Order=4, Title="Verdict", Description="The investigation concludes. DAFavor determines outcome.",
                    CompletionCondition = () => (GameStateManager.Instance?.DayCount ?? 0) >= (GetArc("federal_investigation")?.StartedDay ?? 0) + 90,
                    OnComplete = () => ResolveFederalInvestigation() }
            }
        });
    }

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        int day = GameStateManager.Instance?.DayCount ?? 0;

        // Start arcs based on conditions
        foreach (var arc in _arcs.Where(a => !a.IsActive))
        {
            if (ShouldStartArc(arc, day))
                StartArc(arc, day);
        }

        // Weekly milestone check (every 7 days)
        if (day > 0 && day % 7 == 0 && day != _lastWeeklyCheck)
        {
            _lastWeeklyCheck = day;
            AdvanceArcs();
        }
    }

    private bool ShouldStartArc(NarrativeArc arc, int day)
    {
        return arc.Id switch
        {
            "mayoral_campaign" => day >= 30,
            "syndicate_war" => day >= 40 && GetRivalAggression() > 30f,
            "federal_investigation" => day >= 60 && (GameStateManager.Instance?.Heat ?? 0) > 50f,
            _ => day >= 50
        };
    }

    private void StartArc(NarrativeArc arc, int day)
    {
        arc.IsActive = true;
        arc.StartedDay = day;
        EmitSignal(SignalName.OnArcStarted, arc.Id, arc.Title);
        GD.Print($"[NarrativeArcTracker] Arc started: \"{arc.Title}\" on day {day}.");
    }

    private void AdvanceArcs()
    {
        foreach (var arc in _arcs.Where(a => a.IsActive && !a.IsComplete))
        {
            while (arc.CurrentMilestoneIndex < arc.Milestones.Count)
            {
                var milestone = arc.Milestones[arc.CurrentMilestoneIndex];
                if (milestone.IsComplete) { arc.CurrentMilestoneIndex++; continue; }

                if (milestone.CompletionCondition != null && milestone.CompletionCondition())
                {
                    milestone.IsComplete = true;
                    milestone.CompletedDay = GameStateManager.Instance?.DayCount ?? 0;
                    milestone.OnComplete?.Invoke();

                    EmitSignal(SignalName.OnMilestoneCompleted, arc.Id, milestone.Title, milestone.Order);
                    GD.Print($"[NarrativeArcTracker] {arc.Title}: Milestone {milestone.Order} complete — \"{milestone.Title}\"");
                }
                break;
            }

            // Check if arc is fully complete
            if (arc.CurrentMilestoneIndex >= arc.Milestones.Count && !arc.IsComplete)
            {
                arc.IsComplete = true;
                arc.CompletedDay = GameStateManager.Instance?.DayCount ?? 0;
                _completedArcs.Add(arc);

                EmitSignal(SignalName.OnArcCompleted, arc.Id, arc.EndingTitle ?? arc.Title, arc.Branch);
                GD.Print($"[NarrativeArcTracker] Arc complete: \"{arc.Title}\" [{arc.Branch}] — {arc.EndingDescription}");
            }
        }
    }

    // ── Arc Resolution Methods ─────────────────────────────────────────

    private void ResolveMayoralElection()
    {
        var arc = GetArc("mayoral_campaign");
        if (arc == null) return;

        float sentiment = GameStateManager.Instance?.PublicSentiment ?? 50;
        float daFavor = GetDAFavor();
        float commFavor = GetCommissionerFavor();

        if (sentiment > 60 && daFavor > 50)
        {
            arc.Branch = "good";
            arc.EndingTitle = "Ally Elected Mayor";
            arc.EndingDescription = "Your supported candidate wins. Heat accumulation −30% permanently. New zoning permits unlocked.";
            // Apply permanent bonus
        }
        else if (sentiment < 30 || daFavor < 20)
        {
            arc.Branch = "bad";
            arc.EndingTitle = "Hostile Mayor Elected";
            arc.EndingDescription = "An anti-vice mayor takes office. Police Crackdown phase forced. All bribes +50% cost.";
        }
        else
        {
            arc.Branch = "neutral";
            arc.EndingTitle = "Status Quo Maintained";
            arc.EndingDescription = "The election changes nothing. Business continues as usual.";
        }
    }

    private void ResolveSyndicateWar()
    {
        var arc = GetArc("syndicate_war");
        if (arc == null) return;

        float aggression = GetRivalAggression();
        int districts = GetOwnedDistrictCount();

        if (aggression < 20f && districts >= 3)
        {
            arc.Branch = "good";
            arc.EndingTitle = "Underworld Dominance";
            arc.EndingDescription = "You've crushed the opposition. All rival syndicates pay tribute. +$500/day passive income.";
        }
        else if (aggression > 60f)
        {
            arc.Branch = "bad";
            arc.EndingTitle = "Forced into Retreat";
            arc.EndingDescription = "The syndicates overpowered you. Lose control of weakest district. Reputation −20.";
        }
        else
        {
            arc.Branch = "neutral";
            arc.EndingTitle = "Uneasy Truce";
            arc.EndingDescription = "An armistice is signed. Tensions persist but open warfare ends.";
        }
    }

    private void ResolveFederalInvestigation()
    {
        var arc = GetArc("federal_investigation");
        if (arc == null) return;

        float daFavor = GetDAFavor();

        if (daFavor > 70)
        {
            arc.Branch = "good";
            arc.EndingTitle = "Case Dismissed";
            arc.EndingDescription = "The DA quashes the investigation. Federal heat permanently −20. For now.";
        }
        else if (daFavor < 30)
        {
            arc.Branch = "bad";
            arc.EndingTitle = "Indictment";
            arc.EndingDescription = "Federal charges filed. Assets frozen ($5,000 lost). Police Crackdown phase forced.";
        }
        else
        {
            arc.Branch = "neutral";
            arc.EndingTitle = "Settlement";
            arc.EndingDescription = "A quiet settlement is reached. $2,000 fine. Investigation closed.";
        }
    }

    private void TriggerHighStakesEvent(string arcId, string description)
    {
        EmitSignal(SignalName.OnHighStakesEventTriggered, arcId, description);
        GD.Print($"[NarrativeArcTracker] HIGH-STAKES EVENT [{arcId}]: {description}");
    }

    // ── State Queries ──────────────────────────────────────────────────

    private NarrativeArc GetArc(string id) => _arcs.Find(a => a.Id == id);

    private float GetRivalAggression()
    {
        var rai = GetTree()?.Root?.FindChild("SyndicateRivalAI", true, false) as SyndicateRivalAI;
        return rai?.Syndicates.Any() == true ? rai.Syndicates.Average(s => s.Aggression) : 0f;
    }

    private int GetOwnedDistrictCount()
    {
        var rem = GetTree()?.Root?.FindChild("RealEstateMarket", true, false) as RealEstateMarket;
        return rem?.OwnedCount ?? 0;
    }

    private float GetDAFavor()
    {
        var pis = GetTree()?.Root?.FindChild("PoliticalInfluenceSystem", true, false) as PoliticalInfluenceSystem;
        return pis?.DAFavor ?? 0f;
    }

    private float GetCommissionerFavor()
    {
        var pis = GetTree()?.Root?.FindChild("PoliticalInfluenceSystem", true, false) as PoliticalInfluenceSystem;
        return pis?.CommissionerFavor ?? 0f;
    }

    private int GetBurnCount()
    {
        var bn = GetTree()?.Root?.FindChild("BlackmailNetwork", true, false) as BlackmailNetwork;
        return bn?.TotalFavorsBurned ?? 0;
    }

    private double FindLedgerNet()
    {
        var fl = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;
        return fl?.GetNetProfit() ?? 0;
    }

    /// <summary>Get a campaign summary for the ending screen.</summary>
    public string GetCampaignSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== NARRATIVE ARC SUMMARY ===");
        foreach (var arc in _arcs.Where(a => a.IsComplete))
            sb.AppendLine($"  {arc.Title}: {arc.EndingTitle} [{arc.Branch}] — {arc.EndingDescription}");
        if (_completedArcs.Count == 0)
            sb.AppendLine("  No story arcs completed yet.");
        sb.AppendLine($"  Total arcs completed: {_completedArcs.Count}/{_arcs.Count}");
        return sb.ToString();
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "narrative";

    /// <summary>
    /// Capture arc *progress* only.
    ///
    /// Arcs carry live <c>Func&lt;bool&gt;</c> completion conditions and
    /// <c>Action</c> callbacks, which cannot be serialized. So the save
    /// records which arcs and milestones are done, and restore matches those
    /// against the arc definitions rebuilt in _Ready — keeping the delegates
    /// intact and letting a later build change an arc's logic without
    /// invalidating existing campaigns.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject CaptureState()
    {
        var arcs = new System.Text.Json.Nodes.JsonArray();

        foreach (var arc in _arcs)
        {
            var milestones = new System.Text.Json.Nodes.JsonArray();
            foreach (var m in arc.Milestones)
            {
                milestones.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = m.Id,
                    ["isComplete"] = m.IsComplete,
                    ["completedDay"] = m.CompletedDay
                });
            }

            arcs.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = arc.Id,
                ["isActive"] = arc.IsActive,
                ["isComplete"] = arc.IsComplete,
                ["startedDay"] = arc.StartedDay,
                ["completedDay"] = arc.CompletedDay,
                ["currentMilestoneIndex"] = arc.CurrentMilestoneIndex,
                ["branch"] = arc.Branch,
                ["milestones"] = milestones
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["arcs"] = arcs,
            ["lastWeeklyCheck"] = _lastWeeklyCheck
        };
    }

    public void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state == null) return;

        _lastWeeklyCheck = (int?)state["lastWeeklyCheck"] ?? 0;

        if (state["arcs"] is not System.Text.Json.Nodes.JsonArray arcs) return;

        _completedArcs.Clear();

        foreach (var node in arcs)
        {
            if (node is not System.Text.Json.Nodes.JsonObject entry) continue;

            var arcId = (string)entry["id"];
            var arc = _arcs.Find(a => a.Id == arcId);
            if (arc == null)
            {
                // An arc from an older build that this one no longer defines.
                GD.Print($"[NarrativeArcTracker] Save references unknown arc '{arcId}'. Skipped.");
                continue;
            }

            arc.IsActive = (bool?)entry["isActive"] ?? false;
            arc.IsComplete = (bool?)entry["isComplete"] ?? false;
            arc.StartedDay = (int?)entry["startedDay"] ?? 0;
            arc.CompletedDay = (int?)entry["completedDay"] ?? 0;
            arc.CurrentMilestoneIndex = (int?)entry["currentMilestoneIndex"] ?? 0;
            arc.Branch = (string)entry["branch"] ?? "";

            if (entry["milestones"] is System.Text.Json.Nodes.JsonArray milestones)
            {
                foreach (var mNode in milestones)
                {
                    if (mNode is not System.Text.Json.Nodes.JsonObject mEntry) continue;

                    var milestoneId = (string)mEntry["id"];
                    var milestone = arc.Milestones.Find(m => m.Id == milestoneId);
                    if (milestone == null) continue;

                    milestone.IsComplete = (bool?)mEntry["isComplete"] ?? false;
                    milestone.CompletedDay = (int?)mEntry["completedDay"] ?? 0;
                }
            }

            // Guard against a save whose index outran a shortened arc.
            arc.CurrentMilestoneIndex = Math.Clamp(arc.CurrentMilestoneIndex, 0, arc.Milestones.Count);

            if (arc.IsComplete) _completedArcs.Add(arc);
        }

        GD.Print($"[NarrativeArcTracker] Restored: " +
                 $"{_arcs.Count(a => a.IsActive && !a.IsComplete)} active, " +
                 $"{_completedArcs.Count} complete.");
    }

    public override string ToString() =>
        $"[NarrativeArcTracker] Active={_arcs.Count(a=>a.IsActive&&!a.IsComplete)} " +
        $"Complete={_completedArcs.Count}/{_arcs.Count}";
}
