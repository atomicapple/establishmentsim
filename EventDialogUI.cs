using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── AI Event Payload DTOs ──────────────────────────────────────────────

/// <summary>A single staff effect within a choice option.</summary>
public class AiStaffEffect
{
    [JsonPropertyName("staffName")]
    public string StaffName { get; set; }

    [JsonPropertyName("stressDelta")]
    public float StressDelta { get; set; }

    [JsonPropertyName("satisfactionDelta")]
    public float SatisfactionDelta { get; set; }

    [JsonPropertyName("traumaDelta")]
    public float TraumaDelta { get; set; }
}

/// <summary>Numerical effects of a player choice.</summary>
public class AiChoiceEffects
{
    [JsonPropertyName("cash")]
    public double Cash { get; set; }

    [JsonPropertyName("heat")]
    public float Heat { get; set; }

    [JsonPropertyName("reputation")]
    public float Reputation { get; set; }

    [JsonPropertyName("publicSentiment")]
    public float PublicSentiment { get; set; }

    [JsonPropertyName("staffEffects")]
    public List<AiStaffEffect> StaffEffects { get; set; } = new();
}

/// <summary>A single choice option in an AI-generated event.</summary>
public class AiEventOption
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "Option";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("effects")]
    public AiChoiceEffects Effects { get; set; } = new();
}

/// <summary>Full AI-generated event payload.</summary>
public class AiEventPayload
{
    [JsonPropertyName("eventTitle")]
    public string EventTitle { get; set; } = "Event";

    [JsonPropertyName("narrative")]
    public string Narrative { get; set; } = "";

    [JsonPropertyName("triggerFactor")]
    public string TriggerFactor { get; set; } = "";

    [JsonPropertyName("options")]
    public List<AiEventOption> Options { get; set; } = new();
}

/// <summary>Data for a single dynamically-created choice button.</summary>
public class ChoiceButtonData
{
    public AiEventOption Option { get; set; }
    public Button Button { get; set; }
}

// ── EventDialogUI Control ──────────────────────────────────────────────

