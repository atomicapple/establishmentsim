using Godot;
using com.IvanMurzak.McpPlugin;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Result DTOs ────────────────────────────────────────────────────────

public class PoliticalFavorResult
{
    public bool Success { get; set; }
    public float PrecinctFavor { get; set; }
    public float DAFavor { get; set; }
    public float CommissionerFavor { get; set; }
    public double TotalAllocation { get; set; }
    public bool ZoningUnlocked { get; set; }
    public string[] AvailablePermits { get; set; }
    public string Message { get; set; }
}

public class BlackmailResult
{
    public bool Success { get; set; }
    public string TargetId { get; set; }
    public double CashExtorted { get; set; }
    public float HeatReduction { get; set; }
    public int FavorsRemaining { get; set; }
    public string FavorUsed { get; set; }
    public string Message { get; set; }
}

public class TurfProtectionResult
{
    public bool Success { get; set; }
    public string DistrictId { get; set; }
    public double Cost { get; set; }
    public float RivalAggressionReduction { get; set; }
    public string[] AffectedSyndicates { get; set; }
    public string Message { get; set; }
}

public class RivalSabotageResult
{
    public bool Success { get; set; }
    public string RivalId { get; set; }
    public double Cost { get; set; }
    public float PowerReduction { get; set; }
    public float NewPower { get; set; }
    public float RespectChange { get; set; }
    public string Message { get; set; }
}

/// <summary>
/// MCP tool endpoints for syndicate operations and political manipulation.
/// All methods decorated with [AiTool] for ReflectorNet serialization.
/// </summary>
[AiToolType]
public static class SyndicateMcpTools
{
    /// <summary>Query current political favor levels and unlocked permits.</summary>
    [AiTool("query_political_favors", "Get current favor levels with Precinct Captain, DA, and Commissioner. Shows available zoning permits.")]
    public static PoliticalFavorResult QueryPoliticalFavors()
    {
        var result = new PoliticalFavorResult();
        var pol = FindNode<PoliticalInfluenceSystem>("PoliticalInfluenceSystem");

        if (pol == null)
        {
            result.Message = "PoliticalInfluenceSystem not found.";
            return result;
        }

        result.Success = true;
        result.PrecinctFavor = pol.PrecinctFavor;
        result.DAFavor = pol.DAFavor;
        result.CommissionerFavor = pol.CommissionerFavor;
        result.TotalAllocation = pol.TotalMonthlyAllocation;
        result.ZoningUnlocked = pol.ZoningPermitsUnlocked;

        var permits = new List<string>();
        foreach (var p in new[] { "basic_expansion", "liquor_license", "extended_hours", "gambling_permit" })
            if (pol.IsPermitAvailable(p)) permits.Add(p);
        result.AvailablePermits = permits.ToArray();

        result.Message = $"Favors: Precinct={pol.PrecinctFavor:F0} DA={pol.DAFavor:F0} Comm={pol.CommissionerFavor:F0}";
        return result;
    }

    /// <summary>Execute blackmail extortion against a target, burning a Capital Favor.</summary>
    [AiTool("execute_blackmail_extortion", "Extort cash from a target by burning a Capital Favor. Cash amount scales with favor rarity.")]
    public static BlackmailResult ExecuteBlackmailExtortion(string targetId)
    {
        var result = new BlackmailResult { TargetId = targetId ?? "unknown" };
        var bn = FindNode<BlackmailNetwork>("BlackmailNetwork");

        if (bn == null)
        {
            result.Message = "BlackmailNetwork not found.";
            return result;
        }

        double cash = bn.BurnForExtortion();
        result.Success = cash > 0;
        result.CashExtorted = cash;
        result.FavorsRemaining = bn.Inventory.Count;

        result.Message = cash > 0
            ? $"Extorted ${cash:F0} from {targetId}. {bn.Inventory.Count} favors remaining."
            : $"No suitable Capital Favors available for extortion against {targetId}.";

        return result;
    }

    /// <summary>Deploy turf protection in a district, reducing rival aggression.</summary>
    [AiTool("deploy_turf_protection", "Pay security to protect a district. Reduces rival syndicate aggression by 15.")]
    public static TurfProtectionResult DeployTurfProtection(string districtId)
    {
        var result = new TurfProtectionResult { DistrictId = districtId ?? "unknown" };
        var rivalAI = FindNode<SyndicateRivalAI>("SyndicateRivalAI");
        var gsm = GameStateManager.Instance;

        if (rivalAI == null)
        {
            result.Message = "SyndicateRivalAI not found.";
            return result;
        }

        double cost = 600;
        if (gsm != null && gsm.Cash < cost)
        {
            result.Message = $"Insufficient funds: need ${cost:F0}, have ${gsm.Cash:F0}.";
            return result;
        }

        rivalAI.HireSecurity(cost);
        result.Success = true;
        result.Cost = cost;
        result.RivalAggressionReduction = 15f;
        result.AffectedSyndicates = rivalAI.Syndicates.Select(s => s.Name).ToArray();

        result.Message = $"Turf protection deployed in {districtId}. Rival aggression -15.";
        return result;
    }

    /// <summary>Launch counter-sabotage against a rival syndicate.</summary>
    [AiTool("trigger_rival_sabotage", "Sabotage a rival syndicate's operations. Costs cash, reduces their power by 15 and aggression by 10.")]
    public static RivalSabotageResult TriggerRivalSabotage(string rivalId)
    {
        var result = new RivalSabotageResult { RivalId = rivalId ?? "unknown" };
        var rivalAI = FindNode<SyndicateRivalAI>("SyndicateRivalAI");
        var gsm = GameStateManager.Instance;

        if (rivalAI == null)
        {
            result.Message = "SyndicateRivalAI not found.";
            return result;
        }

        var syndicate = rivalAI.GetSyndicate(rivalId);
        if (syndicate == null)
        {
            result.Message = $"Unknown syndicate: '{rivalId}'. Known: {string.Join(", ", rivalAI.Syndicates.Select(s => s.Name))}";
            return result;
        }

        double cost = 400;
        if (gsm != null && gsm.Cash < cost)
        {
            result.Message = $"Insufficient funds: need ${cost:F0}.";
            return result;
        }

        float powerBefore = syndicate.Power;
        rivalAI.CounterSabotage(rivalId, cost);
        result.Success = true;
        result.Cost = cost;
        result.PowerReduction = powerBefore - syndicate.Power;
        result.NewPower = syndicate.Power;
        result.RespectChange = 10f;

        result.Message = $"Sabotaged {rivalId}: Power {powerBefore:F0}→{syndicate.Power:F0} (-{result.PowerReduction:F0}).";
        return result;
    }

    private static T FindNode<T>(string name) where T : Node
    {
        // Access scene tree through Engine.GetMainLoop()
        if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
            return tree.Root.FindChild(name, true, false) as T;
        return null;
    }
}
