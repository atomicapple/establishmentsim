using Godot;
using System;
using System.Collections.Generic;

/// <summary>Represents a district node on the city map.</summary>
public class DistrictNode
{
    public string Name { get; set; }
    public string Description { get; set; }
    public Vector2 Position { get; set; }
    public float Heat { get; set; }
    public float PolicePatrolIntensity { get; set; }
    public float ClientFootfall { get; set; }
    public DistrictOwner Owner { get; set; }
    public bool IsOwned => Owner == DistrictOwner.Player;
    public bool IsRival => Owner == DistrictOwner.Rival;
    public bool IsAvailable => Owner == DistrictOwner.Unpurchased;
    public double PurchasePrice { get; set; }
    public string ScenePath { get; set; }
}

public enum DistrictOwner { Unpurchased, Player, Rival }

/// <summary>
/// Interactive 2D city map displaying districts as clickable nodes.
/// Shows owned venues, rival turfs, and unpurchased properties.
/// Hover reveals Heat, Police Patrol, and Client Footfall metrics.
/// Click swaps camera or opens purchase dialog.
/// </summary>
public partial class DistrictMapUI : Control
{
    [Signal] public delegate void OnDistrictClickedEventHandler(string districtName, bool isOwned);
    [Signal] public delegate void OnDistrictHoveredEventHandler(string districtName);
    [Signal] public delegate void OnPurchaseRequestedEventHandler(string districtName, double price);

    private readonly List<DistrictNode> _districts = new();
    private readonly Dictionary<string, Control> _nodeControls = new();
    private Control _hoverPanel;
    private Label _hoverTitle;
    private Label _hoverHeat;
    private Label _hoverPolice;
    private Label _hoverFootfall;
    private Label _hoverOwner;
    private string _hoveredDistrict;
    private Camera2D _mapCamera;
    private bool _isDragging;
    private Vector2 _dragStart;

    public override void _Ready()
    {
        BuildMap();
        BuildHoverPanel();
        GD.Print("[DistrictMapUI] Initialized.");
    }

    private void BuildMap()
    {
        // Map background
        var bg = new ColorRect();
        bg.Color = new Color(0.08f, 0.08f, 0.12f);
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        // Camera for pan
        _mapCamera = new Camera2D();
        _mapCamera.Enabled = true;
        AddChild(_mapCamera);

        // Define districts
        _districts.AddRange(new[]
        {
            new DistrictNode { Name="Red Lantern Row", Position=new Vector2(200,150), Heat=25f, PolicePatrolIntensity=0.3f, ClientFootfall=0.7f, Owner=DistrictOwner.Player, PurchasePrice=5000, Description="The heart of the night trade.", ScenePath="res://scenes/red_lantern.tscn" },
            new DistrictNode { Name="Harbor Wharfs", Position=new Vector2(500,300), Heat=60f, PolicePatrolIntensity=0.6f, ClientFootfall=0.5f, Owner=DistrictOwner.Rival, PurchasePrice=8000, Description="Smuggler's haven — profitable but dangerous." },
            new DistrictNode { Name="Gilded Heights", Position=new Vector2(100,400), Heat=10f, PolicePatrolIntensity=0.1f, ClientFootfall=0.9f, Owner=DistrictOwner.Unpurchased, PurchasePrice=12000, Description="Wealthy residential district. Premium clientele." },
            new DistrictNode { Name="The Warrens", Position=new Vector2(350,450), Heat=45f, PolicePatrolIntensity=0.4f, ClientFootfall=0.3f, Owner=DistrictOwner.Unpurchased, PurchasePrice=3000, Description="Cheap properties, desperate clients." },
            new DistrictNode { Name="Market Square", Position=new Vector2(600,100), Heat=35f, PolicePatrolIntensity=0.5f, ClientFootfall=0.85f, Owner=DistrictOwner.Unpurchased, PurchasePrice=7000, Description="Busy commercial hub. High footfall." },
            new DistrictNode { Name="Temple District", Position=new Vector2(300,200), Heat=5f, PolicePatrolIntensity=0.05f, ClientFootfall=0.4f, Owner=DistrictOwner.Rival, PurchasePrice=9000, Description="Religious quarter. Low heat, moral complications." },
            new DistrictNode { Name="Iron Gate Slums", Position=new Vector2(500,500), Heat=70f, PolicePatrolIntensity=0.8f, ClientFootfall=0.15f, Owner=DistrictOwner.Player, PurchasePrice=1500, Description="Dangerous but loyal client base." },
            new DistrictNode { Name="Garden Promenade", Position=new Vector2(650,380), Heat=15f, PolicePatrolIntensity=0.2f, ClientFootfall=0.95f, Owner=DistrictOwner.Unpurchased, PurchasePrice=15000, Description="Elite leisure district. Maximum prestige." },
        });

        foreach (var d in _districts)
        {
            var node = CreateDistrictControl(d);
            _nodeControls[d.Name] = node;
            AddChild(node);
        }
    }

