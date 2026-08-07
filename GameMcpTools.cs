using Godot;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet;
using System;
using System.Linq;

// ── Tool Result DTOs (serialized by ReflectorNet) ──────────────────────

/// <summary>Result from execute_bribe_negotiation.</summary>
public class BribeResult
{
    public bool Success { get; set; }
    public string OfficerId { get; set; }
    public double AmountPaid { get; set; }
    public float HeatBefore { get; set; }
    public float HeatAfter { get; set; }
    public float HeatReduction { get; set; }
    public double RemainingCash { get; set; }
    public string Message { get; set; }

    public override string ToString() =>
        Success
            ? $"Bribe paid: ${AmountPaid:F0} → Heat {HeatBefore:F1}→{HeatAfter:F1} (-{HeatReduction:F1})"
            : $"Bribe failed: {Message}";
}

/// <summary>Result from query_staff_psychology.</summary>
public class StaffPsychologyResult
{
    public bool Found { get; set; }
    public string StaffName { get; set; }
    public string Role { get; set; }

    // RPG Stats
    public float Charisma { get; set; }
    public float Negotiation { get; set; }
    public float Discretion { get; set; }

    // Agency Variables
    public float Stress { get; set; }
    public float Satisfaction { get; set; }
    public float Trauma { get; set; }

    // Derived
    public float MaxSatisfaction { get; set; }
    public float EffectivenessRating { get; set; }
    public bool IsBurningOut { get; set; }
    public bool IsQuitRisk { get; set; }

    // Trauma source from PsychologicalBreakSystem
    public string DominantTraumaSource { get; set; }
    public string BreakRiskAssessment { get; set; }

    public override string ToString() =>
        Found
            ? $"{StaffName} ({Role}): Cha={Charisma:F0} Neg={Negotiation:F0} " +
              $"Dis={Discretion:F0} | Stress={Stress:F0} Sat={Satisfaction:F0} Trauma={Trauma:F0}"
            : $"Staff not found.";
}

/// <summary>Result from apply_policy_directive.</summary>
public class PolicyResult
{
    public bool Success { get; set; }
    public string DirectiveId { get; set; }
    public string PolicyName { get; set; }
    public int Tier { get; set; }
    public string Branch { get; set; }
    public string Effects { get; set; }
    public string Message { get; set; }

    // Current state after application
    public string ActiveBranch { get; set; }
    public int TotalEnactedPolicies { get; set; }
    public string ModifierSummary { get; set; }

    public override string ToString() =>
        Success
            ? $"Policy enacted: {PolicyName} (T{Tier} {Branch}) — {Effects}"
            : $"Policy failed: {Message}";
}

// ── GameMcpTools ───────────────────────────────────────────────────────

/// <summary>
/// Game-specific MCP tool endpoints registered via [AiTool] attributes.
/// These methods are discovered by McpPluginBuilder.WithToolsFromAssembly()
/// and exposed to the AI agent through the MCP protocol.
///
/// All results are returned as structured objects that ReflectorNet
/// serializes into the MCP tool response payload automatically.
/// </summary>
[AiToolType]
public static class GameMcpTools
{
    // ── Tool: execute_bribe_negotiation ────────────────────────────────

    /// <summary>
    /// Execute a bribe negotiation with a police precinct captain.
    /// Pay the specified amount to reduce establishment Heat.
    /// Higher heat levels reduce bribe effectiveness.
    /// </summary>
    /// <param name="officerId">Identifier for the corrupt officer (e.g. "precinct_captain", "beat_cop_3").</param>
    /// <param name="amount">Bribe amount in dollars. Minimum $50 for any effect.</param>
    /// <returns>BribeResult with heat reduction details.</returns>
    [AiTool(
        "execute_bribe_negotiation",
        "Bribe a police officer to reduce establishment Heat. Higher heat reduces effectiveness.")]
    public static BribeResult ExecuteBribeNegotiation(string officerId, double amount)
    {
        var result = new BribeResult
        {
            OfficerId = officerId ?? "precinct_captain",
            AmountPaid = amount
        };

        // Find HeatSystem in the scene tree
        var heatSystem = FindNode<HeatSystem>("HeatSystem");
        if (heatSystem == null)
        {
            result.Success = false;
            result.Message = "HeatSystem not found in scene tree. Is the game running?";
            return result;
        }

        var gsm = GameStateManager.Instance;
        result.HeatBefore = heatSystem.Heat;
        result.RemainingCash = gsm?.Cash ?? 0;

        if (amount < 50)
        {
            result.Success = false;
            result.Message = $"Amount ${amount:F0} is too low. Minimum bribe is $50.";
            return result;
        }

        if (gsm != null && gsm.Cash < amount)
        {
            result.Success = false;
            result.Message = $"Insufficient funds. Have ${gsm.Cash:F2}, need ${amount:F2}.";
            return result;
        }

        // Execute the bribe through HeatSystem
        float heatReduction = heatSystem.BribePrecinctCaptain(amount);

        result.Success = true;
        result.HeatAfter = heatSystem.Heat;
        result.HeatReduction = heatReduction;
        result.RemainingCash = gsm?.Cash ?? 0;
        result.Message = $"Bribe of ${amount:F0} paid to {result.OfficerId}. " +
                         $"Heat reduced by {heatReduction:F1}.";

        GD.Print($"[GameMcpTools] execute_bribe_negotiation: {result}");
        return result;
    }

