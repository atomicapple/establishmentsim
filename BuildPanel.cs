using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>Which page of the Build/Decorate panel is showing.</summary>
public enum BuildPanelTab
{
    /// <summary>Buildable room types with their construction costs.</summary>
    Rooms,

    /// <summary>The roster, for posting people to rooms.</summary>
    Staff,

    /// <summary>Structural purchases — new floors, for now.</summary>
    Upgrades
}

// ── BuildPanel ─────────────────────────────────────────────────────────

/// <summary>
/// The right-hand Build / Decorate panel: three tabs over a scrolling list.
///
/// The panel is a catalogue, not a builder. It reads
/// <see cref="VenueBuilding.RoomCosts"/> for what exists and
/// <c>GameStateManager.Cash</c> for what is affordable, and it emits a signal
/// when the player picks something — placement is the integrator's job,
/// because only the view knows which tile the cursor is over.
///
/// Rows for unaffordable entries are disabled and their price is tinted with
/// <see cref="IsoTheme.Danger"/>, so the reason a thing cannot be bought is
/// visible without a tooltip.
///
/// Built entirely in code; safe to construct with no <see cref="VenueBuilding"/>
/// bound, in which case it shows the catalogue and nothing floor-specific.
/// </summary>
public partial class BuildPanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>A room type was picked. Carries the <see cref="RoomType"/> value.</summary>
    [Signal]
    public delegate void OnBuildRoomRequestedEventHandler(int roomType);

    /// <summary>A floor purchase was picked. Carries the floor index.</summary>
    [Signal]
    public delegate void OnBuyFloorRequestedEventHandler(int floor);

    /// <summary>A staff member was picked. Carries their id.</summary>
    [Signal]
    public delegate void OnStaffSelectedEventHandler(string staffId);

    /// <summary>The visible tab changed. Carries the <see cref="BuildPanelTab"/> value.</summary>
    [Signal]
    public delegate void OnTabChangedEventHandler(int tab);

    // ── Catalogue ──────────────────────────────────────────────────────

    /// <summary>
    /// Build order for the Rooms tab. Explicit rather than enumerated from
    /// the cost table so the list reads as a shopping order — earners first,
    /// then the public floor, then infrastructure — and does not reshuffle
    /// when a room type is added to the enum.
    /// </summary>
    private static readonly RoomType[] Catalogue =
    {
        RoomType.PrivateSuite,
        RoomType.VIPSuite,
        RoomType.Lounge,
        RoomType.Bar,
        RoomType.VIPEntrance,
        RoomType.Security,
        RoomType.Soundproofing,
        RoomType.Medical,
        RoomType.Service,
        RoomType.Storage
    };

    // ── State ──────────────────────────────────────────────────────────

    private VenueBuilding _venue;
    private BuildPanelTab _tab = BuildPanelTab.Rooms;
    private bool _buildingAllowed = true;

    private readonly Dictionary<RoomType, Button> _roomRows = new();
    private readonly Dictionary<RoomType, Label> _roomPrices = new();
    private readonly List<Button> _floorRows = new();

    private VBoxContainer _list;
    private Label _footer;
    private readonly Button[] _tabButtons = new Button[3];

    /// <summary>The tab currently showing.</summary>
    public BuildPanelTab CurrentTab => _tab;

    /// <summary>Whether room construction is currently offered at all.</summary>
    public bool BuildingAllowed => _buildingAllowed;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUi();
        ShowTab(_tab);
    }

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Point the panel at a building. Null is fine — the catalogue still
    /// shows, only the floor-dependent Upgrades page goes empty.
    /// </summary>
    public void Bind(VenueBuilding venue)
    {
        _venue = venue;

        if (_list == null) BuildUi();
        ShowTab(_tab);
    }

    /// <summary>
    /// Gate construction. The night loop only allows building between nights,
    /// and greying the whole list is a clearer answer than a rejection
    /// message after the click.
    /// </summary>
    public void SetBuildingAllowed(bool allowed)
    {
        if (allowed == _buildingAllowed) return;

        _buildingAllowed = allowed;
        Refresh();
    }

    /// <summary>Switch pages.</summary>
    public void ShowTab(BuildPanelTab tab)
    {
        _tab = tab;

        for (int i = 0; i < _tabButtons.Length; i++)
            if (_tabButtons[i] != null)
                _tabButtons[i].ButtonPressed = i == (int)tab;

        Rebuild();
        EmitSignal(SignalName.OnTabChanged, (int)tab);
    }

    /// <summary>
    /// Re-evaluate affordability without rebuilding the list. Cheap enough to
    /// call on every cash change.
    /// </summary>
    public void Refresh()
    {
        var cash = GameStateManager.Instance?.Cash ?? double.MaxValue;

        foreach (var (type, row) in _roomRows)
        {
            var cost = VenueBuilding.GetRoomCost(type).Cash;
            var affordable = cash >= cost;

            row.Disabled = !affordable || !_buildingAllowed;

            if (_roomPrices.TryGetValue(type, out var price))
                price.AddThemeColorOverride("font_color",
                    affordable ? IsoTheme.Gold : IsoTheme.Danger);
        }

        foreach (var row in _floorRows)
        {
            if (row == null || !IsInstanceValid(row)) continue;

            var cost = row.GetMeta("cost", 0.0).AsDouble();
            row.Disabled = cash < cost || !_buildingAllowed;
        }

        RefreshFooter();
    }

    // ── Construction ───────────────────────────────────────────────────

    private void BuildUi()
    {
        if (_list != null) return;

        MouseFilter = MouseFilterEnum.Stop;

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(14, 10));
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(frame);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        frame.AddChild(column);

        var title = HudStyle.MakeLabel("BUILD / DECORATE", 13, IsoTheme.Gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        column.AddChild(title);

        // ── Tabs ───────────────────────────────────────────────────────
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        column.AddChild(tabs);

        foreach (BuildPanelTab tab in Enum.GetValues<BuildPanelTab>())
        {
            var captured = tab;
            var button = new Button
            {
                Text = tab.ToString(),
                ToggleMode = true,
                ButtonPressed = tab == _tab,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28)
            };

            HudStyle.StyleButton(button, IsoTheme.Gold, 8, 12, 4);
            button.Pressed += () => ShowTab(captured);

            _tabButtons[(int)tab] = button;
            tabs.AddChild(button);
        }

        // ── Scrolling body ─────────────────────────────────────────────
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        column.AddChild(scroll);

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);

        _footer = HudStyle.MakeLabel("", 10, IsoTheme.TextMuted);
        _footer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_footer);
    }

    private void Rebuild()
    {
        if (_list == null) return;

        _roomRows.Clear();
        _roomPrices.Clear();
        _floorRows.Clear();

        // Detach before freeing: QueueFree does not take effect until the end
        // of the frame, and the new rows would otherwise append below stale ones.
        foreach (var child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }

        switch (_tab)
        {
            case BuildPanelTab.Rooms: BuildRoomsTab(); break;
            case BuildPanelTab.Staff: BuildStaffTab(); break;
            default: BuildUpgradesTab(); break;
        }

        Refresh();
    }

    private void BuildRoomsTab()
    {
        foreach (var type in Catalogue)
        {
            var blueprint = VenueBuilding.GetRoomCost(type);

            var row = MakeRow(
                blueprint.DisplayName,
                $"${blueprint.Cash:N0}",
                $"{blueprint.Size.X}×{blueprint.Size.Y} · lux {blueprint.Luxury:0} · " +
                $"disc {blueprint.Discretion:0}",
                IsoTheme.GetRoomColor(type),
                out var priceLabel);

            var captured = type;
            row.Pressed += () => EmitSignal(SignalName.OnBuildRoomRequested, (int)captured);

            _roomRows[type] = row;
            _roomPrices[type] = priceLabel;
            _list.AddChild(row);
        }
    }

    private void BuildStaffTab()
    {
        var roster = StaffRoster.Instance;
        var staff = roster?.GetAll().ToList() ?? new List<StaffMember>();

        if (staff.Count == 0)
        {
            _list.AddChild(HudStyle.MakeLabel("Nobody on the roster.", 12, IsoTheme.TextMuted));
            return;
        }

        foreach (var member in staff)
        {
            if (member == null) continue;

            var row = MakeRow(
                member.StaffName,
                $"{member.Satisfaction:0}",
                $"stress {member.Stress:0} · loyalty {member.Loyalty:0}",
                IsoTheme.GetScoreColor(member.Satisfaction),
                out var scoreLabel);

            scoreLabel.AddThemeColorOverride("font_color",
                IsoTheme.GetScoreColor(100f - member.Stress));

            var id = member.Id;
            row.Pressed += () => EmitSignal(SignalName.OnStaffSelected, id);

            _list.AddChild(row);
        }
    }

    private void BuildUpgradesTab()
    {
        if (_venue == null)
        {
            _list.AddChild(HudStyle.MakeLabel("No building bound.", 12, IsoTheme.TextMuted));
            return;
        }

        AddFloorRow(_venue.HighestFloor + 1, "Build up");
        AddFloorRow(_venue.LowestFloor - 1, "Excavate");

        if (_floorRows.Count == 0)
            _list.AddChild(HudStyle.MakeLabel(
                "The building is at its full extent.", 12, IsoTheme.TextMuted));
    }

    private void AddFloorRow(int floor, string verb)
    {
        if (floor < VenueBuilding.MinPossibleFloor || floor > VenueBuilding.MaxPossibleFloor) return;
        if (_venue.HasFloor(floor)) return;

        var cost = _venue.GetFloorCost(floor);

        var row = MakeRow(
            GameHud.FloorName(floor),
            $"${cost:N0}",
            $"{verb} — {VenueBuilding.GetFloorKind(floor)}",
            IsoTheme.GoldDim,
            out _);

        row.SetMeta("cost", cost);
        row.Pressed += () => EmitSignal(SignalName.OnBuyFloorRequested, floor);

        _floorRows.Add(row);
        _list.AddChild(row);
    }

    /// <summary>
    /// One catalogue row: a swatch, a name, a subtitle, and a right-aligned
    /// value. Built as a Button so disabled state and hover come free.
    /// </summary>
    private static Button MakeRow(
        string title, string value, string subtitle, Color swatch, out Label valueLabel)
    {
        var row = new Button { CustomMinimumSize = new Vector2(0, 46) };
        HudStyle.StyleButton(row, swatch, 8, 13, 8);

        var layout = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        layout.AddThemeConstantOverride("separation", 8);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layout.OffsetLeft = 8;
        layout.OffsetRight = -8;
        row.AddChild(layout);

        var chip = new ColorRect
        {
            Color = swatch,
            CustomMinimumSize = new Vector2(5, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        layout.AddChild(chip);

        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeConstantOverride("separation", 0);
        layout.AddChild(text);

        var titleLabel = HudStyle.MakeLabel(title, 13, IsoTheme.TextPrimary);
        titleLabel.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(titleLabel);

        var subtitleLabel = HudStyle.MakeLabel(subtitle, 9, IsoTheme.TextMuted);
        subtitleLabel.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(subtitleLabel);

        valueLabel = HudStyle.MakeLabel(value, 14, IsoTheme.Gold);
        valueLabel.MouseFilter = MouseFilterEnum.Ignore;
        valueLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        layout.AddChild(valueLabel);

        return row;
    }

    private void RefreshFooter()
    {
        if (_footer == null) return;

        if (!_buildingAllowed)
        {
            _footer.Text = "Building is closed while the doors are open.";
            _footer.AddThemeColorOverride("font_color", IsoTheme.Warning);
            return;
        }

        _footer.AddThemeColorOverride("font_color", IsoTheme.TextMuted);

        _footer.Text = _venue == null
            ? "No building bound."
            : $"{_venue.Rooms.Count} rooms · {_venue.FloorCount} floors · " +
              $"upkeep ${_venue.GetNightlyUpkeep():N0}/night";
    }

    public override string ToString() =>
        $"[BuildPanel] {_tab} — {_roomRows.Count} room rows, " +
        $"{(_buildingAllowed ? "open" : "locked")}";
}
