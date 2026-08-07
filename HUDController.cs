using Godot;
using System;

/// <summary>
/// Persistent HUD overlay attached to a CanvasLayer.
/// Displays Cash, Venue Prestige (star rating), Public Sentiment,
/// and a dynamic Heat progress bar with color-coded ranges.
///
/// Binds to GameStateManager signals for efficient updates —
/// no per-frame polling, zero GC spikes from string allocations.
/// </summary>
public partial class HUDController : CanvasLayer
{
    // ── Node References ────────────────────────────────────────────────
    private Label _cashLabel;
    private Label _prestigeLabel;
    private Label _sentimentLabel;
    private ProgressBar _heatBar;
    private Label _heatLabel;
    private PanelContainer _topBar;
    private ColorRect _heatFillRect;
    private StyleBoxFlat _heatStyleNormal;
    private StyleBoxFlat _heatStyleWarning;
    private StyleBoxFlat _heatStyleDanger;

    // ── Animation ──────────────────────────────────────────────────────
    private float _pulseTime;
    private bool _isDangerZone;

    // ── Cached Strings (avoids per-frame allocations) ──────────────────
    private string _cachedCash = "$0";
    private string _cachedPrestige = "★ 1.0";
    private string _cachedSentiment = "0%";
    private float _lastCash = float.MinValue;
    private float _lastReputation = float.MinValue;
    private float _lastSentiment = float.MinValue;
    private float _lastHeat = float.MinValue;

    // ── Styling Constants ──────────────────────────────────────────────
    private static readonly Color GreenZone = new(0.15f, 0.75f, 0.25f);
    private static readonly Color YellowZone = new(0.85f, 0.75f, 0.15f);
    private static readonly Color RedZone = new(0.85f, 0.15f, 0.15f);
    private static readonly Color RedPulseMin = new(0.85f, 0.15f, 0.15f);
    private static readonly Color RedPulseMax = new(1.0f, 0.25f, 0.2f);

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUI();
        ConnectSignals();

        // Initial update
        RefreshAll(GameStateManager.Instance);