/// <summary>
/// Dynamic event dialog that parses AI-generated JSON payloads and
/// renders them as interactive UI with title, narrative text, and
/// choice buttons. Button clicks apply the associated effects to
/// GameStateManager, HeatSystem, FinancialLedger, and StaffMembers.
///
/// Expected JSON schema:
/// {
///   "eventTitle": "...",
///   "narrative": "...",
///   "triggerFactor": "...",
///   "options": [
///     {
///       "label": "A: ...",
///       "description": "...",
///       "effects": {
///         "cash": 0.0, "heat": 0.0, "reputation": 0.0,
///         "publicSentiment": 0.0, "staffEffects": []
///       }
///     }
///   ]
/// }
/// </summary>
public partial class EventDialogUI : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when the dialog is shown with an event.</summary>
    [Signal]
    public delegate void OnEventShownEventHandler(string eventTitle, int optionCount);

    /// <summary>Fired when the player selects a choice.</summary>
    [Signal]
    public delegate void OnChoiceSelectedEventHandler(
        string eventTitle, string choiceLabel, double cashEffect, float heatEffect);

    /// <summary>Fired when the dialog is dismissed.</summary>
    [Signal]
    public delegate void OnDialogDismissedEventHandler();

    // ── Node References ────────────────────────────────────────────────
    private Label _titleLabel;
    private RichTextLabel _narrativeLabel;
    private Label _triggerLabel;
    private VBoxContainer _buttonContainer;
    private PanelContainer _panel;
    private Button _closeButton;

    // ── State ──────────────────────────────────────────────────────────
    private AiEventPayload _currentEvent;
    private readonly List<ChoiceButtonData> _choiceButtons = new();
    private bool _isVisible;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Whether an event is currently being displayed.</summary>
    public bool IsShowing => _isVisible && Visible;

    /// <summary>Current event payload (null if no event active).</summary>
    public AiEventPayload CurrentEvent => _currentEvent;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUI();
        HideDialog();
        GD.Print("[EventDialogUI] Initialized and ready for events.");
    }

    /// <summary>Build the UI hierarchy programmatically.</summary>
    private void BuildUI()
    {
        // Root panel
        _panel = new PanelContainer();
        _panel.SetAnchorsPreset(LayoutPreset.Center);
        _panel.SetSize(new Vector2(600, 400));
        _panel.AddThemeStyleboxOverride("panel",
            new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.15f, 0.95f) });

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 20);
        margin.AddThemeConstantOverride("margin_right", 20);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        _panel.AddChild(margin);

        var rootVbox = new VBoxContainer();
        rootVbox.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddChild(rootVbox);

        // Title
        _titleLabel = new Label();
        _titleLabel.AddThemeFontSizeOverride("font_size", 22);
        _titleLabel.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        rootVbox.AddChild(_titleLabel);

        // Trigger info
        _triggerLabel = new Label();
        _triggerLabel.AddThemeFontSizeOverride("font_size", 12);
        _triggerLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.7f));
        _triggerLabel.HorizontalAlignment = HorizontalAlignment.Center;
        rootVbox.AddChild(_triggerLabel);

        rootVbox.AddChild(new HSeparator());

        // Narrative
        var scrollContainer = new ScrollContainer();
        scrollContainer.CustomMinimumSize = new Vector2(0, 140);
        scrollContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        _narrativeLabel = new RichTextLabel();
        _narrativeLabel.BbcodeEnabled = true;
        _narrativeLabel.FitContent = true;
        _narrativeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scrollContainer.AddChild(_narrativeLabel);
        rootVbox.AddChild(scrollContainer);

        rootVbox.AddChild(new HSeparator());

        // Choice label
        var choiceHeader = new Label();
        choiceHeader.Text = "Choose your response:";
        choiceHeader.AddThemeFontSizeOverride("font_size", 15);
        choiceHeader.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        rootVbox.AddChild(choiceHeader);

        // Button container
        _buttonContainer = new VBoxContainer();
        _buttonContainer.AddThemeConstantOverride("separation", 8);
        rootVbox.AddChild(_buttonContainer);

        // Close button (hidden during events)
        _closeButton = new Button();
        _closeButton.Text = "×";
        _closeButton.Flat = true;
        _closeButton.AddThemeFontSizeOverride("font_size", 18);
        _closeButton.Pressed += () => HideDialog();
        rootVbox.AddChild(_closeButton);

        AddChild(_panel);
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Show an event dialog from a JSON string payload.
    /// </summary>
    public bool ShowEventFromJson(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<AiEventPayload>(json, _jsonOptions);
            if (payload == null || payload.Options.Count == 0)
            {
                GD.PrintErr("[EventDialogUI] Invalid or empty event JSON.");
                return false;
            }

            return ShowEvent(payload);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[EventDialogUI] Failed to parse event JSON: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Show an event dialog from a pre-built AiEventPayload.
    /// </summary>
    public bool ShowEvent(AiEventPayload payload)
    {
        if (payload == null || payload.Options.Count == 0)
            return false;

        _currentEvent = payload;
        RenderEvent(payload);
        ShowDialog();

        EmitSignal(SignalName.OnEventShown, payload.EventTitle, payload.Options.Count);
        GD.Print($"[EventDialogUI] Showing event: \"{payload.EventTitle}\" " +
                 $"({payload.Options.Count} options)");
        return true;
    }

    /// <summary>
    /// Hide the event dialog.
    /// </summary>
    public void HideDialog()
    {
        _isVisible = false;
        _panel.Visible = false;
        EmitSignal(SignalName.OnDialogDismissed);
    }

    private void ShowDialog()
    {
        _isVisible = true;
        _panel.Visible = true;
    }

    // ── Rendering ──────────────────────────────────────────────────────

    private void RenderEvent(AiEventPayload payload)
    {
        // Clear previous buttons
        foreach (var cb in _choiceButtons)
        {
            cb.Button.Pressed -= () => { }; // clear handler
            cb.Button.QueueFree();
        }
        _choiceButtons.Clear();

        // Title
        _titleLabel.Text = payload.EventTitle;

        // Trigger
        _triggerLabel.Text = string.IsNullOrEmpty(payload.TriggerFactor)
            ? ""
            : $"Triggered by: {payload.TriggerFactor}";

        // Narrative with BBCode formatting
        _narrativeLabel.Text = FormatNarrative(payload.Narrative);

        // Choice buttons
        char optionLabel = 'A';
        foreach (var option in payload.Options)
        {
            var button = CreateChoiceButton(option, optionLabel);
            _buttonContainer.AddChild(button);
            _choiceButtons.Add(new ChoiceButtonData { Option = option, Button = button });
            optionLabel++;
        }

        // Resize panel to fit content
        CallDeferred(nameof(AdjustPanelSize));
    }

    private Button CreateChoiceButton(AiEventOption option, char label)
    {
        var button = new Button();
        button.Text = $"[{label}] {option.Description}";
        button.AddThemeFontSizeOverride("font_size", 14);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.Alignment = HorizontalAlignment.Left;

        // Show cost/risk consequences on hover via tooltip
        button.TooltipText = BuildConsequenceTooltip(option);

        // Style based on risk profile
        var style = new StyleBoxFlat();
        if (option.Effects.Heat > 0 || option.Effects.Reputation < -5)
        {
            // Risky option — red tint
            style.BgColor = new Color(0.35f, 0.1f, 0.1f);
        }
        else if (option.Effects.Cash > 100 || option.Effects.Reputation > 3)
        {
            // High reward — green tint
            style.BgColor = new Color(0.1f, 0.25f, 0.1f);
        }
        else
        {
            // Neutral/safe — blue tint
            style.BgColor = new Color(0.1f, 0.15f, 0.35f);
        }
        button.AddThemeStyleboxOverride("normal", style);

        // Bind click handler
        char capturedLabel = label;
        button.Pressed += () => OnChoiceClicked(option, capturedLabel);

        return button;
    }

    /// <summary>Build a tooltip showing the consequences of a choice.</summary>
    private string BuildConsequenceTooltip(AiEventOption option)
    {
        var e = option.Effects;
        var parts = new List<string>();

        if (e.Cash != 0)
            parts.Add($"Cash: {(e.Cash >= 0 ? "+" : "")}${e.Cash:F0}");
        if (e.Heat != 0)
            parts.Add($"Heat: {(e.Heat >= 0 ? "+" : "-")}{Math.Abs(e.Heat):F0}");
        if (e.Reputation != 0)
            parts.Add($"Reputation: {(e.Reputation >= 0 ? "+" : "-")}{Math.Abs(e.Reputation):F0}");
        if (e.PublicSentiment != 0)
            parts.Add($"Sentiment: {(e.PublicSentiment >= 0 ? "+" : "-")}{Math.Abs(e.PublicSentiment):F0}");
        if (e.StaffEffects != null && e.StaffEffects.Count > 0)
            parts.Add($"Affects {e.StaffEffects.Count} staff member(s)");

        return parts.Count > 0
            ? "Consequences: " + string.Join(" | ", parts)
            : "No significant consequences.";
    }

    private string FormatNarrative(string narrative)
    {
        if (string.IsNullOrWhiteSpace(narrative)) return "(no narrative)";

        // Basic BBCode formatting for readability
        return $"[font_size=15]{narrative}[/font_size]";
    }

    private void AdjustPanelSize()
    {
        // Auto-size based on content
        float contentHeight = 180f + (_choiceButtons.Count * 50f);
        contentHeight += _narrativeLabel.GetContentHeight();
        _panel.SetSize(new Vector2(600, Mathf.Clamp(contentHeight, 250f, 600f)));
    }

    // ── Choice Execution ───────────────────────────────────────────────

    private void OnChoiceClicked(AiEventOption option, char label)
    {
        if (!_isVisible) return;

        GD.Print($"[EventDialogUI] Player chose [{label}] {option.Description}");

        // Apply effects to game systems
        ApplyEffects(option.Effects);

        // Emit signal for external systems to react
        EmitSignal(SignalName.OnChoiceSelected,
            _currentEvent?.EventTitle ?? "",
            option.Label ?? $"{label}",
            option.Effects.Cash,
            option.Effects.Heat);

        // Disable all buttons to prevent double-clicks
        foreach (var cb in _choiceButtons)
            cb.Button.Disabled = true;

        // Auto-dismiss after a short delay
        var timer = GetTree().CreateTimer(1.5f);
        timer.Timeout += () => HideDialog();
    }

    /// <summary>
    /// Apply the effects of a player choice to the relevant game systems.
    /// </summary>
    private void ApplyEffects(AiChoiceEffects effects)
    {
        var gsm = GameStateManager.Instance;

        // ── Cash ────────────────────────────────────────────────────
        if (effects.Cash != 0 && gsm != null)
        {
            gsm.Cash += effects.Cash;
            GD.Print($"[EventDialogUI] Cash {(effects.Cash >= 0 ? "+" : "")}${effects.Cash:F0} → ${gsm.Cash:F2}");
        }

        // ── Heat ────────────────────────────────────────────────────
        if (effects.Heat != 0)
        {
            var heatSystem = FindNode<HeatSystem>("HeatSystem");
            if (heatSystem != null)
            {
                if (effects.Heat > 0)
                    heatSystem.AddHeat(effects.Heat);
                else
                    heatSystem.RemoveHeat(-effects.Heat);

                GD.Print($"[EventDialogUI] Heat {(effects.Heat >= 0 ? "+" : "")}{effects.Heat:F0} → {heatSystem.Heat:F1}");
            }
            else if (gsm != null)
            {
                // Fallback: modify GSM directly
                gsm.Heat = Mathf.Clamp(gsm.Heat + effects.Heat, 0f, 100f);
            }
        }

        // ── Reputation ──────────────────────────────────────────────
        if (effects.Reputation != 0 && gsm != null)
        {
            gsm.Reputation += effects.Reputation;
            GD.Print($"[EventDialogUI] Reputation {(effects.Reputation >= 0 ? "+" : "")}{effects.Reputation:F0}");
        }

        // ── Public Sentiment ────────────────────────────────────────
        if (effects.PublicSentiment != 0 && gsm != null)
        {
            gsm.PublicSentiment += effects.PublicSentiment;
            GD.Print($"[EventDialogUI] PublicSentiment {(effects.PublicSentiment >= 0 ? "+" : "")}{effects.PublicSentiment:F0}");
        }

        // ── Staff Effects ───────────────────────────────────────────
        if (effects.StaffEffects != null && effects.StaffEffects.Count > 0)
        {
            ApplyStaffEffects(effects.StaffEffects);
        }
    }

    private void ApplyStaffEffects(List<AiStaffEffect> staffEffects)
    {
        var allStaff = FindAllStaff();

        foreach (var se in staffEffects)
        {
            if (string.IsNullOrWhiteSpace(se.StaffName)) continue;

            var matched = allStaff.Find(s =>
                s.StaffName.Contains(se.StaffName, StringComparison.OrdinalIgnoreCase));

            if (matched == null)
            {
                GD.Print($"[EventDialogUI] Staff '{se.StaffName}' not found for effect application.");
                continue;
            }

            if (se.StressDelta != 0)
                matched.Stress += se.StressDelta;
            if (se.SatisfactionDelta != 0)
                matched.Satisfaction += se.SatisfactionDelta;
            if (se.TraumaDelta != 0)
                matched.Trauma += se.TraumaDelta;

            GD.Print($"[EventDialogUI] Applied to {matched.StaffName}: " +
                     $"Stress {(se.StressDelta >= 0 ? "+" : "")}{se.StressDelta:F0} " +
                     $"Sat {(se.SatisfactionDelta >= 0 ? "+" : "")}{se.SatisfactionDelta:F0} " +
                     $"Trauma {(se.TraumaDelta >= 0 ? "+" : "")}{se.TraumaDelta:F0}");
        }
    }

    // ── Utility ────────────────────────────────────────────────────────

    private T FindNode<T>(string name) where T : Node
    {
        if (GetTree()?.Root == null) return null;
        return GetTree().Root.FindChild(name, recursive: true, owned: false) as T;
    }

    private List<StaffMember> FindAllStaff()
    {
        // StaffRoster is the canonical owner. The previous implementation read
        // PsychologicalBreakSystem.GetStaffAtRisk(), which filters to Stress >= 80.
        return StaffRoster.Instance?.GetAll().ToList() ?? new List<StaffMember>();
    }

    /// <summary>
    /// Parse a JSON string without displaying it — for validation.
    /// </summary>
    public AiEventPayload ParseEventJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AiEventPayload>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public override string ToString()
    {
        return $"[EventDialogUI] Visible={IsShowing} " +
               $"Event=\"{_currentEvent?.EventTitle ?? "none"}\"";
    }
}