    private Control CreateDistrictControl(DistrictNode district)
    {
        var container = new Control();
        container.Position = district.Position - new Vector2(40, 40);
        container.SetSize(new Vector2(80, 80));
        container.MouseFilter = MouseFilterEnum.Stop;

        // District circle
        var circle = new ColorRect();
        circle.SetSize(new Vector2(60, 60));
        circle.Position = new Vector2(10, 10);
        circle.PivotOffset = new Vector2(30, 30);

        var style = new StyleBoxFlat();
        style.BgColor = district.Owner switch
        {
            DistrictOwner.Player => new Color(0.15f, 0.5f, 0.85f, 0.8f),
            DistrictOwner.Rival => new Color(0.85f, 0.2f, 0.2f, 0.8f),
            _ => new Color(0.3f, 0.3f, 0.3f, 0.6f)
        };
        style.CornerRadiusTopLeft = 30; style.CornerRadiusTopRight = 30;
        style.CornerRadiusBottomLeft = 30; style.CornerRadiusBottomRight = 30;
        style.BorderWidthBottom = 2;
        style.BorderWidthTop = 2;
        style.BorderWidthLeft = 2;
        style.BorderWidthRight = 2;
        style.BorderColor = district.IsOwned ? Colors.White : new Color(0.5f, 0.5f, 0.5f);
        circle.AddThemeStyleboxOverride("panel", style);
        container.AddChild(circle);

        // Label
        var label = new Label();
        label.Text = district.Name;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Position = new Vector2(-10, 68);
        label.SetSize(new Vector2(100, 20));
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        container.AddChild(label);

        // Input handlers
        container.GuiInput += (evt) => OnDistrictInput(evt, district);
        container.MouseEntered += () => OnDistrictEnter(district);
        container.MouseExited += () => OnDistrictExit();

        return container;
    }

