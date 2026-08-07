using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Handles static UI text localization and dynamic LLM text formatting.
/// Loads CSV/JSON translation dictionaries. Sanitizes raw LLM output,
/// injects game variables (e.g., {CASH} → "$1,250"), and wraps text
/// within UI dialog panel width constraints.
/// </summary>
public partial class LocalizationManager : Node
{
    private readonly Dictionary<string, Dictionary<string, string>> _locales = new();
    private string _currentLocale = "en";
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string CurrentLocale => _currentLocale;

    public override void _Ready()
    {
        LoadLocale("en", DefaultEnglishStrings());
        GD.Print($"[Localization] Initialized. Locale: {_currentLocale}. {_locales[_currentLocale].Count} keys.");
    }

    /// <summary>Load a locale dictionary from JSON string.</summary>
    public void LoadLocale(string code, Dictionary<string, string> entries)
    {
        _locales[code] = entries;
        _currentLocale = code;
    }

    /// <summary>Get a localized UI string by key.</summary>
    public string Get(string key, string fallback = "")
    {
        if (_locales.TryGetValue(_currentLocale, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    /// <summary>Get a localized string with variable injection.</summary>
    public string GetFormatted(string key, Dictionary<string, string> variables)
    {
        string text = Get(key);
        return InjectVariables(text, variables);
    }

    // ── Variable Injection ─────────────────────────────────────────────

    /// <summary>
    /// Replace {VARIABLE} placeholders with formatted values.
    /// Special formatters: {CASH} → "$1,250", {HEAT} → "65%",
    /// {REPUTATION} → "★★★☆☆", {DAY} → "Day 42".
    /// </summary>
    public static string InjectVariables(string text, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(text) || variables == null) return text;

        return Regex.Replace(text, @"\{(\w+)\}", match =>
        {
            string key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var val) ? val : match.Value;
        });
    }

    /// <summary>Build a standard variable dictionary from current game state.</summary>
    public static Dictionary<string, string> BuildGameVariables()
    {
        var gsm = GameStateManager.Instance;
        return new Dictionary<string, string>
        {
            ["CASH"] = gsm != null ? FormatCash(gsm.Cash) : "$0",
            ["HEAT"] = gsm != null ? $"{gsm.Heat:F0}%" : "0%",
            ["REPUTATION"] = gsm != null ? FormatStars(gsm.Reputation) : "★☆☆☆☆",
            ["SENTIMENT"] = gsm != null ? $"{gsm.PublicSentiment:F0}%" : "0%",
            ["DAY"] = gsm != null ? $"Day {gsm.DayCount}" : "Day 1",
            ["YEAR"] = DateTime.Now.Year.ToString(),
        };
    }

    // ── LLM Text Sanitization ──────────────────────────────────────────

    /// <summary>
    /// Sanitize raw LLM output for display in UI panels.
    /// Fixes encoding issues, strips hallucinated markup, wraps text.
    /// </summary>
    public static string SanitizeLlmText(string raw, int maxLineWidth = 60)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string text = raw;

        // Strip common LLM hallucination patterns
        text = Regex.Replace(text, @"\[INST\].*?\[/INST\]", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<\|.*?\|>", "");
        text = Regex.Replace(text, @"\[ASSTAG\].*?\[/ASSTAG\]", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"```\w*\n", "");
        text = Regex.Replace(text, @"```", "");

        // Fix encoding
        text = text.Replace("â\u0080\u0099", "'")
                   .Replace("â\u0080\u009C", "\"")
                   .Replace("â\u0080\u009D", "\"")
                   .Replace("â\u0080\u0094", "—")
                   .Replace("â\u0080\u0093", "–");

        // Collapse whitespace
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Inject any {VARIABLE} references from game state
        text = InjectVariables(text, BuildGameVariables());

        // Word wrap
        text = WrapText(text, maxLineWidth);

        return text;
    }

    /// <summary>Wrap text to fit within a character width.</summary>
    public static string WrapText(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text;

        var result = new System.Text.StringBuilder();
        int lineStart = 0;

        while (lineStart < text.Length)
        {
            int lineEnd = Math.Min(lineStart + maxWidth, text.Length);
            if (lineEnd < text.Length)
            {
                // Try to break at last space
                int lastSpace = text.LastIndexOf(' ', lineEnd, lineEnd - lineStart);
                if (lastSpace > lineStart) lineEnd = lastSpace;
            }
            result.AppendLine(text[lineStart..lineEnd].Trim());
            lineStart = lineEnd;
            while (lineStart < text.Length && text[lineStart] == ' ') lineStart++;
        }

        return result.ToString().TrimEnd();
    }

    // ── Default English Strings ────────────────────────────────────────

    private static Dictionary<string, string> DefaultEnglishStrings() => new()
    {
        ["menu_new_game"]   = "New Game",
        ["menu_continue"]   = "Continue",
        ["menu_settings"]   = "Settings",
        ["menu_quit"]       = "Quit to Desktop",
        ["hud_cash"]        = "Cash",
        ["hud_prestige"]    = "Prestige",
        ["hud_heat"]        = "Heat",
        ["hud_sentiment"]   = "Public Sentiment",
        ["btn_assign"]      = "Assign Shift",
        ["btn_train"]       = "Train Skill",
        ["btn_bonus"]       = "Pay Bonus",
        ["btn_terminate"]   = "Terminate Contract",
        ["policy_branch_wf"] = "Workforce Protection",
        ["policy_branch_se"] = "Systemic Exploitation",
        ["crisis_title_default"] = "Crisis Event",
        ["negotiation_offer"] = "Counter-offer: ${AMOUNT}",
        ["save_success"]    = "Game saved successfully.",
        ["load_success"]    = "Game loaded successfully.",
        ["error_generic"]   = "An error occurred.",
        ["confirm_purchase"] = "Purchase {ITEM} for ${PRICE}?",
        ["day_prefix"]      = "Day {DAY}",
        ["strike_active"]   = "⚠ Workers are on strike!",
        ["raid_active"]     = "🔥 Police raid in progress!",
    };

    // ── Formatting Utilities ───────────────────────────────────────────

    private static string FormatCash(double value)
    {
        if (value >= 1_000_000) return $"${value / 1_000_000:F2}M";
        if (value >= 10_000) return $"${value / 1_000:F1}K";
        if (value < 0) return $"-${Math.Abs(value):F0}";
        return $"${value:F0}";
    }

    private static string FormatStars(float reputation)
    {
        int stars = Mathf.Clamp(Mathf.FloorToInt(reputation / 20f), 1, 5);
        return new string('★', stars) + new string('☆', 5 - stars);
    }

    public override string ToString() =>
        $"[Localization] Locale={_currentLocale} Keys={_locales.GetValueOrDefault(_currentLocale)?.Count ?? 0}";
}