    // ── Tool: query_staff_psychology ───────────────────────────────────

    /// <summary>
    /// Query the psychological state and RPG stats of a staff member.
    /// Returns full profile including stress, satisfaction, trauma,
    /// burnout risk, and dominant trauma source.
    /// </summary>
    /// <param name="staffId">Name or role identifier of the staff member (e.g. "Alice", "Attendant").</param>
    /// <returns>StaffPsychologyResult with full psychological profile.</returns>
    [AiTool(
        "query_staff_psychology",
        "Get the full psychological profile and RPG stats of a staff member by name or role.")]
    public static StaffPsychologyResult QueryStaffPsychology(string staffId)
    {
        var result = new StaffPsychologyResult();

        if (string.IsNullOrWhiteSpace(staffId))
        {
            result.Found = false;
            result.StaffName = "(empty query)";
            return result;
        }

        // Find all registered staff (via PsychologicalBreakSystem or scene)
        var allStaff = FindAllStaff();

        // Match by name (case-insensitive contains) or role
        StaffMember matched = allStaff.FirstOrDefault(s =>
            s.StaffName.Contains(staffId, StringComparison.OrdinalIgnoreCase) ||
            s.Role.Contains(staffId, StringComparison.OrdinalIgnoreCase));

        if (matched == null)
        {
            result.Found = false;
            result.StaffName = staffId;
            return result;
        }

        // Populate full profile
        result.Found = true;
        result.StaffName = matched.StaffName;
        result.Role = matched.Role;
        result.Charisma = matched.Charisma;
        result.Negotiation = matched.Negotiation;
        result.Discretion = matched.Discretion;
        result.Stress = matched.Stress;
        result.Satisfaction = matched.Satisfaction;
        result.Trauma = matched.Trauma;
        result.MaxSatisfaction = matched.MaxSatisfaction;
        result.EffectivenessRating = matched.EffectivenessRating;
        result.IsBurningOut = matched.IsBurningOut;
        result.IsQuitRisk = matched.IsQuitRisk;

        // Get trauma source from PsychologicalBreakSystem
        var psychBreak = FindNode<PsychologicalBreakSystem>("PsychologicalBreakSystem");
        if (psychBreak != null)
        {
            var source = psychBreak.GetTraumaSource(matched);
            result.DominantTraumaSource = source.ToString();
        }
        else
        {
            result.DominantTraumaSource = "Unknown (PsychBreak system not found)";
        }

        // Generate break risk assessment
        result.BreakRiskAssessment = GenerateBreakRiskAssessment(matched);

        GD.Print($"[GameMcpTools] query_staff_psychology: {result}");
        return result;
    }

    // ── Tool: apply_policy_directive ───────────────────────────────────