    private void BuildHoverPanel()
    {
        _hoverPanel = new PanelContainer();
        _hoverPanel.Visible = false;
        _hoverPanel.SetSize(new Vector2(200, 130));
        _hoverPanel.MouseFilter = MouseFilterEnum.Ignore;

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.1f, 0.95f),
            BorderWidthBottom = 1, BorderWidthTop = 1,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = new Color(0.4f, 0.4f, 0.5f)
        };
        _hoverPanel.AddThemeStyleboxOverride("panel", panelStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _hoverPanel.AddChild(margin);
        margin.AddChild(vbox);

        _hoverTitle = new Label();
        _hoverTitle.AddThemeFontSizeOverride("font_size", 14);
        _hoverTitle.AddThemeColorOverride("font_color", Colors.White);
        vbox.AddChild(_hoverTitle);

        vbox.AddChild(new HSeparator());

        _hoverOwner = new Label();
        _hoverOwner.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_hoverOwner);

        _hoverHeat = new Label();
        _hoverHeat.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_hoverHeat);

        _hoverPolice = new Label();
        _hoverPolice.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_hoverPolice);

        _hoverFootfall = new Label();
        _hoverFootfall.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_hoverFootfall);

        AddChild(_hoverPanel);
    }

    // ── Input Handling ─────────────────────────────────────────────────

    private void OnDistrictInput(InputEvent evt, DistrictNode district)
    {
        if (evt is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            if (district.IsOwned)
            {
                GD.Print($"[DistrictMapUI] Owned district clicked: {district.Name}");
                EmitSignal(SignalName.OnDistrictClicked, district.Name, true);
            }
            else if (district.IsAvailable)
            {
                GD.Print($"[DistrictMapUI] Available district clicked: {district.Name} (${district.PurchasePrice})");
                EmitSignal(SignalName.OnPurchaseRequested, district.Name, district.PurchasePrice);
            }
        }
    }

    private void OnDistrictEnter(DistrictNode district)
    {
        _hoveredDistrict = district.Name;

        _hoverTitle.Text = district.Name;
        _hoverOwner.Text = district.Owner switch
        {
            DistrictOwner.Player => "🟦 Owned by You",
            DistrictOwner.Rival => "🟥 Rival Control",
            _ => $"⬜ Available — ${district.PurchasePrice:N0}"
        };
        _hoverOwner.AddThemeColorOverride("font_color", district.IsOwned
            ? new Color(0.3f, 0.7f, 1f) : new Color(0.7f, 0.7f, 0.7f));

        _hoverHeat.Text = $"Heat: {district.Heat:F0}/100";
        _hoverHeat.AddThemeColorOverride("font_color", district.Heat > 50
            ? new Color(1f, 0.4f, 0.3f) : new Color(0.5f, 1f, 0.4f));

        _hoverPolice.Text = $"Police: {district.PolicePatrolIntensity * 100:F0}%";
        _hoverPolice.AddThemeColorOverride("font_color", district.PolicePatrolIntensity > 0.5f
            ? new Color(1f, 0.5f, 0.3f) : new Color(0.6f, 0.8f, 0.5f));

        _hoverFootfall.Text = $"Footfall: {district.ClientFootfall * 100:F0}%";
        _hoverFootfall.AddThemeColorOverride("font_color", district.ClientFootfall > 0.6f
            ? new Color(0.3f, 1f, 0.5f) : new Color(0.7f, 0.5f, 0.3f));

        _hoverPanel.Visible = true;
        _hoverPanel.Position = district.Position + new Vector2(50, -40);

        EmitSignal(SignalName.OnDistrictHovered, district.Name);
    }

    private void OnDistrictExit()
    {
        _hoverPanel.Visible = false;
        _hoveredDistrict = null;
    }

    public override void _GuiInput(InputEvent evt)
    {
        if (evt is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Middle || (mb.ButtonIndex == MouseButton.Right))
            {
                _isDragging = mb.Pressed;
                _dragStart = GetGlobalMousePosition();
            }
        }
        else if (evt is InputEventMouseMotion mm && _isDragging)
        {
            var delta = GetGlobalMousePosition() - _dragStart;
            _mapCamera.Position -= delta * 0.5f;
            _dragStart = GetGlobalMousePosition();
        }

        // Zoom
        if (evt is InputEventMouseButton zoom && zoom.ButtonIndex == MouseButton.WheelUp)
            _mapCamera.Zoom *= 1.1f;
        if (evt is InputEventMouseButton zoom2 && zoom2.ButtonIndex == MouseButton.WheelDown)
            _mapCamera.Zoom *= 0.9f;
    }

    /// <summary>Update district ownership (called after purchase).</summary>
    public void SetDistrictOwned(string districtName)
    {
        var district = _districts.Find(d => d.Name == districtName);
        if (district == null) return;
        district.Owner = DistrictOwner.Player;

        if (_nodeControls.TryGetValue(districtName, out var control))
        {
            control.QueueFree();
            var newNode = CreateDistrictControl(district);
            _nodeControls[districtName] = newNode;
            AddChild(newNode);
            MoveChild(newNode, 0);
        }
    }

    public DistrictNode GetDistrict(string name) => _districts.Find(d => d.Name == name);
    public IReadOnlyList<DistrictNode> Districts => _districts;
}
