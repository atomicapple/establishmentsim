using Godot;
using System;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>
/// The three quick-action tools that live both as top-bar status chips and
/// as the large round buttons in the bottom-left corner.
/// </summary>
public enum HudTool
{
    /// <summary>Housekeeping — mop the rooms between encounters.</summary>
    Cleaning,

    /// <summary>Trouble — incidents, raids, anything needing a decision now.</summary>
    Alert,

    /// <summary>Inspect — read the room/staff detail under the cursor.</summary>
    Info
}

/// <summary>
/// Procedural StyleBox factory for the HUD chrome.
///
/// Every colour here is derived from <see cref="IsoTheme"/> rather than
/// invented, so a palette change in one place moves the whole interface.
/// Shared by <see cref="GameHud"/> and <see cref="BuildPanel"/> — the two are
/// meant to read as one piece of framing, and two copies of these numbers
/// would drift apart the first time a corner radius changed.
/// </summary>
public static class HudStyle
{
    /// <summary>Fill for the main chrome panels.</summary>
    public static Color PanelFill => IsoTheme.Facade.Darkened(0.35f);

    /// <summary>Fill for recessed rows inside a panel.</summary>
    public static Color RowFill => IsoTheme.FacadeShadow;

    /// <summary>Fill for an interactive control at rest.</summary>
    public static Color ControlFill => IsoTheme.Facade;

    /// <summary>A flat rounded box with a hairline trim.</summary>
    public static StyleBoxFlat Box(
        Color fill, Color border, int radius = 10, int borderWidth = 2, int padding = 8)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            ContentMarginLeft = padding,
            ContentMarginRight = padding,
            ContentMarginTop = Mathf.Max(2, padding / 2),
            ContentMarginBottom = Mathf.Max(2, padding / 2)
        };

        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        return box;
    }

    /// <summary>The standard art-nouveau framed panel: dark plum, gold trim.</summary>
    public static StyleBoxFlat FramedPanel(int radius = 12, int padding = 10)
    {
        var box = Box(PanelFill, IsoTheme.GoldDim, radius, 2, padding);
        box.BgColor = new Color(PanelFill, 0.94f);
        return box;
    }

    /// <summary>
    /// Dress a button in the HUD's language. <paramref name="accent"/> tints
    /// the trim and the hover state, which is how Alert/Danger buttons read
    /// as different without a second stylesheet.
    /// </summary>
    public static void StyleButton(
        Button button, Color accent, int radius = 8, int fontSize = 13, int padding = 10)
    {
        if (button == null) return;

        var fill = ControlFill;

        button.AddThemeStyleboxOverride("normal", Box(fill, accent.Darkened(0.35f), radius, 2, padding));
        button.AddThemeStyleboxOverride("hover", Box(fill.Lightened(0.12f), accent, radius, 2, padding));
        button.AddThemeStyleboxOverride("pressed", Box(accent.Darkened(0.55f), accent, radius, 2, padding));
        button.AddThemeStyleboxOverride("focus", Box(new Color(fill, 0f), accent, radius, 1, padding));
        button.AddThemeStyleboxOverride("disabled",
            Box(IsoTheme.FacadeShadow, IsoTheme.FacadeShadow.Lightened(0.15f), radius, 2, padding));

        button.AddThemeColorOverride("font_color", IsoTheme.TextPrimary);
        button.AddThemeColorOverride("font_hover_color", IsoTheme.Gold);
        button.AddThemeColorOverride("font_pressed_color", IsoTheme.TextPrimary);
        button.AddThemeColorOverride("font_focus_color", IsoTheme.TextPrimary);
        button.AddThemeColorOverride("font_disabled_color", IsoTheme.TextMuted.Darkened(0.35f));
        button.AddThemeFontSizeOverride("font_size", fontSize);
    }

    /// <summary>A label in the HUD's type scale.</summary>
    public static Label MakeLabel(string text, int fontSize, Color colour)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }
}

// ── Satisfaction Face ──────────────────────────────────────────────────

/// <summary>
/// The little face on the Client Satisfaction meter, drawn procedurally
/// because the HUD ships without external assets. The mouth curve tracks the
/// 0–10 value so the reading is legible before the number is.
/// </summary>
public partial class SatisfactionFace : Control
{
    private float _value = 5f;

    /// <summary>Satisfaction, 0–10. Redraws on change.</summary>
    public float Value
    {
        get => _value;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, 10f);
            if (Mathf.IsEqualApprox(clamped, _value)) return;

