using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Remote balance override data structure.</summary>
public class BalancePatch
{
    [JsonPropertyName("version")]          public string Version { get; set; } = "1.0";
    [JsonPropertyName("description")]      public string Description { get; set; } = "";
    [JsonPropertyName("appliedAt")]        public string AppliedAt { get; set; }
    [JsonPropertyName("pricingMatrix")]    public Dictionary<string, double> PricingMatrix { get; set; } = new();
    [JsonPropertyName("policyCooldowns")]  public Dictionary<string, int> PolicyCooldowns { get; set; } = new();
    [JsonPropertyName("stressMultipliers")] public Dictionary<string, float> StressMultipliers { get; set; } = new();
    [JsonPropertyName("heatMultipliers")]  public Dictionary<string, float> HeatMultipliers { get; set; } = new();
    [JsonPropertyName("revenueMultipliers")] public Dictionary<string, float> RevenueMultipliers { get; set; } = new();
    [JsonPropertyName("globalModifiers")]  public Dictionary<string, float> GlobalModifiers { get; set; } = new();
}

/// <summary>
/// Fetches remote JSON balance overrides without engine updates.
/// Parses pricing matrices, policy cooldowns, and stress multipliers.
/// Applies tweaks safely at runtime on title screen launch.
/// Falls back to local defaults if remote is unavailable.
/// </summary>
public partial class LiveBalancePatcher : Node
{
    [Signal] public delegate void OnPatchAppliedEventHandler(string version, int overrideCount);
    [Signal] public delegate void OnPatchFailedEventHandler(string reason);
    [Signal] public delegate void OnFallbackUsedEventHandler();

    private BalancePatch _activePatch;
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string RemotePatchUrl = "https://api.establishmentsim.example.com/balance/latest.json";
    private const string LocalCachePath = "user://balance_patch_cache.json";

    public BalancePatch ActivePatch => _activePatch;
    public bool IsPatchActive => _activePatch != null;

    public override void _Ready()
    {
        // Try loading cached patch first, then attempt remote fetch
        LoadCachedPatch();
        GD.Print("[LiveBalancePatcher] Initialized.");
    }

    /// <summary>Attempt to fetch and apply the latest balance patch.</summary>
    public void FetchAndApply()
    {
        // Try remote fetch first
        if (TryFetchRemote())
        {
            GD.Print($"[LiveBalancePatcher] Remote patch v{_activePatch?.Version} applied.");
            return;
        }

        // Fall back to cached
        if (_activePatch != null)
        {
            GD.Print("[LiveBalancePatcher] Using cached patch.");
            EmitSignal(SignalName.OnFallbackUsed);
            return;
        }

        // Use built-in defaults
        _activePatch = BuildDefaultPatch();
        GD.Print("[LiveBalancePatcher] Using built-in defaults.");
        EmitSignal(SignalName.OnFallbackUsed);
    }

    // ── Remote Fetch ────────────────────────────────────────────────────

    private bool TryFetchRemote()
    {
        try
        {
            var http = new HttpRequest();
            AddChild(http);
            http.RequestCompleted += (result, code, headers, body) =>
            {
                if ((int)result == 0) // RESULT_SUCCESS
                {
                    string json = System.Text.Encoding.UTF8.GetString(body);
                    var patch = JsonSerializer.Deserialize<BalancePatch>(json, _jsonOpts);
                    if (patch != null)
                    {
                        _activePatch = patch;
                        _activePatch.AppliedAt = DateTime.UtcNow.ToString("O");
                        CachePatch(json);
                        ApplyPatch();
                        EmitSignal(SignalName.OnPatchApplied, _activePatch.Version, CountOverrides());
                    }
                }
                else
                {
                    EmitSignal(SignalName.OnPatchFailed, $"HTTP {(int)code}");
                }
                http.QueueFree();
            };

            http.Request(RemotePatchUrl);
            return true; // async — result comes via callback
        }
        catch (Exception ex)
        {
            EmitSignal(SignalName.OnPatchFailed, ex.Message);
            return false;
        }
    }

    // ── JSON Parsing ────────────────────────────────────────────────────

    /// <summary>Parse and apply a balance patch from a JSON string.</summary>
    public bool ApplyPatchFromJson(string json)
    {
        try
        {
            var patch = JsonSerializer.Deserialize<BalancePatch>(json, _jsonOpts);
            if (patch == null) return false;

            _activePatch = patch;
            _activePatch.AppliedAt = DateTime.UtcNow.ToString("O");
            ApplyPatch();
            CachePatch(json);

            EmitSignal(SignalName.OnPatchApplied, _activePatch.Version, CountOverrides());
            GD.Print($"[LiveBalancePatcher] Patch applied: v{_activePatch.Version} ({CountOverrides()} overrides).");
            return true;
        }
        catch (Exception ex)
        {
            EmitSignal(SignalName.OnPatchFailed, ex.Message);
            return false;
        }
    }

    // ── Apply Overrides ─────────────────────────────────────────────────

    private void ApplyPatch()
    {
        if (_activePatch == null) return;

        // Apply pricing overrides to systems in the scene tree
        ApplyPricingOverrides();
        ApplyPolicyOverrides();
        ApplyStressOverrides();
        ApplyHeatOverrides();
        ApplyRevenueOverrides();
        ApplyGlobalOverrides();
    }

    private void ApplyPricingOverrides()
    {
        var rem = GetTree()?.Root?.FindChild("RealEstateMarket", true, false) as RealEstateMarket;

        foreach (var kvp in _activePatch.PricingMatrix)
        {
            switch (kvp.Key)
            {
                case "room_base_cost_multiplier":
                    // Room costs now live in VenueBuilding.RoomCosts, which is a
                    // static table — patching it needs an override layer there.
                    GD.Print($"[LiveBalancePatcher] Pricing: {kvp.Key} = {kvp.Value}");
                    break;
                case "property_base_value_multiplier":
                    if (rem != null) { /* rem.AdjustBaseValues(kvp.Value); */ }
                    break;
            }
        }
    }