    /// <summary>
    /// Enact a policy directive from the Panderer's Code policy tree.
    /// Requires the policy to be available (prerequisites met, cooldown expired).
    /// Applying a tier-0 policy locks the chosen branch permanently.
    /// </summary>
    /// <param name="directiveId">
    /// Policy key identifier. Valid values:
    ///   WF0 = Workforce Protection Act,
    ///   WF1 = Medical Care Provision,
    ///   WF2 = Profit Sharing Program,
    ///   WF3 = Security Detail,
    ///   SE0 = Operational Freedom Act,
    ///   SE1 = Extended Shift Authorization,
    ///   SE2 = Debt Bondage Protocol,
    ///   SE3 = Information Leverage Initiative
    /// </param>
    /// <returns>PolicyResult with enactment details.</returns>
    [AiTool(
        "apply_policy_directive",
        "Enact a policy from the Panderer's Code. Tier-0 policies lock a branch permanently. " +
        "Keys: WF0–WF3 (Workforce Protection), SE0–SE3 (Systemic Exploitation).")]
    public static PolicyResult ApplyPolicyDirective(string directiveId)
    {
        var result = new PolicyResult
        {
            DirectiveId = directiveId ?? ""
        };

        if (string.IsNullOrWhiteSpace(directiveId))
        {
            result.Success = false;
            result.Message = "No directive ID provided. Use keys like WF0, SE0, etc.";
            return result;
        }

        // Normalize to uppercase
        directiveId = directiveId.Trim().ToUpperInvariant();
        result.DirectiveId = directiveId;

        var policyTree = FindNode<PolicyTreeManager>("PolicyTreeManager");
        if (policyTree == null)
        {
            result.Success = false;
            result.Message = "PolicyTreeManager not found in scene tree.";
            return result;
        }

        // Validate directive format
        var validKeys = new[] { "WF0", "WF1", "WF2", "WF3", "SE0", "SE1", "SE2", "SE3" };
        if (!validKeys.Contains(directiveId))
        {
            result.Success = false;
            result.Message = $"Unknown directive '{directiveId}'. Valid keys: {string.Join(", ", validKeys)}";
            return result;
        }

        // Attempt enactment
        var enactment = policyTree.EnactPolicy(directiveId);

        result.Success = enactment.Success;
        result.Message = enactment.Message;

        if (enactment.Success && enactment.Policy != null)
        {
            result.PolicyName = enactment.Policy.PolicyName;
            result.Tier = enactment.Policy.Tier;
            result.Branch = enactment.Policy.Branch.ToString();
            result.Effects = enactment.Policy.EffectsDescription;
        }

        // Current state
        result.ActiveBranch = policyTree.ActiveBranch.ToString();
        result.TotalEnactedPolicies = policyTree.EnactedPolicies.Count;
        result.ModifierSummary = policyTree.GetModifierSummary();

        GD.Print($"[GameMcpTools] apply_policy_directive: {result}");
        return result;
    }

    // ── Internal Helpers ───────────────────────────────────────────────

    /// <summary>Find a node of type T in the scene tree.</summary>
    private static T FindNode<T>(string name) where T : Node
    {
        // Try to get the scene tree from the engine main loop
        // In editor context, use EditorInterface; at runtime, use the scene tree
        var tree = GetSceneTree();
        if (tree?.Root == null) return null;

        return tree.Root.FindChild(name, recursive: true, owned: false) as T;
    }

    /// <summary>Get all registered StaffMember instances.</summary>
    private static System.Collections.Generic.List<StaffMember> FindAllStaff()
    {
        // Static context, so this reaches the roster through its singleton
        // rather than a scene-tree lookup. The previous implementation read
        // PsychologicalBreakSystem.GetStaffAtRisk(), which filters to Stress >= 80.
        return StaffRoster.Instance?.GetAll().ToList()
               ?? new System.Collections.Generic.List<StaffMember>();
    }

    /// <summary>Generate a human-readable break risk assessment.</summary>
    private static string GenerateBreakRiskAssessment(StaffMember staff)
    {
        if (staff.IsBurningOut && staff.IsQuitRisk)
            return "CRITICAL: Staff is both burning out AND at risk of quitting. Immediate intervention required.";

        if (staff.IsBurningOut)
            return "HIGH: Staff is approaching or at burnout threshold. Reduce workload or provide rest immediately.";

        if (staff.IsQuitRisk)
            return "HIGH: Staff satisfaction is at zero — they may quit at any moment. Consider a bonus or time off.";

        if (staff.Stress >= 70f)
            return "ELEVATED: Stress is approaching critical levels. Monitor closely and consider rest.";

        if (staff.Trauma >= 60f)
            return "ELEVATED: Trauma accumulation is significant. Therapy recommended.";

        if (staff.Satisfaction <= 25f)
            return "LOW SATISFACTION: Staff morale is poor. A bonus or better conditions would help.";

        if (staff.Stress <= 30f && staff.Satisfaction >= 60f)
            return "STABLE: Staff is in good psychological health. No intervention needed.";

        return $"MODERATE: Staff is managing. Stress={staff.Stress:F0}/100 Sat={staff.Satisfaction:F0}/100.";
    }

    /// <summary>
    /// Get the current scene tree, handling both editor and runtime contexts.
    /// </summary>
    private static SceneTree GetSceneTree()
    {
        // At runtime, Engine.GetMainLoop() returns the SceneTree
        if (Engine.GetMainLoop() is SceneTree tree)
            return tree;

        // Fallback: search for any available viewport
        return null;
    }
}
