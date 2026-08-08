using Godot;

/// <summary>
/// The conversation at the door with a client worth having one with.
///
/// <see cref="ClientNegotiationHandler"/> is 600 lines of tuned haggling
/// maths that has never been reachable — no caller, no UI, and
/// <c>CalculateAcceptProbability</c> weighing charisma, negotiation skill,
/// the room's Appointment against what the client expected, and whether its
/// style matches their taste, all for nobody. This screen is the entry point
/// it never had.
///
/// One modal, three prices, real odds on each. Not a multi-round haggle:
/// this fires three to five times a night and a six-round back-and-forth
/// each time would stop being a decision by the second client. The player
/// picks a number, the odds are stated honestly, and the dice are rolled
/// once.
/// </summary>
public partial class NegotiationScreen : CanvasLayer
{
    /// <summary>A price was agreed. The encounter proceeds at this figure.</summary>
    [Signal]
    public delegate void OnPriceAgreedEventHandler(double price);

    /// <summary>The client was turned away, or walked.</summary>
    [Signal]
    public delegate void OnClientRefusedEventHandler();

    private Control _root;
    private Label _title;
    private Label _subtitle;
    private RichTextLabel _summary;
    private VBoxContainer _options;
    private Label _outcome;
    private Button _dismiss;

    private ClientNegotiationHandler _handler;
    private ClientProfile _client;
    private double _asking;

    public bool IsShowing => _root != null && _root.Visible;

    public override void _Ready()
    {
        Layer = 45;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
    }

    public void Bind(ClientNegotiationHandler handler) => _handler = handler;

    // ── Showing ────────────────────────────────────────────────────────

    public bool Show(ClientProfile client, StaffMember staff, RoomModule room, double asking)
    {
        if (client == null) return false;

        _client = client;
        _asking = asking;

        // Gives the handler its context, so CalculateAcceptProbability is
        // scoring this client against this room and this staff member rather
        // than a default.
        _handler?.StartNegotiation(client, staff, room);

        if (_root == null) BuildSkeleton();
        Populate(client, staff, room, asking);

        Visible = true;
        _root.Visible = true;
        if (GetTree() != null) GetTree().Paused = true;

        return true;
    }

    public new void Hide()
    {
        _handler?.CancelNegotiation();

        if (_root != null) _root.Visible = false;
        Visible = false;
        _client = null;

        if (GetTree() != null) GetTree().Paused = false;
    }

    // ── Skeleton ───────────────────────────────────────────────────────