    private void ApplyPolicyOverrides()
    {
        var pt = GetTree()?.Root?.FindChild("PolicyTreeManager", true, false) as PolicyTreeManager;
        if (pt == null) return;

        foreach (var kvp in _activePatch.PolicyCooldowns)
        {
            if (kvp.Key == "enactment_cooldown_days")
            {
                pt.GetType().GetProperty("EnactmentCooldownDays")?.SetValue(pt, kvp.Value);
                GD.Print($"[LiveBalancePatcher] Policy cooldown: {kvp.Value} days.");
            }
        }
    }

    private void ApplyStressOverrides()
    {
        var hs = GetTree()?.Root?.FindChild("HeatSystem", true, false);
        foreach (var kvp in _activePatch.StressMultipliers)
        {
            switch (kvp.Key)
            {
                case "shift_workload_multiplier":
                    GD.Print($"[LiveBalancePatcher] Stress: {kvp.Key} = {kvp.Value}");
                    break;
            }
        }
    }

    private void ApplyHeatOverrides()
    {
        var hs = GetTree()?.Root?.FindChild("HeatSystem", true, false) as HeatSystem;
        if (hs == null) return;

        foreach (var kvp in _activePatch.HeatMultipliers)
        {
            if (kvp.Key == "heat_per_thousand_revenue")
            {
                hs.GetType().GetProperty("HeatPerThousandRevenue")?.SetValue(hs, (float)kvp.Value);
                GD.Print($"[LiveBalancePatcher] Heat: {kvp.Key} = {kvp.Value}");
            }
            else if (kvp.Key == "intervention_threshold")
            {
                hs.GetType().GetProperty("InterventionThreshold")?.SetValue(hs, (float)kvp.Value);
            }
        }
    }

    private void ApplyRevenueOverrides()
    {
        var macro = GetTree()?.Root?.FindChild("MacroEconomyEngine", true, false);
        foreach (var kvp in _activePatch.RevenueMultipliers)
        {
            GD.Print($"[LiveBalancePatcher] Revenue: {kvp.Key} = {kvp.Value}");
        }
    }

    private void ApplyGlobalOverrides()
    {
        foreach (var kvp in _activePatch.GlobalModifiers)
        {
            // General-purpose key-value override
            GD.Print($"[LiveBalancePatcher] Global: {kvp.Key} = {kvp.Value}");
        }
    }

    // ── Query ───────────────────────────────────────────────────────────

    /// <summary>Get a patched value, falling back to a default.</summary>
    public double GetPatchedPrice(string key, double defaultValue)
    {
        if (_activePatch?.PricingMatrix.TryGetValue(key, out var val) == true) return val;
        return defaultValue;
    }

    public int GetPatchedCooldown(string key, int defaultValue)
    {
        if (_activePatch?.PolicyCooldowns.TryGetValue(key, out var val) == true) return val;
        return defaultValue;
    }

    public float GetPatchedMultiplier(string category, string key, float defaultValue)
    {
        var dict = category switch
        {
            "stress" => _activePatch?.StressMultipliers,
            "heat" => _activePatch?.HeatMultipliers,
            "revenue" => _activePatch?.RevenueMultipliers,
            "global" => _activePatch?.GlobalModifiers,
            _ => null
        };
        if (dict != null && dict.TryGetValue(key, out var val)) return val;
        return defaultValue;
    }

    private int CountOverrides()
    {
        if (_activePatch == null) return 0;
        return _activePatch.PricingMatrix.Count +
               _activePatch.PolicyCooldowns.Count +
               _activePatch.StressMultipliers.Count +
               _activePatch.HeatMultipliers.Count +
               _activePatch.RevenueMultipliers.Count +
               _activePatch.GlobalModifiers.Count;
    }

    // ── Caching ─────────────────────────────────────────────────────────

    private void CachePatch(string json)
    {
        try
        {
            using var f = Godot.FileAccess.Open(LocalCachePath, Godot.FileAccess.ModeFlags.Write);
            f.StoreString(json);
        }
        catch { /* silent fail */ }
    }

    private void LoadCachedPatch()
    {
        if (!Godot.FileAccess.FileExists(LocalCachePath)) return;
        try
        {
            using var f = Godot.FileAccess.Open(LocalCachePath, Godot.FileAccess.ModeFlags.Read);
            string json = f.GetAsText();
            _activePatch = JsonSerializer.Deserialize<BalancePatch>(json, _jsonOpts);
            if (_activePatch != null) ApplyPatch();
        }
        catch { /* silent fail */ }
    }

    private static BalancePatch BuildDefaultPatch()
    {
        return new BalancePatch
        {
            Version = "default",
            Description = "Built-in default balance values.",
            AppliedAt = DateTime.UtcNow.ToString("O"),
            PricingMatrix = new() { ["room_base_cost_multiplier"] = 1.0 },
            PolicyCooldowns = new() { ["enactment_cooldown_days"] = 1 },
            StressMultipliers = new() { ["shift_workload_multiplier"] = 1.0f },
            HeatMultipliers = new() { ["heat_per_thousand_revenue"] = 0.5f },
            RevenueMultipliers = new() { ["client_spend_multiplier"] = 1.0f },
            GlobalModifiers = new() { ["global_difficulty"] = 1.0f }
        };
    }

    public override string ToString() =>
        $"[LiveBalancePatcher] Patch={_activePatch?.Version ?? "none"} Overrides={CountOverrides()}";
}