            _value = clamped;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        var box = Size;
        var radius = Mathf.Max(6f, Mathf.Min(box.X, box.Y) * 0.5f - 2f);
        var centre = box * 0.5f;
        var tint = IsoTheme.GetScoreColor(_value * 10f);

        DrawCircle(centre, radius, new Color(tint, 0.22f));
        DrawArc(centre, radius, 0f, Mathf.Tau, 32, tint, 2f, true);

        // Eyes.
        var eyeOffset = radius * 0.36f;
        var eyeRadius = Mathf.Max(1.2f, radius * 0.11f);
        DrawCircle(centre + new Vector2(-eyeOffset, -eyeOffset * 0.55f), eyeRadius, tint);
        DrawCircle(centre + new Vector2(eyeOffset, -eyeOffset * 0.55f), eyeRadius, tint);

        // Mouth: a quadratic sampled by hand. Curvature is signed, so an
        // unhappy house frowns rather than simply smiling less.
        var curve = (_value - 5f) / 5f * radius * 0.45f;
        var left = centre + new Vector2(-radius * 0.45f, radius * 0.22f);
        var right = centre + new Vector2(radius * 0.45f, radius * 0.22f);
        var control = centre + new Vector2(0f, radius * 0.22f + curve);

        var points = new Vector2[9];
        for (int i = 0; i < points.Length; i++)
        {
            float t = i / (float)(points.Length - 1);
            var a = left.Lerp(control, t);
            var b = control.Lerp(right, t);
            points[i] = a.Lerp(b, t);
        }

        DrawPolyline(points, tint, 2f, true);
    }
}

// ── GameHud ────────────────────────────────────────────────────────────

