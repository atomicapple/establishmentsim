using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>Result of JSON validation.</summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string Error { get; set; }
    public JsonElement Parsed { get; set; }
}

/// <summary>Expected field definition for schema validation.</summary>
public struct SchemaField
{
    public string Name;
    public Type ExpectedType;
    public bool Required;
    public string[] AllowedValues;
}

/// <summary>
/// Sanitizes JSON strings returned from LLM agent responses.
/// Enforces schema validation against expected field types.
/// Provides fallback logic: if LLM returns malformed JSON or
/// times out after 3 seconds, generates a deterministic default event.
/// </summary>
public partial class McpPayloadValidator : Node
{
    [Signal] public delegate void OnValidationFailedEventHandler(string reason, string fallbackUsed);
    [Signal] public delegate void OnTimeoutEventHandler();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private DateTime _requestStart;
    private bool _awaitingResponse;
    private const double TimeoutSeconds = 3.0;

    public override void _Ready()
    {
        GD.Print("[McpValidator] Initialized. Timeout: 3s.");
    }

    public override void _Process(double delta)
    {
        if (!_awaitingResponse) return;

        if ((DateTime.UtcNow - _requestStart).TotalSeconds > TimeoutSeconds)
        {
            _awaitingResponse = false;
            EmitSignal(SignalName.OnTimeout);
            GD.PrintErr("[McpValidator] MCP response timeout after 3s — using fallback.");
        }
    }

    /// <summary>Mark the start of an MCP request for timeout tracking.</summary>
    public void BeginRequest()
    {
        _requestStart = DateTime.UtcNow;
        _awaitingResponse = true;
    }

    /// <summary>Mark MCP response received (cancels timeout).</summary>
    public void EndRequest()
    {
        _awaitingResponse = false;
    }

    // ── Schema Validation ──────────────────────────────────────────────