    private void BuildSkeleton()
    {
        _root = new Control { Name = "NegotiationRoot" };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.ProcessMode = ProcessModeEnum.Always;
        AddChild(_root);

        var scrim = new ColorRect
        {
            Color = new Color(IsoTheme.Backdrop.R, IsoTheme.Backdrop.G, IsoTheme.Backdrop.B, 0.93f)
        };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrim.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.AddChild(scrim);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        _root.AddChild(margin);

        // Centred by spacers rather than a CenterContainer, which sizes a
        // child to its minimum and would pin the panel at its smallest.
        var centre = new HBoxContainer();
        margin.AddChild(centre);
        centre.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(600f, 0f) };
        panel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.PanelFill, IsoTheme.Gold, radius: 14, borderWidth: 2, padding: 0));
        centre.AddChild(panel);

        centre.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var inner = new MarginContainer();
        inner.AddThemeConstantOverride("margin_left", 28);
        inner.AddThemeConstantOverride("margin_right", 28);
        inner.AddThemeConstantOverride("margin_top", 20);
        inner.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(inner);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 9);
        inner.AddChild(body);

        _subtitle = HudStyle.MakeLabel("AT THE DOOR", 10, IsoTheme.Gold);
        _subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_subtitle);

        _title = HudStyle.MakeLabel("", 26, IsoTheme.Gold);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        body.AddChild(_title);

        body.AddChild(new HSeparator());

        _summary = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(0f, 64f)
        };
        _summary.AddThemeFontSizeOverride("normal_font_size", 14);
        _summary.AddThemeColorOverride("default_color", IsoTheme.TextPrimary);
        body.AddChild(_summary);

        body.AddChild(new HSeparator());

        _options = new VBoxContainer();
        _options.AddThemeConstantOverride("separation", 7);
        body.AddChild(_options);

        _outcome = HudStyle.MakeLabel("", 13, IsoTheme.TextMuted);
        _outcome.HorizontalAlignment = HorizontalAlignment.Center;
        _outcome.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_outcome);

        _dismiss = new Button
        {
            Text = "Continue",
            CustomMinimumSize = new Vector2(0f, 40f),
            ProcessMode = ProcessModeEnum.Always,
            Visible = false
        };

        HudStyle.StyleButton(_dismiss, IsoTheme.Gold, 8, 16, 8);
        _dismiss.Pressed += OnDismissPressed;
        body.AddChild(_dismiss);
    }

    // ── Content ────────────────────────────────────────────────────────

    private void Populate(ClientProfile client, StaffMember staff, RoomModule room, double asking)
    {
        _title.Text = client.Name;
        _outcome.Text = "";
        _dismiss.Visible = false;

        _summary.Text =
            $"{staff?.StaffName ?? "The house"} would be showing them to " +
            $"{room?.RoomName ?? "a room"}.\n" +
            $"They came expecting to pay about ${client.FairPrice:N0}, " +
            $"and they can reach ${client.Budget:N0}.";

        foreach (var child in _options.GetChildren()) child.QueueFree();

        // Three prices spanning what they expect to what they can just afford.
        AddOffer("Take what they offered", client.FairPrice,
                 "No argument. They pay what they came prepared to pay.");

        AddOffer("The house rate", asking,
                 "What the room is worth tonight.");

        AddOffer("Push them", System.Math.Min(asking * 1.35, client.Budget),
                 "More than they meant to spend.");

        var refuse = new Button
        {
            Text = "Turn them away",
            CustomMinimumSize = new Vector2(0f, 34f),
            ProcessMode = ProcessModeEnum.Always
        };

        HudStyle.StyleButton(refuse, IsoTheme.Danger, 8, 13, 8);
        refuse.Pressed += () =>
        {
            EmitSignal(SignalName.OnClientRefused);
            Hide();
        };

        _options.AddChild(refuse);
    }

    private void AddOffer(string label, double price, string note)
    {
        // The real function, not an approximation of it. This is the whole
        // point of the screen: the odds shown are the odds used.
        float chance = _handler?.CalculateAcceptProbability(price) ?? 0.5f;

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 9));

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        card.AddChild(column);

        var head = HudStyle.MakeLabel($"{note}", 11, IsoTheme.TextMuted);
        head.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(head);

        column.AddChild(HudStyle.MakeLabel(
            $"${price:N0}  ·  {chance:P0} they say yes", 12,
            IsoTheme.GetScoreColor(chance * 100f)));

        var button = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(0f, 34f),
            ProcessMode = ProcessModeEnum.Always
        };

        HudStyle.StyleButton(button, IsoTheme.Gold, 8, 14, 8);
        button.Pressed += () => Offer(price, chance);
        column.AddChild(button);

        _options.AddChild(card);
    }

    /// <summary>
    /// Put the price to them and roll once. Whichever way it goes, the answer
    /// is shown before the modal closes — a decision whose result the player
    /// never sees teaches them nothing about the next one.
    /// </summary>
    private void Offer(double price, float chance)
    {
        bool accepted = GD.Randf() < chance;
        _agreedPrice = price;
        _accepted = accepted;

        foreach (var child in _options.GetChildren())
            if (child is Control control) control.Visible = false;

        _outcome.Text = accepted
            ? $"They agree. ${price:N0} for the room."
            : $"They will not pay ${price:N0}. They leave.";

        _outcome.AddThemeColorOverride("font_color",
            accepted ? IsoTheme.Good : IsoTheme.Danger);

        _dismiss.Visible = true;
        _dismiss.GrabFocus();
    }

    private double _agreedPrice;
    private bool _accepted;

    private void OnDismissPressed()
    {
        var price = _agreedPrice;
        var accepted = _accepted;

        Hide();

        if (accepted) EmitSignal(SignalName.OnPriceAgreed, price);
        else EmitSignal(SignalName.OnClientRefused);
    }

    public override string ToString() =>
        $"[NegotiationScreen] {(IsShowing ? _client?.Name ?? "showing" : "hidden")}";
}