/// <summary>
/// The chrome around the isometric dollhouse: top status bar, right-hand
/// Build/Decorate panel, bottom-left tool buttons, bottom-centre night
/// controls, the night clock, and the floor selector.
///
/// The HUD is deliberately inert. It reads <see cref="NightDirector"/> and
/// <see cref="VenueBuilding"/>, it drives the night beats through the
/// director's own public API, and everything else it emits as a signal for
/// the integrator to route. It never touches the renderer — that keeps the
/// view and the chrome independently replaceable.
///
/// Built entirely in code. Usage:
/// <code>
/// var hud = new GameHud();
/// AddChild(hud);
/// hud.Bind(nightDirector, venueBuilding);
/// </code>
/// Both arguments may be null; the HUD degrades to a read-only shell rather
/// than throwing, which is what lets it be added before the systems exist.
/// </summary>
public partial class GameHud : CanvasLayer
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>The player asked to look at the floor above.</summary>
    [Signal]
    public delegate void OnFloorUpRequestedEventHandler();

    /// <summary>The player asked to look at the floor below.</summary>
    [Signal]
    public delegate void OnFloorDownRequestedEventHandler();

    /// <summary>A room type was chosen in the Build panel. Carries the <see cref="RoomType"/> value.</summary>
    [Signal]
    public delegate void OnBuildRoomRequestedEventHandler(int roomType);

    /// <summary>A time scale was chosen: 1, 2 or 3.</summary>
    [Signal]
    public delegate void OnSpeedRequestedEventHandler(float scale);

    /// <summary>The pause button was pressed. Read <see cref="IsPaused"/> for the new state.</summary>
    [Signal]
    public delegate void OnPauseToggledEventHandler();

    /// <summary>A quick-action tool was picked. Carries the <see cref="HudTool"/> value.</summary>
    [Signal]
    public delegate void OnToolRequestedEventHandler(int tool);

    /// <summary>The hamburger menu was pressed.</summary>
    [Signal]
    public delegate void OnMenuRequestedEventHandler();

    /// <summary>A floor purchase was requested from the Upgrades tab.</summary>
    [Signal]
    public delegate void OnBuyFloorRequestedEventHandler(int floor);

    /// <summary>A staff member was picked in the Staff tab.</summary>
    [Signal]
    public delegate void OnStaffSelectedEventHandler(string staffId);

    // ── Layout Constants ───────────────────────────────────────────────

    private const int TopBarHeight = 58;
    private const int BuildPanelWidth = 268;
    private const int ToolButtonSize = 62;

    // ── Bound Systems ──────────────────────────────────────────────────

    private NightDirector _night;
    private VenueBuilding _venue;
    private GameStateManager _state;

    // ── Nodes ──────────────────────────────────────────────────────────

    private Control _root;
    private Label _cashLabel;
    private Label _reputationLabel;
    private Label _heatLabel;
    private Label _nightLabel;
    private Label _satisfactionValue;
    private SatisfactionFace _satisfactionFace;

    private Button _menuButton;
    private readonly Button[] _statusChips = new Button[3];
    private readonly Button[] _toolButtons = new Button[3];

    private Label _phaseLabel;
    private Button _nightButton;
    private Button _pauseButton;
    private readonly Button[] _speedButtons = new Button[3];

    private ProgressBar _nightClock;
    private Label _nightClockLabel;

    private Button _floorUpButton;
    private Button _floorDownButton;
    private Label _floorLabel;

    private BuildPanel _buildPanel;

    // ── State ──────────────────────────────────────────────────────────

    private NightPhase _lastPhase = (NightPhase)(-1);
    private int _lastClockMinute = -1;
    private bool _connected;

    /// <summary>The floor the HUD believes the view is showing.</summary>
    public int CurrentFloor { get; private set; }

    /// <summary>Whether the pause toggle is currently engaged.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>The time scale the player last asked for: 1, 2 or 3.</summary>
    public float SpeedScale { get; private set; } = 1f;

    /// <summary>The right-hand Build/Decorate panel, for direct inspection.</summary>
    public BuildPanel Build => _buildPanel;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUi();
        RefreshAll();
        GD.Print("[GameHud] Ready.");
    }

    public override void _ExitTree() => DisconnectState();

    public override void _Process(double delta)
    {
        RefreshPhase();
        RefreshClock();
    }

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Wire the HUD to the running systems. Safe to call before or after the
    /// node enters the tree, safe to call again, and safe with nulls.
    /// </summary>
    public void Bind(NightDirector night, VenueBuilding venue)
    {
        _night = night;
        _venue = venue;

        if (_root == null) BuildUi();

        _buildPanel?.Bind(venue);

        // Force the next refresh through even if the phase happens to match
        // what the shell was already showing.
        _lastPhase = (NightPhase)(-1);

        ConnectState();
        ClampFloorToBuilding();
        RefreshAll();
    }

    /// <summary>
    /// Tell the HUD which floor the view actually settled on. The integrator
    /// calls this after handling a floor request — the HUD moves its own
    /// label optimistically, but the view is the authority.
    /// </summary>
    public void SetCurrentFloor(int floor)
    {
        CurrentFloor = floor;
        RefreshFloor();
    }

    /// <summary>Override the Client Satisfaction reading, 0–10.</summary>
    public void SetClientSatisfaction(float value0To10)
    {
        var clamped = Mathf.Clamp(value0To10, 0f, 10f);

        if (_satisfactionFace != null) _satisfactionFace.Value = clamped;
        if (_satisfactionValue == null) return;

        _satisfactionValue.Text = $"{clamped:0.0}";
        _satisfactionValue.AddThemeColorOverride("font_color", IsoTheme.GetScoreColor(clamped * 10f));
    }

    /// <summary>
    /// Set the caption and highlight of one top-bar status chip. An active
    /// chip is tinted with the tool's accent so it reads at a glance.
    /// </summary>
    public void SetStatusChip(HudTool tool, string text, bool active)
    {
        var chip = _statusChips[(int)tool];
        if (chip == null) return;

        chip.Text = text ?? ToolLabel(tool);
        HudStyle.StyleButton(chip, active ? ToolAccent(tool) : IsoTheme.GoldDim, 12, 12, 12);
    }

    /// <summary>Repaint every live reading from the bound systems.</summary>
    public void RefreshAll()
    {
        RefreshMetrics();
        RefreshPhase();
        RefreshClock();
        RefreshFloor();
        _buildPanel?.Refresh();
    }

    // ── Signal Wiring ──────────────────────────────────────────────────

    private void ConnectState()
    {
        var state = GameStateManager.Instance;
        if (_connected && ReferenceEquals(state, _state)) return;

        DisconnectState();
        _state = state;
        if (_state == null) return;

        _state.OnCashChanged += HandleCashChanged;
        _state.OnReputationChanged += HandleReputationChanged;
        _state.OnHeatChanged += HandleHeatChanged;
        _state.OnPublicSentimentChanged += HandleSentimentChanged;
        _connected = true;
    }

    private void DisconnectState()
    {
        if (!_connected || _state == null || !IsInstanceValid(_state))
        {
            _connected = false;
            _state = null;
            return;
        }

        _state.OnCashChanged -= HandleCashChanged;
        _state.OnReputationChanged -= HandleReputationChanged;
        _state.OnHeatChanged -= HandleHeatChanged;
        _state.OnPublicSentimentChanged -= HandleSentimentChanged;

        _connected = false;
        _state = null;
    }

    private void HandleCashChanged(double newValue, double delta)
    {
        if (_cashLabel != null) _cashLabel.Text = $"${newValue:N0}";

        // Affordability is the only thing in the build list that moves with
        // cash, so a targeted refresh beats rebuilding the panel.
        _buildPanel?.Refresh();
    }

    private void HandleReputationChanged(float newValue, float delta)
    {
        if (_reputationLabel == null) return;

        _reputationLabel.Text = $"{newValue:0}";
        _reputationLabel.AddThemeColorOverride("font_color", IsoTheme.GetScoreColor(newValue));
    }

    private void HandleHeatChanged(float newValue, float delta)
    {
        if (_heatLabel == null) return;

        _heatLabel.Text = $"{newValue:0}";

        // Heat is inverted: high is bad, so the ramp is read backwards.
        _heatLabel.AddThemeColorOverride("font_color", IsoTheme.GetScoreColor(100f - newValue));
    }

    private void HandleSentimentChanged(float newValue, float delta) =>
        SetClientSatisfaction(newValue / 10f);

    // ── Construction ───────────────────────────────────────────────────

    private void BuildUi()
    {
        if (_root != null) return;

        _root = new Control
        {
            Name = "HudRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        BuildTopBar();
        BuildRightPanel();
        BuildToolCluster();
        BuildNightControls();
        BuildFloorSelector();
    }

    private void BuildTopBar()
    {
        var bar = new PanelContainer { Name = "TopBar" };
        bar.AddThemeStyleboxOverride("panel", HudStyle.Box(
            new Color(IsoTheme.Backdrop, 0.95f), IsoTheme.GoldDim, 0, 2, 0));
        bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        bar.OffsetBottom = TopBarHeight;
        _root.AddChild(bar);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        bar.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        margin.AddChild(row);

        _menuButton = new Button { Text = "≡", CustomMinimumSize = new Vector2(40, 34) };
        HudStyle.StyleButton(_menuButton, IsoTheme.Gold, 8, 20, 6);
        _menuButton.Pressed += () => EmitSignal(SignalName.OnMenuRequested);
        row.AddChild(_menuButton);

        row.AddChild(MakeSeparator());

        for (int i = 0; i < _statusChips.Length; i++)
        {
            var tool = (HudTool)i;
            var chip = new Button { Text = ToolLabel(tool), Flat = false };
            HudStyle.StyleButton(chip, IsoTheme.GoldDim, 12, 12, 12);
            chip.Pressed += () => EmitSignal(SignalName.OnToolRequested, (int)tool);
            _statusChips[i] = chip;
            row.AddChild(chip);
        }

        row.AddChild(MakeSeparator());

        _cashLabel = AddMetric(row, "CASH", "$0", IsoTheme.Gold, out _);
        _reputationLabel = AddMetric(row, "REP", "0", IsoTheme.TextPrimary, out _);
        _heatLabel = AddMetric(row, "HEAT", "0", IsoTheme.TextPrimary, out _);
        _nightLabel = AddMetric(row, "NIGHT", "1", IsoTheme.TextPrimary, out _);

        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        // ── Client Satisfaction ────────────────────────────────────────
        var meter = new PanelContainer();
        meter.AddThemeStyleboxOverride("panel", HudStyle.Box(
            HudStyle.RowFill, IsoTheme.GoldDim, 14, 2, 10));
        row.AddChild(meter);

        var meterRow = new HBoxContainer();
        meterRow.AddThemeConstantOverride("separation", 8);
        meter.AddChild(meterRow);

        _satisfactionFace = new SatisfactionFace
        {
            CustomMinimumSize = new Vector2(30, 30),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        meterRow.AddChild(_satisfactionFace);

        var meterText = new VBoxContainer();
        meterText.AddThemeConstantOverride("separation", 0);
        meterRow.AddChild(meterText);

        meterText.AddChild(HudStyle.MakeLabel("CLIENT SATISFACTION", 9, IsoTheme.TextMuted));

        _satisfactionValue = HudStyle.MakeLabel("5.0", 17, IsoTheme.TextPrimary);
        meterText.AddChild(_satisfactionValue);
    }

    private Label AddMetric(
        HBoxContainer row, string caption, string value, Color valueColour, out VBoxContainer holder)
    {
        holder = new VBoxContainer();
        holder.AddThemeConstantOverride("separation", 0);
        row.AddChild(holder);

        holder.AddChild(HudStyle.MakeLabel(caption, 9, IsoTheme.TextMuted));

        var label = HudStyle.MakeLabel(value, 16, valueColour);
        holder.AddChild(label);
        return label;
    }

    private static Control MakeSeparator() => new VSeparator();

    private void BuildRightPanel()
    {
        _buildPanel = new BuildPanel { Name = "BuildPanel" };
        _buildPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.RightWide);
        _buildPanel.OffsetLeft = -BuildPanelWidth - 12;
        _buildPanel.OffsetRight = -12;
        _buildPanel.OffsetTop = TopBarHeight + 12;
        _buildPanel.OffsetBottom = -150;
        _root.AddChild(_buildPanel);

        _buildPanel.OnBuildRoomRequested += type => EmitSignal(SignalName.OnBuildRoomRequested, type);
        _buildPanel.OnBuyFloorRequested += floor => EmitSignal(SignalName.OnBuyFloorRequested, floor);
        _buildPanel.OnStaffSelected += id => EmitSignal(SignalName.OnStaffSelected, id);
    }

    private void BuildToolCluster()
    {
        var cluster = new HBoxContainer { Name = "ToolCluster" };
        cluster.AddThemeConstantOverride("separation", 12);
        cluster.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        cluster.OffsetLeft = 18;
        cluster.OffsetTop = -(ToolButtonSize + 18);
        cluster.OffsetBottom = -18;
        _root.AddChild(cluster);

        for (int i = 0; i < _toolButtons.Length; i++)
        {
            var tool = (HudTool)i;
            var button = new Button
            {
                Text = ToolGlyph(tool),
                TooltipText = ToolLabel(tool),
                CustomMinimumSize = new Vector2(ToolButtonSize, ToolButtonSize)
            };

            // A large corner radius on a square button is a circle.
            HudStyle.StyleButton(button, ToolAccent(tool), ToolButtonSize / 2, 22, 4);
            button.Pressed += () => EmitSignal(SignalName.OnToolRequested, (int)tool);

            _toolButtons[i] = button;
            cluster.AddChild(button);
        }
    }

    private void BuildNightControls()
    {
        var panel = new PanelContainer { Name = "NightControls" };
        panel.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(14, 12));
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        panel.OffsetLeft = -190;
        panel.OffsetRight = 190;
        panel.OffsetTop = -128;
        panel.OffsetBottom = -18;
        _root.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);

        // ── Night clock ────────────────────────────────────────────────
        var clockRow = new HBoxContainer();
        clockRow.AddThemeConstantOverride("separation", 8);
        column.AddChild(clockRow);

        _phaseLabel = HudStyle.MakeLabel("IDLE", 11, IsoTheme.TextMuted);
        _phaseLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        clockRow.AddChild(_phaseLabel);

        _nightClockLabel = HudStyle.MakeLabel("21:00", 11, IsoTheme.Gold);
        clockRow.AddChild(_nightClockLabel);

        _nightClock = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 8)
        };
        _nightClock.AddThemeStyleboxOverride("background",
            HudStyle.Box(IsoTheme.Backdrop, IsoTheme.GoldDim, 4, 1, 0));
        _nightClock.AddThemeStyleboxOverride("fill",
            HudStyle.Box(IsoTheme.Gold, IsoTheme.Gold, 4, 0, 0));
        column.AddChild(_nightClock);

        // ── Primary beat button ────────────────────────────────────────
        _nightButton = new Button { Text = "Begin Night", CustomMinimumSize = new Vector2(0, 38) };
        HudStyle.StyleButton(_nightButton, IsoTheme.Gold, 10, 16, 14);
        _nightButton.Pressed += HandleNightButton;
        column.AddChild(_nightButton);

        // ── Speed row ──────────────────────────────────────────────────
        var speedRow = new HBoxContainer();
        speedRow.AddThemeConstantOverride("separation", 6);
        column.AddChild(speedRow);

        for (int i = 0; i < _speedButtons.Length; i++)
        {
            float scale = i + 1;
            var button = new Button
            {
                Text = $"{i + 1}x",
                ToggleMode = true,
                ButtonPressed = i == 0,
                CustomMinimumSize = new Vector2(0, 28),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };

            HudStyle.StyleButton(button, IsoTheme.GoldDim, 8, 13, 6);
            button.Pressed += () => HandleSpeedButton(scale);

            _speedButtons[i] = button;
            speedRow.AddChild(button);
        }

        _pauseButton = new Button
        {
            Text = "‖",
            ToggleMode = true,
            TooltipText = "Pause",
            CustomMinimumSize = new Vector2(44, 28)
        };
        HudStyle.StyleButton(_pauseButton, IsoTheme.Warning, 8, 13, 6);
        _pauseButton.Toggled += HandlePauseToggled;
        speedRow.AddChild(_pauseButton);
    }

    private void BuildFloorSelector()
    {
        var panel = new PanelContainer { Name = "FloorSelector" };
        panel.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(12, 8));
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterLeft);
        panel.OffsetLeft = 18;
        panel.OffsetRight = 118;
        panel.OffsetTop = -62;
        panel.OffsetBottom = 62;
        _root.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);

        _floorUpButton = new Button { Text = "▲", CustomMinimumSize = new Vector2(0, 30) };
        HudStyle.StyleButton(_floorUpButton, IsoTheme.Gold, 8, 14, 4);
        _floorUpButton.Pressed += () => HandleFloorStep(1);
        column.AddChild(_floorUpButton);

        column.AddChild(HudStyle.MakeLabel("FLOOR", 9, IsoTheme.TextMuted));

        _floorLabel = HudStyle.MakeLabel("Ground", 14, IsoTheme.TextPrimary);
        _floorLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _floorLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        column.AddChild(_floorLabel);

        _floorDownButton = new Button { Text = "▼", CustomMinimumSize = new Vector2(0, 30) };
        HudStyle.StyleButton(_floorDownButton, IsoTheme.Gold, 8, 14, 4);
        _floorDownButton.Pressed += () => HandleFloorStep(-1);
        column.AddChild(_floorDownButton);
    }

    // ── Input Handlers ─────────────────────────────────────────────────

    /// <summary>
    /// The one button that moves the night forward. Which beat it advances is
    /// entirely a function of the director's phase, so there is no separate
    /// state to keep in sync.
    /// </summary>
    private void HandleNightButton()
    {
        if (_night == null) return;

        switch (_night.Phase)
        {
            case NightPhase.Idle:
                _night.BeginNight();
                break;
            case NightPhase.Preparation:
                _night.OpenDoors();
                break;
            case NightPhase.Service:
                _night.CloseDoors();
                break;
            case NightPhase.Ledger:
                _night.ConcludeNight();
                break;
        }

        RefreshPhase();
        RefreshMetrics();
    }

    private void HandleSpeedButton(float scale)
    {
        SpeedScale = scale;

        for (int i = 0; i < _speedButtons.Length; i++)
            if (_speedButtons[i] != null)
                _speedButtons[i].ButtonPressed = Mathf.IsEqualApprox(scale, i + 1);

        EmitSignal(SignalName.OnSpeedRequested, scale);
    }

    private void HandlePauseToggled(bool pressed)
    {
        IsPaused = pressed;
        if (_pauseButton != null) _pauseButton.TooltipText = pressed ? "Resume" : "Pause";

        EmitSignal(SignalName.OnPauseToggled);
    }

    /// <summary>
    /// Move the focused floor. The label moves optimistically so the control
    /// feels responsive, but only within floors that actually exist — the
    /// integrator can still correct it via <see cref="SetCurrentFloor"/>.
    /// </summary>
    private void HandleFloorStep(int direction)
    {
        var target = CurrentFloor + direction;

        if (_venue == null || _venue.HasFloor(target))
            CurrentFloor = target;

        RefreshFloor();

        EmitSignal(direction > 0
            ? SignalName.OnFloorUpRequested
            : SignalName.OnFloorDownRequested);
    }

    // ── Refresh ────────────────────────────────────────────────────────

    private void RefreshMetrics()
    {
        var state = GameStateManager.Instance;
        if (state == null) return;

        HandleCashChanged(state.Cash, 0);
        HandleReputationChanged(state.Reputation, 0);
        HandleHeatChanged(state.Heat, 0);
        HandleSentimentChanged(state.PublicSentiment, 0);
    }

    private void RefreshPhase()
    {
        var phase = _night?.Phase ?? NightPhase.Idle;
        if (phase == _lastPhase) return;
        _lastPhase = phase;

        if (_phaseLabel != null)
        {
            _phaseLabel.Text = phase.ToString().ToUpperInvariant();
            _phaseLabel.AddThemeColorOverride("font_color", PhaseColour(phase));
        }

        if (_nightButton != null)
        {
            _nightButton.Text = phase switch
            {
                NightPhase.Preparation => "Open Doors",
                NightPhase.Service => "Close Doors",
                NightPhase.Closing => "Closing…",
                NightPhase.Ledger => "Continue",
                _ => "Begin Night"
            };

            _nightButton.Disabled = _night == null || phase == NightPhase.Closing;
        }

        if (_nightLabel != null)
        {
            var night = _night != null && _night.Phase != NightPhase.Idle
                ? _night.CurrentReport?.Night ?? 1
                : (GameStateManager.Instance?.DayCount ?? 0) + 1;

            _nightLabel.Text = night.ToString();
        }

        // Building is a Preparation activity — the panel greys out once the
        // doors are open rather than letting the player rebuild mid-service.
        _buildPanel?.SetBuildingAllowed(_night == null || phase is NightPhase.Idle or NightPhase.Preparation);
    }

    private void RefreshClock()
    {
        if (_nightClock == null) return;

        var progress = _night?.ServiceProgress ?? 0f;
        _nightClock.Value = progress;

        // Nine in the evening through three in the morning: six hours of
        // service mapped onto the Service beat.
        var minutes = Mathf.RoundToInt(progress * 6f * 60f);
        var rounded = minutes / 5 * 5;
        if (rounded == _lastClockMinute) return;
        _lastClockMinute = rounded;

        var hour = (21 + rounded / 60) % 24;
        if (_nightClockLabel != null)
            _nightClockLabel.Text = $"{hour:00}:{rounded % 60:00}";
    }

    private void RefreshFloor()
    {
        if (_floorLabel != null) _floorLabel.Text = FloorName(CurrentFloor);

        if (_floorUpButton != null)
            _floorUpButton.Disabled = _venue != null && !_venue.HasFloor(CurrentFloor + 1);

        if (_floorDownButton != null)
            _floorDownButton.Disabled = _venue != null && !_venue.HasFloor(CurrentFloor - 1);
    }

    private void ClampFloorToBuilding()
    {
        if (_venue == null || _venue.FloorCount == 0) return;
        if (_venue.HasFloor(CurrentFloor)) return;

        CurrentFloor = Mathf.Clamp(CurrentFloor, _venue.LowestFloor, _venue.HighestFloor);
    }

    // ── Naming ─────────────────────────────────────────────────────────

    /// <summary>Human name for a floor index, matching the building's semantics.</summary>
    public static string FloorName(int floor) => VenueBuilding.GetFloorKind(floor) switch
    {
        FloorKind.Basement => floor == -1 ? "Basement" : $"Sub {-floor}",
        FloorKind.Ground => "Ground",
        _ => $"Floor {floor}"
    };

    private static Color PhaseColour(NightPhase phase) => phase switch
    {
        NightPhase.Service => IsoTheme.Good,
        NightPhase.Closing => IsoTheme.Warning,
        NightPhase.Ledger => IsoTheme.Gold,
        NightPhase.Preparation => IsoTheme.TextPrimary,
        _ => IsoTheme.TextMuted
    };

    private static string ToolLabel(HudTool tool) => tool switch
    {
        HudTool.Cleaning => "Cleaning",
        HudTool.Alert => "Alert",
        _ => "Info"
    };

    private static string ToolGlyph(HudTool tool) => tool switch
    {
        HudTool.Cleaning => "✦",
        HudTool.Alert => "!",
        _ => "?"
    };

    private static Color ToolAccent(HudTool tool) => tool switch
    {
        HudTool.Cleaning => IsoTheme.Good,
        HudTool.Alert => IsoTheme.Danger,
        _ => IsoTheme.Gold
    };

    public override string ToString() =>
        $"[GameHud] {_lastPhase} — floor {CurrentFloor} ({FloorName(CurrentFloor)}), " +
        $"{SpeedScale:0}x{(IsPaused ? " paused" : "")}";
}