        GD.Print("[HUDController] Initialized.");
    }

    public override void _Process(double delta)
    {
        // Pulse animation for danger zone (only when active)
        if (_isDangerZone)
        {
            _pulseTime += (float)delta * 4f;
            float t = (Mathf.Sin(_pulseTime) + 1f) / 2f;
            _heatStyleDanger.BgColor = RedPulseMin.Lerp(RedPulseMax, t);
            _heatBar.AddThemeStyleboxOverride("fill", _heatStyleDanger);
        }
    }

    public override void _ExitTree()
    {
        DisconnectSignals();
    }

    // ── UI Construction ────────────────────────────────────────────────

    private void BuildUI()
    {
        // Top bar container
        _topBar = new PanelContainer();
        _topBar.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _topBar.SetSize(new Vector2(0, 48));
        _topBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.08f, 0.92f),
            BorderWidthBottom = 2,
            BorderColor = new Color(0.3f, 0.25f, 0.15f)
        });

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        _topBar.AddChild(margin);

        var hbox = new HBoxContainer();
        hbox.Alignment = BoxContainer.AlignmentMode.Begin;
        hbox.AddThemeConstantOverride("separation", 24);
        margin.AddChild(hbox);

        // Cash label
        _cashLabel = CreateLabel("$0", new Color(0.3f, 0.9f, 0.4f), 16);
        hbox.AddChild(_cashLabel);

        hbox.AddChild(new VSeparator());

        // Prestige label (star rating)
        _prestigeLabel = CreateLabel("★ 1.0", new Color(1f, 0.85f, 0.3f), 16);
        hbox.AddChild(_prestigeLabel);

        hbox.AddChild(new VSeparator());

        // Sentiment label
        _sentimentLabel = CreateLabel("0%", new Color(0.6f, 0.75f, 1f), 14);
        hbox.AddChild(_sentimentLabel);

        // Spacer
        var spacer = new Control();
        spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hbox.AddChild(spacer);

        // Heat section
        var heatContainer = new HBoxContainer();
        heatContainer.AddThemeConstantOverride("separation", 8);
        hbox.AddChild(heatContainer);

        _heatLabel = CreateLabel("Heat", new Color(0.7f, 0.7f, 0.7f), 12);
        heatContainer.AddChild(_heatLabel);

        _heatBar = new ProgressBar();
        _heatBar.CustomMinimumSize = new Vector2(120, 18);
        _heatBar.MinValue = 0;
        _heatBar.MaxValue = 100;
        _heatBar.ShowPercentage = false;
        _heatBar.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color(0.15f, 0.15f, 0.15f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4
        });

        // Pre-create styleboxes for heat bar zones
        _heatStyleNormal = new StyleBoxFlat
        {
            BgColor = GreenZone,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4
        };
        _heatStyleWarning = new StyleBoxFlat
        {
            BgColor = YellowZone,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4
        };
        _heatStyleDanger = new StyleBoxFlat
        {
            BgColor = RedZone,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4
        };

        _heatBar.AddThemeStyleboxOverride("fill", _heatStyleNormal);
        heatContainer.AddChild(_heatBar);

        AddChild(_topBar);
    }

    private static Label CreateLabel(string text, Color color, int fontSize)
    {
        var label = new Label();
        label.Text = text;
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    // ── Signal Wiring ──────────────────────────────────────────────────

    private void ConnectSignals()
    {
        var gsm = GameStateManager.Instance;
        if (gsm == null) return;

        gsm.OnCashChanged += OnCashChanged;
        gsm.OnReputationChanged += OnReputationChanged;
        gsm.OnHeatChanged += OnHeatChanged;
        gsm.OnPublicSentimentChanged += OnPublicSentimentChanged;
    }

    private void DisconnectSignals()
    {
        var gsm = GameStateManager.Instance;
        if (gsm == null) return;

        gsm.OnCashChanged -= OnCashChanged;
        gsm.OnReputationChanged -= OnReputationChanged;
        gsm.OnHeatChanged -= OnHeatChanged;
        gsm.OnPublicSentimentChanged -= OnPublicSentimentChanged;
    }

    // ── Signal Handlers (delta-driven, no per-frame allocations) ──────

    private void OnCashChanged(double newValue, double delta)
    {
        // Only update label if value actually changed enough to display differently
        if (Math.Abs(newValue - _lastCash) < 0.5) return;
        _lastCash = (float)newValue;

        if (newValue < 0)
            _cashLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.2f, 0.2f));
        else
            _cashLabel.AddThemeColorOverride("font_color", new Color(0.3f, 0.9f, 0.4f));

        _cashLabel.Text = FormatCurrency(newValue);
    }

    private void OnReputationChanged(float newValue, float delta)
    {
        if (Mathf.IsEqualApprox(newValue, _lastReputation)) return;
        _lastReputation = newValue;

        float stars = Mathf.Clamp(newValue / 20f, 1f, 5f);
        int fullStars = Mathf.FloorToInt(stars);
        float frac = stars - fullStars;
        float displayStars = fullStars + (frac >= 0.5f ? 0.5f : 0f);

        _prestigeLabel.Text = $"★ {displayStars:F1}";
    }

    private void OnHeatChanged(float newValue, float delta)
    {
        if (Mathf.IsEqualApprox(newValue, _lastHeat)) return;
        _lastHeat = newValue;

        _heatBar.Value = newValue;

        if (newValue <= 40f)
        {
            // Green zone
            _isDangerZone = false;
            _heatBar.AddThemeStyleboxOverride("fill", _heatStyleNormal);
            _heatLabel.AddThemeColorOverride("font_color", GreenZone);
            _heatLabel.Text = $"Heat {newValue:F0}%";
        }
        else if (newValue <= 70f)
        {
            // Yellow zone
            _isDangerZone = false;
            _heatStyleWarning.BgColor = YellowZone;
            _heatBar.AddThemeStyleboxOverride("fill", _heatStyleWarning);
            _heatLabel.AddThemeColorOverride("font_color", YellowZone);
            _heatLabel.Text = $"⚠ Heat {newValue:F0}%";
        }
        else
        {
            // Red danger zone — activate pulse
            _isDangerZone = true;
            _pulseTime = 0f; // reset pulse phase
            _heatLabel.AddThemeColorOverride("font_color", RedZone);
            _heatLabel.Text = $"🔥 HEAT {newValue:F0}%";
        }
    }

    private void OnPublicSentimentChanged(float newValue, float delta)
    {
        if (Mathf.IsEqualApprox(newValue, _lastSentiment)) return;
        _lastSentiment = newValue;

        Color sentimentColor = newValue switch
        {
            >= 70 => new Color(0.3f, 0.85f, 0.4f),   // green — positive
            >= 40 => new Color(0.85f, 0.75f, 0.2f),   // yellow — neutral
            _     => new Color(0.9f, 0.3f, 0.3f)      // red — negative
        };

        _sentimentLabel.AddThemeColorOverride("font_color", sentimentColor);
        _sentimentLabel.Text = $"Public: {newValue:F0}%";
    }

    // ── Bulk Refresh ───────────────────────────────────────────────────

    private void RefreshAll(GameStateManager gsm)
    {
        if (gsm == null) return;

        OnCashChanged(gsm.Cash, 0);
        OnReputationChanged(gsm.Reputation, 0);
        OnHeatChanged(gsm.Heat, 0);
        OnPublicSentimentChanged(gsm.PublicSentiment, 0);
    }

    // ── Formatting ─────────────────────────────────────────────────────

    private static string FormatCurrency(double value)
    {
        if (value >= 1_000_000)
            return $"${value / 1_000_000:F2}M";
        if (value >= 10_000)
            return $"${value / 1_000:F1}K";
        if (value < 0)
            return $"-${Math.Abs(value):F0}";
        return $"${value:F0}";
    }
}
