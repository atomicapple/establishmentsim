using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── DecoratePanel ──────────────────────────────────────────────────────

/// <summary>
/// The furnishing panel: a diagnosis of one room's Appointment score, the
/// shop that can fix it, and the pieces already standing in it.
///
/// The panel exists to make style coherence visible. Appointment is
/// <c>(Coverage·0.4 + Quality·0.4 + Coherence·0.2) · Condition</c>, and
/// Coherence is the only component that is <i>free</i> — five cheap matching
/// pieces beat five expensive clashing ones. A player who cannot see that
/// will never find it, so every surface here is built to say it out loud:
/// the coherence bar, the plain-language verdict beneath it, the
/// style-matched badge on shop rows, and the value-per-dollar ordering that
/// floats matching pieces to the top on its own.
///
/// The panel owns its transactions — it calls
/// <see cref="FurnitureCatalog.Purchase"/> and <see cref="FurnitureCatalog.Sell"/>
/// directly and then emits <see cref="SignalName.OnRoomChanged"/> so the
/// integrator can repaint the dollhouse. It never touches the venue itself.
///
/// Built entirely in code. Safe with no catalog, no venue, no selected room,
/// an empty room, or a room with no space left.
/// </summary>
public partial class DecoratePanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>
    /// A room's furniture changed — bought or sold. Carries the tile so the
    /// view can repaint just that room.
    /// </summary>
    [Signal]
    public delegate void OnRoomChangedEventHandler(int x, int y, int floor);

    /// <summary>The player dismissed the panel.</summary>
    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    // ── Tuning ─────────────────────────────────────────────────────────

    /// <summary>
    /// Most shop rows shown at once. The unfiltered catalogue is six styles ×
    /// nine categories × three tiers, which is more buttons than anyone reads
    /// and more than is pleasant to rebuild on every purchase. The list is
    /// value-sorted, so the truncated tail is the part nobody wanted.
    /// </summary>
    public const int MaxShopRows = 48;

    /// <summary>Offers shown when the Recommended toggle is on.</summary>
    public const int RecommendationCount = 8;

    /// <summary>Share of a style needed for the coherence bonus. Mirrors the calculator.</summary>
    private const float CoherenceTarget = RoomAppointmentCalculator.CoherenceThreshold;

    // ── State ──────────────────────────────────────────────────────────

    private FurnitureCatalog _catalog;
    private VenueBuilding _venue;

    private Vector3I? _tile;
    private FurnitureStyle? _styleFilter;
    private FurnitureCategory? _categoryFilter;
    private bool _recommendedOnly;

    // ── Widgets ────────────────────────────────────────────────────────

    private Label _title;
    private Label _subtitle;
    private Label _appointment;

    private readonly Dictionary<string, ProgressBar> _bars = new();
    private readonly Dictionary<string, Label> _barValues = new();

    private Label _coherenceVerdict;
    private Label _missingLine;
    private Label _essentialLine;
    private Label _slotsLine;

    private OptionButton _styleDrop;
    private OptionButton _categoryDrop;
    private Button _recommendButton;

    private VBoxContainer _shopList;
    private VBoxContainer _placedList;
    private Label _shopHeader;
    private Label _placedHeader;
    private Label _footer;

    private readonly List<FurnitureStyle> _styleOrder = new();
    private readonly List<FurnitureCategory> _categoryOrder = new();

    // ── Public read-only state ─────────────────────────────────────────

    /// <summary>The tile whose room is being furnished, or null if none.</summary>
    public Vector3I? SelectedTile => _tile;

    /// <summary>Whether the shop list is showing recommendations only.</summary>
    public bool RecommendedOnly => _recommendedOnly;

    /// <summary>The room currently under the panel, or null.</summary>
    public RoomModule CurrentRoom =>
        _tile.HasValue ? _venue?.GetRoom(_tile.Value) : null;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        BuildUi();
        Refresh();
    }

    // ── Binding ────────────────────────────────────────────────────────

    /// <summary>
    /// Point the panel at a shop and a building. Either may be null — the
    /// panel then explains that it has nothing to sell rather than throwing.
    /// </summary>
    public void Bind(FurnitureCatalog catalog, VenueBuilding venue)
    {
        _catalog = catalog;
        _venue = venue;

        if (_shopList == null) BuildUi();
        Refresh();
    }

    /// <summary>Select the room covering <paramref name="tile"/>.</summary>
    public void ShowRoom(Vector3I tile)
    {
        _tile = tile;

        if (_shopList == null) BuildUi();
        Refresh();
    }

    /// <summary>Deselect. The panel stays up but shows no room.</summary>
    public void Clear()
    {
        _tile = null;

        if (_shopList == null) BuildUi();
        Refresh();
    }

    /// <summary>Re-read the room, the shop and the player's cash from scratch.</summary>
    public void Refresh()
    {
        if (_shopList == null) return;

        var room = CurrentRoom;

        RefreshHeader(room);
        RefreshReadout(room);
        RefreshShop(room);
        RefreshPlaced(room);
        RefreshFooter(room);
    }

    // ── Construction ───────────────────────────────────────────────────

    private void BuildUi()
    {
        if (_shopList != null) return;

        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(320, 0);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel(14, 10));
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(frame);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        frame.AddChild(column);

        BuildHeader(column);
        BuildReadout(column);
        BuildFilters(column);
        BuildBody(column);

        _footer = HudStyle.MakeLabel("", 10, IsoTheme.TextMuted);
        _footer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_footer);
    }

    private void BuildHeader(VBoxContainer column)
    {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 6);
        column.AddChild(bar);

        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 0);
        bar.AddChild(text);

        _title = HudStyle.MakeLabel("DECORATE", 13, IsoTheme.Gold);
        text.AddChild(_title);

        _subtitle = HudStyle.MakeLabel("No room selected.", 9, IsoTheme.TextMuted);
        _subtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddChild(_subtitle);

        _appointment = HudStyle.MakeLabel("—", 20, IsoTheme.TextMuted);
        _appointment.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        bar.AddChild(_appointment);

        var close = new Button
        {
            Text = "✕",
            CustomMinimumSize = new Vector2(28, 28),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        HudStyle.StyleButton(close, IsoTheme.Danger, 8, 12, 4);
        close.Pressed += () => EmitSignal(SignalName.OnCloseRequested);
        bar.AddChild(close);
    }

    /// <summary>
    /// The teaching block: four component bars, then the sentences that say
    /// what those bars mean in words the player can act on.
    /// </summary>
    private void BuildReadout(VBoxContainer column)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim.Darkened(0.4f), 8, 1, 8));
        column.AddChild(panel);

        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 3);
        panel.AddChild(inner);

        AddBarRow(inner, "Coverage");
        AddBarRow(inner, "Quality");
        AddBarRow(inner, "Coherence");
        AddBarRow(inner, "Condition");

        _coherenceVerdict = HudStyle.MakeLabel("", 10, IsoTheme.TextPrimary);
        _coherenceVerdict.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        inner.AddChild(_coherenceVerdict);

        _essentialLine = HudStyle.MakeLabel("", 10, IsoTheme.Danger);
        _essentialLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        inner.AddChild(_essentialLine);

        _missingLine = HudStyle.MakeLabel("", 10, IsoTheme.Warning);
        _missingLine.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        inner.AddChild(_missingLine);

        _slotsLine = HudStyle.MakeLabel("", 10, IsoTheme.TextMuted);
        inner.AddChild(_slotsLine);
    }

    private void AddBarRow(VBoxContainer parent, string label)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        parent.AddChild(row);

        var name = HudStyle.MakeLabel(label, 10, IsoTheme.TextMuted);
        name.CustomMinimumSize = new Vector2(62, 0);
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(name);

        var bar = MakeBar(9);
        row.AddChild(bar);

        var value = HudStyle.MakeLabel("—", 10, IsoTheme.TextMuted);
        value.CustomMinimumSize = new Vector2(38, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(value);

        _bars[label] = bar;
        _barValues[label] = value;
    }

    private void BuildFilters(VBoxContainer column)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        column.AddChild(row);

        _styleDrop = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 26)
        };
        HudStyle.StyleButton(_styleDrop, IsoTheme.Gold, 6, 10, 6);
        _styleDrop.AddItem("All styles");
        _styleOrder.Clear();

        foreach (var style in Enum.GetValues<FurnitureStyle>())
        {
            _styleOrder.Add(style);
            _styleDrop.AddItem(style.ToString());
            _styleDrop.SetItemIcon(_styleDrop.ItemCount - 1, MakeSwatch(IsoTheme.GetStyleColor(style)));
        }

        _styleDrop.Selected = 0;
        _styleDrop.ItemSelected += OnStyleFilterChanged;
        row.AddChild(_styleDrop);

        _categoryDrop = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 26)
        };
        HudStyle.StyleButton(_categoryDrop, IsoTheme.Gold, 6, 10, 6);
        _categoryDrop.AddItem("All kinds");
        _categoryOrder.Clear();

        foreach (var category in Enum.GetValues<FurnitureCategory>())
        {
            _categoryOrder.Add(category);
            _categoryDrop.AddItem(category.ToString());
        }

        _categoryDrop.Selected = 0;
        _categoryDrop.ItemSelected += OnCategoryFilterChanged;
        row.AddChild(_categoryDrop);

        _recommendButton = new Button
        {
            Text = "★",
            ToggleMode = true,
            TooltipText = "Show only the pieces that improve this room most per dollar.",
            CustomMinimumSize = new Vector2(30, 26)
        };
        HudStyle.StyleButton(_recommendButton, IsoTheme.Good, 6, 12, 4);
        _recommendButton.Toggled += OnRecommendToggled;
        row.AddChild(_recommendButton);
    }

    private void BuildBody(VBoxContainer column)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        column.AddChild(scroll);

        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(body);

        _shopHeader = HudStyle.MakeLabel("SHOP", 10, IsoTheme.GoldDim);
        body.AddChild(_shopHeader);

        _shopList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _shopList.AddThemeConstantOverride("separation", 4);
        body.AddChild(_shopList);

        _placedHeader = HudStyle.MakeLabel("IN THIS ROOM", 10, IsoTheme.GoldDim);
        body.AddChild(_placedHeader);

        _placedList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _placedList.AddThemeConstantOverride("separation", 4);
        body.AddChild(_placedList);
    }

    // ── Filter handlers ────────────────────────────────────────────────

    private void OnStyleFilterChanged(long index)
    {
        _styleFilter = index >= 1 && index <= _styleOrder.Count
            ? _styleOrder[(int)index - 1]
            : null;

        Refresh();
    }

    private void OnCategoryFilterChanged(long index)
    {
        _categoryFilter = index >= 1 && index <= _categoryOrder.Count
            ? _categoryOrder[(int)index - 1]
            : null;

        Refresh();
    }

    private void OnRecommendToggled(bool on)
    {
        _recommendedOnly = on;
        Refresh();
    }

    // ── Header ─────────────────────────────────────────────────────────

    private void RefreshHeader(RoomModule room)
    {
        if (room == null)
        {
            _title.Text = "DECORATE";
            _subtitle.Text = _venue == null
                ? "No building bound."
                : "Pick a room to furnish.";
            _appointment.Text = "—";
            _appointment.AddThemeColorOverride("font_color", IsoTheme.TextMuted);
            return;
        }

        var breakdown = RoomAppointmentCalculator.GetBreakdown(room);

        _title.Text = room.RoomName.ToUpperInvariant();
        _subtitle.Text = $"{room.Type} · {room.Size.X}×{room.Size.Y} · " +
                         $"{breakdown.ItemCount} piece{(breakdown.ItemCount == 1 ? "" : "s")}";

        _appointment.Text = $"{breakdown.Appointment:F0}";
        _appointment.AddThemeColorOverride("font_color",
            IsoTheme.GetScoreColor(breakdown.Appointment));
    }

    // ── Readout ────────────────────────────────────────────────────────

    private void RefreshReadout(RoomModule room)
    {
        if (room == null)
        {
            SetBar("Coverage", 0f, "—");
            SetBar("Quality", 0f, "—");
            SetBar("Coherence", 0f, "—");
            SetBar("Condition", 0f, "—");

            _coherenceVerdict.Text = "Select a room to see how well it reads.";
            _coherenceVerdict.AddThemeColorOverride("font_color", IsoTheme.TextMuted);
            _essentialLine.Text = "";
            _missingLine.Text = "";
            _slotsLine.Text = "";
            return;
        }

        var b = RoomAppointmentCalculator.GetBreakdown(room);

        SetBar("Coverage", b.Coverage, $"{b.Coverage:F0}");
        SetBar("Quality", b.Quality, $"{b.Quality:F0}");
        SetBar("Coherence", b.Coherence, $"{b.Coherence:F0}");

        // The condition multiplier never falls below 0.45, so a raw ×100 would
        // paint a room of scrap as merely mediocre. Rescale the live band onto
        // 0–100 for the colour, and print the honest multiplier beside it.
        float conditionBand = Mathf.Clamp(
            (b.ConditionMultiplier - RoomAppointmentCalculator.MinConditionMultiplier) /
            (1f - RoomAppointmentCalculator.MinConditionMultiplier) * 100f, 0f, 100f);

        SetBar("Condition", conditionBand, $"×{b.ConditionMultiplier:F2}");

        RefreshCoherenceVerdict(room, b);
        RefreshGaps(room, b);

        _slotsLine.Text =
            $"Slots: {room.FreeFurnitureArea} free of {room.FurnitureCapacity}";
        _slotsLine.AddThemeColorOverride("font_color",
            room.FreeFurnitureArea == 0 ? IsoTheme.Warning : IsoTheme.TextMuted);
    }

    /// <summary>
    /// The sentence the whole panel exists for. Counts are taken from the
    /// room directly rather than from the effective share, because "4 of 5"
    /// is a fact the player can check by looking at the list below, while the
    /// effective share is half-credit arithmetic they cannot.
    /// </summary>
    private void RefreshCoherenceVerdict(RoomModule room, AppointmentBreakdown b)
    {
        if (b.ItemCount == 0)
        {
            _coherenceVerdict.Text =
                "Empty. Pick one style and stay with it — matching pieces are " +
                "worth more than expensive ones.";
            _coherenceVerdict.AddThemeColorOverride("font_color", IsoTheme.TextMuted);
            return;
        }

        int dominantCount = room.Furniture.Count(f => f != null && f.StyleTag == b.DominantStyle);
        int total = b.ItemCount;

        string text;
        Color colour;

        if (b.DistinctStyleCount == 1)
        {
            text = $"All {b.DominantStyle} ({total} of {total}). Perfectly coherent — " +
                   "keep buying this style.";
            colour = IsoTheme.Good;
        }
        else if (b.DominantStyleShare >= CoherenceTarget)
        {
            text = $"Mostly {b.DominantStyle} ({dominantCount} of {total}). " +
                   $"Coherent — add more {b.DominantStyle} to push it further.";
            colour = IsoTheme.Good;
        }
        else if (b.DistinctStyleCount >= 3)
        {
            text = $"{b.DistinctStyleCount} clashing styles — this room reads as a " +
                   $"junk shop. Commit to {b.DominantStyle} and sell the rest.";
            colour = IsoTheme.Danger;
        }
        else
        {
            int needed = Mathf.Max(1,
                Mathf.CeilToInt((CoherenceTarget * total - dominantCount) / (1f - CoherenceTarget)));

            text = $"Split styles — {b.DominantStyle} leads at {b.DominantStyleShare:P0}, " +
                   $"short of the 70% bonus. About {needed} more {b.DominantStyle} " +
                   "piece" + (needed == 1 ? "" : "s") + " would do it.";
            colour = IsoTheme.Warning;
        }

        _coherenceVerdict.Text = text;
        _coherenceVerdict.AddThemeColorOverride("font_color", colour);
    }

    private void RefreshGaps(RoomModule room, AppointmentBreakdown b)
    {
        var essential = RoomAppointmentCalculator.GetEssentialCategory(room.Type);

        if (b.MissingEssential && essential.HasValue)
        {
            _essentialLine.Text =
                $"NO {essential.Value.ToString().ToUpperInvariant()} — a {room.Type} " +
                "without one barely counts as a room. Coverage is gutted until it has one.";
            _essentialLine.Visible = true;
        }
        else
        {
            _essentialLine.Text = "";
            _essentialLine.Visible = false;
        }

        var missing = b.MissingCategories?
            .Where(c => !(essential.HasValue && b.MissingEssential && c == essential.Value))
            .ToList() ?? new List<FurnitureCategory>();

        if (missing.Count > 0)
        {
            _missingLine.Text = "Still missing: " + string.Join(", ", missing);
            _missingLine.Visible = true;
        }
        else if (!b.MissingEssential)
        {
            _missingLine.Text = "Every required kind of furniture is present.";
            _missingLine.AddThemeColorOverride("font_color", IsoTheme.TextMuted);
            _missingLine.Visible = true;
            return;
        }
        else
        {
            _missingLine.Text = "";
            _missingLine.Visible = false;
        }

        _missingLine.AddThemeColorOverride("font_color", IsoTheme.Warning);
    }

    // ── Shop ───────────────────────────────────────────────────────────

    private void RefreshShop(RoomModule room)
    {
        ClearChildren(_shopList);

        if (_catalog == null)
        {
            _shopHeader.Text = "SHOP";
            _shopList.AddChild(HudStyle.MakeLabel("No catalogue bound.", 11, IsoTheme.TextMuted));
            return;
        }

        if (room == null)
        {
            _shopHeader.Text = "SHOP";
            _shopList.AddChild(HudStyle.MakeLabel(
                "Select a room and the shop will price every piece against it.",
                11, IsoTheme.TextMuted));
            return;
        }

        var offers = GatherOffers(room);

        _shopHeader.Text = _recommendedOnly
            ? $"RECOMMENDED — {offers.Count}"
            : $"SHOP — {offers.Count} SHOWN";

        if (offers.Count == 0)
        {
            _shopList.AddChild(HudStyle.MakeLabel(
                _recommendedOnly
                    ? "Nothing here would improve this room. Try selling a clashing piece."
                    : "Nothing matches those filters.",
                11, IsoTheme.TextMuted));
            return;
        }

        var cash = GameStateManager.Instance?.Cash ?? double.MaxValue;

        foreach (var offer in offers)
            _shopList.AddChild(MakeOfferRow(room, offer, cash));
    }

    /// <summary>
    /// Offers for the room, sorted the way the lesson wants them read:
    /// required gaps first, then best Appointment per dollar. Because
    /// coherence is free, a matching tier-1 piece routinely outranks a
    /// clashing tier-3 one without the list having to say so.
    /// </summary>
    private List<CatalogOffer> GatherOffers(RoomModule room)
    {
        if (_recommendedOnly)
        {
            var recommended = _catalog.GetRecommendations(room, RecommendationCount)
                              ?? new List<CatalogOffer>();

            return recommended
                .Where(o => o?.Item != null)
                .Where(MatchesFilters)
                .ToList();
        }

        var offers = _catalog.GetOffers(room, _styleFilter, _categoryFilter)
                     ?? new List<CatalogOffer>();

        return offers
            .Where(o => o?.Item != null)
            .OrderByDescending(o => o.FillsRequiredGap)
            .ThenByDescending(o => o.ValuePerCost)
            .ThenByDescending(o => o.AppointmentDelta)
            .Take(MaxShopRows)
            .ToList();
    }

    private bool MatchesFilters(CatalogOffer offer)
    {
        if (offer?.Item == null) return false;
        if (_styleFilter.HasValue && offer.Item.StyleTag != _styleFilter.Value) return false;
        if (_categoryFilter.HasValue && offer.Item.Category != _categoryFilter.Value) return false;
        return true;
    }

    private Button MakeOfferRow(RoomModule room, CatalogOffer offer, double cash)
    {
        var item = offer.Item;
        var accent = IsoTheme.GetStyleColor(item.StyleTag);

        bool affordable = cash >= offer.Price;
        bool fits = room.FreeFurnitureArea >= item.Area;
        bool buyable = affordable && fits;

        var row = new Button
        {
            CustomMinimumSize = new Vector2(0, 52),
            Disabled = !buyable,
            TooltipText = buyable
                ? $"{item.ItemName} — comfort {item.Comfort:F0}, prestige {item.Prestige:F0}, " +
                  $"upkeep ${item.Upkeep:F2}/night"
                : affordable
                    ? $"No room: needs {item.Area} slot(s), {room.FreeFurnitureArea} free."
                    : $"Costs ${offer.Price:F0}; you have ${cash:F0}."
        };
        HudStyle.StyleButton(row, accent, 8, 12, 6);

        var layout = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        layout.AddThemeConstantOverride("separation", 8);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layout.OffsetLeft = 8;
        layout.OffsetRight = -8;
        row.AddChild(layout);

        layout.AddChild(new ColorRect
        {
            Color = buyable ? accent : accent.Darkened(0.5f),
            CustomMinimumSize = new Vector2(5, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        });

        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeConstantOverride("separation", 1);
        layout.AddChild(text);

        var name = HudStyle.MakeLabel(item.ItemName, 12,
            buyable ? IsoTheme.TextPrimary : IsoTheme.TextMuted);
        name.MouseFilter = MouseFilterEnum.Ignore;
        name.ClipText = true;
        text.AddChild(name);

        var meta = HudStyle.MakeLabel(
            $"{item.Category} · T{item.Tier} · {item.StyleTag}", 9, IsoTheme.TextMuted);
        meta.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(meta);

        // The two badges are the lesson in miniature: one says "this room is
        // legally incomplete without this kind of thing", the other says
        // "this one agrees with what is already here".
        if (offer.FillsRequiredGap || offer.MatchesRoomStyle)
        {
            var badges = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            badges.AddThemeConstantOverride("separation", 4);
            text.AddChild(badges);

            if (offer.FillsRequiredGap)
                badges.AddChild(MakeBadge("NEEDED", IsoTheme.Warning));

            if (offer.MatchesRoomStyle)
                badges.AddChild(MakeBadge($"MATCHES {item.StyleTag}".ToUpperInvariant(),
                    IsoTheme.Good));
        }

        var right = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        right.AddThemeConstantOverride("separation", 1);
        layout.AddChild(right);

        var price = HudStyle.MakeLabel($"${offer.Price:N0}", 13,
            affordable ? IsoTheme.Gold : IsoTheme.Danger);
        price.HorizontalAlignment = HorizontalAlignment.Right;
        price.MouseFilter = MouseFilterEnum.Ignore;
        right.AddChild(price);

        var delta = HudStyle.MakeLabel(
            $"{offer.AppointmentDelta:+0.0;-0.0;0.0} appt", 10,
            offer.AppointmentDelta > 0.05f ? IsoTheme.Good
                : offer.AppointmentDelta < -0.05f ? IsoTheme.Danger
                : IsoTheme.TextMuted);
        delta.HorizontalAlignment = HorizontalAlignment.Right;
        delta.MouseFilter = MouseFilterEnum.Ignore;
        right.AddChild(delta);

        if (!fits)
        {
            var note = HudStyle.MakeLabel("no space", 9, IsoTheme.Danger);
            note.HorizontalAlignment = HorizontalAlignment.Right;
            note.MouseFilter = MouseFilterEnum.Ignore;
            right.AddChild(note);
        }

        if (buyable)
        {
            var captured = offer;
            row.Pressed += () => Buy(captured);
        }

        return row;
    }

    private void Buy(CatalogOffer offer)
    {
        if (_catalog == null || _venue == null || !_tile.HasValue || offer?.Item == null) return;

        var tile = _tile.Value;
        if (!_catalog.Purchase(_venue, tile, offer))
        {
            // The catalogue emits its own rejection signal; refresh anyway so
            // affordability tinting reflects whatever changed underneath us.
            Refresh();
            return;
        }

        Refresh();
        EmitSignal(SignalName.OnRoomChanged, tile.X, tile.Y, tile.Z);
    }

    // ── Placed furniture ───────────────────────────────────────────────

    private void RefreshPlaced(RoomModule room)
    {
        ClearChildren(_placedList);

        if (room == null)
        {
            _placedHeader.Text = "IN THIS ROOM";
            return;
        }

        var furniture = room.Furniture;
        _placedHeader.Text = $"IN THIS ROOM — {furniture.Count}";

        if (furniture.Count == 0)
        {
            _placedList.AddChild(HudStyle.MakeLabel(
                "Nothing placed yet.", 11, IsoTheme.TextMuted));
            return;
        }

        for (int i = 0; i < furniture.Count; i++)
        {
            var item = furniture[i];
            if (item == null) continue;

            _placedList.AddChild(MakePlacedRow(item, i));
        }
    }

    private PanelContainer MakePlacedRow(FurnitureItem item, int index)
    {
        var accent = IsoTheme.GetStyleColor(item.StyleTag);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, accent.Darkened(0.45f), 8, 1, 6));

        var layout = new HBoxContainer();
        layout.AddThemeConstantOverride("separation", 6);
        panel.AddChild(layout);

        layout.AddChild(new ColorRect
        {
            Color = accent,
            CustomMinimumSize = new Vector2(4, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        });

        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        text.AddThemeConstantOverride("separation", 1);
        layout.AddChild(text);

        var name = HudStyle.MakeLabel(item.ItemName, 11,
            item.IsDilapidated ? IsoTheme.Danger : IsoTheme.TextPrimary);
        name.ClipText = true;
        text.AddChild(name);

        var meta = HudStyle.MakeLabel(
            $"{item.StyleTag} · {item.Category}" + (item.IsDilapidated ? " · DILAPIDATED" : ""),
            9, item.IsDilapidated ? IsoTheme.Danger : IsoTheme.TextMuted);
        text.AddChild(meta);

        var conditionRow = new HBoxContainer();
        conditionRow.AddThemeConstantOverride("separation", 4);
        text.AddChild(conditionRow);

        var bar = MakeBar(7);
        SetBarValue(bar, item.Condition);
        conditionRow.AddChild(bar);

        var percent = HudStyle.MakeLabel($"{item.Condition:F0}%", 9,
            IsoTheme.GetScoreColor(item.Condition));
        percent.CustomMinimumSize = new Vector2(30, 0);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        conditionRow.AddChild(percent);

        double refund = _catalog != null
            ? Math.Round(item.PurchasePrice * _catalog.ResaleRate * (item.Condition / 100.0))
            : 0.0;

        var sell = new Button
        {
            Text = $"Sell ${refund:N0}",
            CustomMinimumSize = new Vector2(74, 26),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Disabled = _catalog == null || _venue == null || !_tile.HasValue
        };
        HudStyle.StyleButton(sell, IsoTheme.Danger, 6, 10, 6);

        int captured = index;
        sell.Pressed += () => SellAt(captured);
        layout.AddChild(sell);

        return panel;
    }

    private void SellAt(int index)
    {
        if (_catalog == null || _venue == null || !_tile.HasValue) return;

        var tile = _tile.Value;
        if (!_catalog.Sell(_venue, tile, index))
        {
            Refresh();
            return;
        }

        Refresh();
        EmitSignal(SignalName.OnRoomChanged, tile.X, tile.Y, tile.Z);
    }

    // ── Footer ─────────────────────────────────────────────────────────

    private void RefreshFooter(RoomModule room)
    {
        if (_footer == null) return;

        var cash = GameStateManager.Instance?.Cash;

        if (room == null)
        {
            _footer.Text = cash.HasValue ? $"Cash ${cash.Value:N0}" : "";
            _footer.AddThemeColorOverride("font_color", IsoTheme.TextMuted);
            return;
        }

        var upkeep = room.GetFurnitureUpkeep();

        _footer.Text = (cash.HasValue ? $"Cash ${cash.Value:N0} · " : "") +
                       $"upkeep ${upkeep:N0}/night · " +
                       "coherence is 20% of Appointment and costs nothing.";
        _footer.AddThemeColorOverride("font_color", IsoTheme.TextMuted);
    }

    // ── Widget helpers ─────────────────────────────────────────────────

    private void SetBar(string key, float value, string text)
    {
        if (_bars.TryGetValue(key, out var bar)) SetBarValue(bar, value);

        if (_barValues.TryGetValue(key, out var label))
        {
            label.Text = text;
            label.AddThemeColorOverride("font_color",
                text == "—" ? IsoTheme.TextMuted : IsoTheme.GetScoreColor(value));
        }
    }

    private static void SetBarValue(ProgressBar bar, float value)
    {
        if (bar == null) return;

        var clamped = Mathf.Clamp(value, 0f, 100f);
        bar.Value = clamped;

        var colour = IsoTheme.GetScoreColor(clamped);
        var radius = Mathf.Max(2, (int)bar.CustomMinimumSize.Y / 2);
        bar.AddThemeStyleboxOverride("fill", HudStyle.Box(colour, colour, radius, 0, 0));
    }

    private static ProgressBar MakeBar(int height)
    {
        var bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, height),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };

        var radius = Mathf.Max(2, height / 2);
        var track = IsoTheme.Backdrop;

        bar.AddThemeStyleboxOverride("background", HudStyle.Box(track, track, radius, 0, 0));
        bar.AddThemeStyleboxOverride("fill",
            HudStyle.Box(IsoTheme.GoldDim, IsoTheme.GoldDim, radius, 0, 0));

        return bar;
    }

    private static PanelContainer MakeBadge(string text, Color colour)
    {
        var badge = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel",
            HudStyle.Box(new Color(colour, 0.22f), colour, 4, 1, 4));

        var label = HudStyle.MakeLabel(text, 8, colour);
        label.MouseFilter = MouseFilterEnum.Ignore;
        badge.AddChild(label);

        return badge;
    }

    /// <summary>
    /// A flat colour chip for the style dropdown. Built procedurally because
    /// the project ships without external art.
    /// </summary>
    private static ImageTexture MakeSwatch(Color colour, int size = 10)
    {
        var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(colour);
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// Detach before freeing: <c>QueueFree</c> does not take effect until the
    /// end of the frame, and rebuilt rows would otherwise stack below stale ones.
    /// </summary>
    private static void ClearChildren(Node parent)
    {
        if (parent == null) return;

        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    public override string ToString() =>
        $"[DecoratePanel] {(_tile.HasValue ? _tile.Value.ToString() : "no room")} — " +
        $"{(_recommendedOnly ? "recommended" : "shop")}, " +
        $"style={(_styleFilter.HasValue ? _styleFilter.Value.ToString() : "all")}, " +
        $"category={(_categoryFilter.HasValue ? _categoryFilter.Value.ToString() : "all")}";
}
