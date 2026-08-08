using Godot;
using System.Collections.Generic;

/// <summary>
/// The first thing a new campaign shows.
///
/// Four cards, skippable, and deliberately short. It explains the shape of a
/// night and what the house actually sells, then stops — the specific "do this
/// next" guidance lives in <see cref="Onboarding"/>, driven by real state, and
/// arrives when it is relevant rather than all at once up front.
///
/// It is shown only for a new campaign, never on a capture run or a loaded
/// game.
/// </summary>
public partial class IntroScreen : CanvasLayer
{
    /// <summary>The player is done reading.</summary>
    [Signal]
    public delegate void OnDismissedEventHandler();

    private sealed class Card
    {
        public string Title;
        public string Body;
    }

    private static readonly List<Card> Cards = new()
    {
        new Card
        {
            Title = "The house",
            Body =
                "You have a lease on a ground floor, three people on the books, " +
                "and three thousand dollars.\n\n" +
                "The reception and the bar came with the building. Everything " +
                "that earns — every suite, every floor above this one — you " +
                "build yourself."
        },

        new Card
        {
            Title = "A night",
            Body =
                "Preparation is paused. Build, furnish, hire, and decide who " +
                "works which room. Nothing moves until you open the doors.\n\n" +
                "Then the night runs by itself. Clients arrive, get matched to " +
                "a room, and what happens is settled by the choices you already " +
                "made. At dawn you get the books."
        },

        new Card
        {
            Title = "What you are selling",
            Body =
                "A room's Appointment score is what a client is really paying " +
                "for. It comes from having the right kinds of furniture, their " +
                "quality, and — the part that matters most — whether they " +
                "share a style.\n\n" +
                "Cheap pieces that match beat expensive pieces that clash. A " +
                "coherent room earns more than a costly jumble."
        },

        new Card
        {
            Title = "What it costs",
            Body =
                "Your staff are people. They tire, they resent being worked, " +
                "and they leave or break if you let it run. The police notice " +
                "a house that gets loud, and the city's mood swings for weeks " +
                "at a time whatever you do.\n\n" +
                "Nothing here is solved, only balanced. Press the ? button at " +
                "any time for the keys."
        }
    };

    private Control _root;
    private Label _title;
    private RichTextLabel _body;
    private Label _progress;
    private Button _next;
    private Button _back;

    private int _index;

    public bool IsShowing => _root != null && _root.Visible;

    public override void _Ready()
    {
        Layer = 50;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
    }

    public void ShowIntro()
    {
        if (_root == null) BuildSkeleton();

        _index = 0;
        Populate();

        Visible = true;
        _root.Visible = true;
        if (GetTree() != null) GetTree().Paused = true;

        _next.GrabFocus();
    }

    /// <summary>
    /// Shadows <c>CanvasLayer.Hide()</c>: this screen holds a pause and a
    /// plain visibility toggle would leave the game frozen behind it.
    /// </summary>
    public new void Hide()
    {
        if (_root != null) _root.Visible = false;
        Visible = false;

        if (GetTree() != null) GetTree().Paused = false;
    }

    // ── Skeleton ───────────────────────────────────────────────────────

    private void BuildSkeleton()
    {
        _root = new Control { Name = "IntroRoot" };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.ProcessMode = ProcessModeEnum.Always;
        AddChild(_root);

        var scrim = new ColorRect
        {
            Color = new Color(IsoTheme.Backdrop.R, IsoTheme.Backdrop.G, IsoTheme.Backdrop.B, 0.97f)
        };
        scrim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        scrim.MouseFilter = Control.MouseFilterEnum.Stop;
        _root.AddChild(scrim);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 40);
        margin.AddThemeConstantOverride("margin_right", 40);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        _root.AddChild(margin);

        var centre = new HBoxContainer();
        margin.AddChild(centre);
        centre.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(640f, 0f),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };

        panel.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.PanelFill, IsoTheme.Gold, radius: 14, borderWidth: 2, padding: 0));
        centre.AddChild(panel);

        centre.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var inner = new MarginContainer();
        inner.AddThemeConstantOverride("margin_left", 34);
        inner.AddThemeConstantOverride("margin_right", 34);
        inner.AddThemeConstantOverride("margin_top", 26);
        inner.AddThemeConstantOverride("margin_bottom", 22);
        panel.AddChild(inner);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        inner.AddChild(body);

        body.AddChild(HudStyle.MakeLabel("ESTABLISHMENT SIMULATOR", 10, IsoTheme.GoldDim));

        _title = HudStyle.MakeLabel("", 30, IsoTheme.Gold);
        body.AddChild(_title);

        body.AddChild(new HSeparator());

        _body = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(0f, 190f)
        };

        _body.AddThemeFontSizeOverride("normal_font_size", 15);
        _body.AddThemeColorOverride("default_color", IsoTheme.TextPrimary);
        body.AddChild(_body);

        body.AddChild(new HSeparator());

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        body.AddChild(row);

        _progress = HudStyle.MakeLabel("", 11, IsoTheme.TextMuted);
        _progress.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(_progress);

        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var skip = new Button { Text = "Skip", ProcessMode = ProcessModeEnum.Always };
        HudStyle.StyleButton(skip, IsoTheme.GoldDim, 8, 13, 12);
        skip.Pressed += Dismiss;
        row.AddChild(skip);

        _back = new Button { Text = "Back", ProcessMode = ProcessModeEnum.Always };
        HudStyle.StyleButton(_back, IsoTheme.GoldDim, 8, 13, 12);
        _back.Pressed += () => { _index--; Populate(); };
        row.AddChild(_back);

        _next = new Button
        {
            Text = "Next",
            CustomMinimumSize = new Vector2(120f, 38f),
            ProcessMode = ProcessModeEnum.Always
        };

        HudStyle.StyleButton(_next, IsoTheme.Gold, 8, 15, 12);
        _next.Pressed += OnNextPressed;
        row.AddChild(_next);
    }

    // ── Content ────────────────────────────────────────────────────────

    private void Populate()
    {
        _index = Mathf.Clamp(_index, 0, Cards.Count - 1);

        var card = Cards[_index];
        _title.Text = card.Title;
        _body.Text = card.Body;

        _progress.Text = $"{_index + 1} of {Cards.Count}";
        _back.Visible = _index > 0;
        _next.Text = _index == Cards.Count - 1 ? "Open the house" : "Next";
    }

    private void OnNextPressed()
    {
        if (_index >= Cards.Count - 1)
        {
            Dismiss();
            return;
        }

        _index++;
        Populate();
    }

    private void Dismiss()
    {
        Hide();
        EmitSignal(SignalName.OnDismissed);
    }
}