    /// <summary>
    /// Validate a JSON string against expected field types.
    /// Returns ValidationResult with parsed element or error.
    /// </summary>
    public ValidationResult ValidateJson(string json, SchemaField[] schema)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(json))
        {
            result.Error = "Empty or null JSON string.";
            return result;
        }

        // Try to extract JSON from markdown/code blocks
        json = ExtractJsonFromText(json);

        try
        {
            var doc = JsonDocument.Parse(json);
            result.Parsed = doc.RootElement;
        }
        catch (Exception ex)
        {
            result.Error = $"JSON parse error: {ex.Message}";
            return result;
        }

        // Validate against schema
        foreach (var field in schema)
        {
            if (!result.Parsed.TryGetProperty(field.Name, out var prop))
            {
                if (field.Required)
                {
                    result.Error = $"Missing required field: '{field.Name}'.";
                    return result;
                }
                continue;
            }

            if (!ValidateFieldType(prop, field.ExpectedType))
            {
                result.Error = $"Field '{field.Name}' has wrong type. Expected {field.ExpectedType.Name}.";
                return result;
            }

            if (field.AllowedValues != null && field.AllowedValues.Length > 0)
            {
                string value = prop.ToString();
                if (Array.IndexOf(field.AllowedValues, value) < 0)
                {
                    result.Error = $"Field '{field.Name}' value '{value}' not in allowed set.";
                    return result;
                }
            }
        }

        result.IsValid = true;
        return result;
    }

    /// <summary>Quick-check if a JSON string is parseable.</summary>
    public bool IsValidJson(string json)
    {
        try { JsonDocument.Parse(ExtractJsonFromText(json)); return true; }
        catch { return false; }
    }

    /// <summary>Extract JSON from markdown code blocks or surrounding text.</summary>
    public static string ExtractJsonFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Strip ```json ... ``` code blocks
        int start = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            start += 7;
            int end = text.IndexOf("```", start);
            if (end > start) return text[start..end].Trim();
        }

        // Strip ``` ... ``` generic code blocks
        start = text.IndexOf("```");
        if (start >= 0)
        {
            start += 3;
            int end = text.IndexOf("```", start);
            if (end > start) return text[start..end].Trim();
        }

        // Try to find JSON object boundaries
        start = text.IndexOf('{');
        if (start >= 0)
        {
            int end = text.LastIndexOf('}');
            if (end > start) return text[start..(end + 1)];
        }

        start = text.IndexOf('[');
        if (start >= 0)
        {
            int end = text.LastIndexOf(']');
            if (end > start) return text[start..(end + 1)];
        }

        return text.Trim();
    }

    private static bool ValidateFieldType(JsonElement element, Type expected)
    {
        return expected switch
        {
            _ when expected == typeof(string) => element.ValueKind == JsonValueKind.String,
            _ when expected == typeof(double) || expected == typeof(float) =>
                element.ValueKind == JsonValueKind.Number,
            _ when expected == typeof(int) || expected == typeof(long) =>
                element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            _ when expected == typeof(bool) =>
                element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ when expected == typeof(string[]) || expected == typeof(List<string>) =>
                element.ValueKind == JsonValueKind.Array,
            _ when expected == typeof(object) => true,
            _ => false
        };
    }

    // ── Fallback Generators ────────────────────────────────────────────

    /// <summary>Event dialog fallback schema.</summary>
    public static readonly SchemaField[] EventDialogSchema =
    {
        new() { Name="eventTitle", ExpectedType=typeof(string), Required=true },
        new() { Name="narrative", ExpectedType=typeof(string), Required=true },
        new() { Name="options", ExpectedType=typeof(string[]), Required=true },
    };

    /// <summary>Haggling counter-offer fallback schema.</summary>
    public static readonly SchemaField[] HagglingSchema =
    {
        new() { Name="dialogue", ExpectedType=typeof(string), Required=true },
        new() { Name="counterAmount", ExpectedType=typeof(double), Required=true },
        new() { Name="feeAdjustment", ExpectedType=typeof(float), Required=true },
        new() { Name="patienceAdjustment", ExpectedType=typeof(float), Required=true },
    };

    /// <summary>Crisis scenario fallback schema.</summary>
    public static readonly SchemaField[] CrisisSchema =
    {
        new() { Name="title", ExpectedType=typeof(string), Required=true },
        new() { Name="narrative", ExpectedType=typeof(string), Required=true },
        new() { Name="choices", ExpectedType=typeof(string[]), Required=true },
    };

    /// <summary>
    /// Generate a deterministic default event choice when LLM
    /// returns malformed JSON or times out.
    /// </summary>
    public string GenerateFallbackEventJson(string triggerType)
    {
        var fallback = new
        {
            eventTitle = $"Emergency: {triggerType}",
            narrative = $"A critical situation has developed ({triggerType}). " +
                        "The LLM narrative engine is currently unavailable. " +
                        "You must make an immediate decision based on standard protocol.",
            triggerFactor = triggerType,
            options = new[]
            {
                new {
                    label = "A: Standard Protocol",
                    description = "Follow established procedures. Safe but unremarkable.",
                    effects = new { cash = -200.0, heat = -10.0, reputation = 0.0,
                                    publicSentiment = 5.0, staffEffects = Array.Empty<object>() }
                },
                new {
                    label = "B: Aggressive Action",
                    description = "Take bold action to resolve the crisis quickly.",
                    effects = new { cash = -100.0, heat = 5.0, reputation = 5.0,
                                    publicSentiment = -5.0, staffEffects = Array.Empty<object>() }
                },
                new {
                    label = "C: Wait and Assess",
                    description = "Delay action until more information is available.",
                    effects = new { cash = -50.0, heat = 0.0, reputation = -3.0,
                                    publicSentiment = -3.0, staffEffects = Array.Empty<object>() }
                }
            }
        };

        return JsonSerializer.Serialize(fallback, _jsonOpts);
    }

    /// <summary>Generate fallback haggling counter-offer.</summary>
    public string GenerateFallbackHagglingJson(string clientName, double fairPrice)
    {
        var fallback = new
        {
            dialogue = $"The negotiation with {clientName} continues. They seem receptive.",
            counterAmount = fairPrice * 0.9,
            mood = "neutral",
            feeAdjustment = -0.05,
            patienceAdjustment = -2.0,
            reasoning = "Deterministic fallback — MCP LLM unavailable."
        };

        return JsonSerializer.Serialize(fallback, _jsonOpts);
    }

    /// <summary>
    /// Validate and return parsed JSON, or log + emit fallback.
    /// Returns true if validation passed.
    /// </summary>
    public bool TryValidateOrFallback(string json, SchemaField[] schema,
        out string validatedJson, string fallbackJson = null)
    {
        var result = ValidateJson(json, schema);

        if (result.IsValid)
        {
            validatedJson = json;
            return true;
        }

        string reason = result.Error ?? "Unknown validation error";
        EmitSignal(SignalName.OnValidationFailed, reason, fallbackJson != null ? "yes" : "no");
        GD.PrintErr($"[McpValidator] Validation failed: {reason}");

        validatedJson = fallbackJson ?? GenerateFallbackEventJson("unknown");
        return false;
    }

    /// <summary>Sanitize a JSON string: trim, extract from markdown, validate basic structure.</summary>
    public string Sanitize(string raw)
    {
        return ExtractJsonFromText(raw);
    }

    public override string ToString() =>
        $"[McpValidator] Awaiting={_awaitingResponse} Timeout={TimeoutSeconds}s";
}
