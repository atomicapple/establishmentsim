using Godot;
using System.Collections.Generic;

/// <summary>A single research node in a tech tree.</summary>
public class ResearchNode
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Tree { get; set; }   // "facility", "staff", "influence"
    public int Tier { get; set; }
    public string[] Prerequisites { get; set; } = System.Array.Empty<string>();
    public double Cost { get; set; }
    public int ResearchDays { get; set; }  // in-game days to unlock
    public int DaysRemaining { get; set; }
    public bool IsUnlocked { get; set; }
    public bool IsResearching { get; set; }
    public string EffectDescription { get; set; }
}

/// <summary>
/// Multi-branch progression node graph for research.
/// Three trees: Facility Enhancements, Staff Development, Influence & Underground.
/// Manages research costs and active unlock timers across daily ticks.
/// </summary>
public partial class ResearchTreeUI : Control
{
    [Signal] public delegate void OnResearchStartedEventHandler(string nodeId, string nodeName);
    [Signal] public delegate void OnResearchCompletedEventHandler(string nodeId, string nodeName);
    [Signal] public delegate void OnNodeClickedEventHandler(string nodeId);

    private readonly Dictionary<string, ResearchNode> _nodes = new();
    private readonly List<ResearchNode> _activeResearch = new();
    private VBoxContainer _treeContainer;
    private Label _infoLabel;
    private string _selectedTab = "facility";

    public override void _Ready()
    {
        BuildResearchTree();
        BuildUI();

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;

        GD.Print("[ResearchTreeUI] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        // Advance research timers
        for (int i = _activeResearch.Count - 1; i >= 0; i--)
        {
            var node = _activeResearch[i];
            node.DaysRemaining--;
            if (node.DaysRemaining <= 0)
            {
                node.IsResearching = false;
                node.IsUnlocked = true;
                _activeResearch.RemoveAt(i);
                EmitSignal(SignalName.OnResearchCompleted, node.Id, node.Name);
                GD.Print($"[ResearchTree] Research complete: {node.Name}!");
            }
        }
        RefreshDisplay();
    }

    private void BuildResearchTree()
    {
        // ── Facility Enhancements ───────────────────────────────────
        AddNode(new ResearchNode { Id="F1", Name="Soundproofing I", Tree="facility", Tier=1, Cost=500, ResearchDays=3, EffectDescription="Rooms: Soundproofing +15" });
        AddNode(new ResearchNode { Id="F2", Name="Luxury Finishes", Tree="facility", Tier=1, Cost=800, ResearchDays=4, EffectDescription="Rooms: LuxuryScore +20" });
        AddNode(new ResearchNode { Id="F3", Name="Expanded Floorplan", Tree="facility", Tier=2, Prerequisites=new[]{"F1"}, Cost=1500, ResearchDays=6, EffectDescription="Grid +2 columns" });
        AddNode(new ResearchNode { Id="F4", Name="VIP Wing", Tree="facility", Tier=3, Prerequisites=new[]{"F2","F3"}, Cost=3000, ResearchDays=8, EffectDescription="Unlocks VIP-only rooms" });
        AddNode(new ResearchNode { Id="F5", Name="Security Overwatch", Tree="facility", Tier=2, Prerequisites=new[]{"F1"}, Cost=1200, ResearchDays=5, EffectDescription="Security rooms: +10 discretion to neighbors" });

        // ── Staff Development ───────────────────────────────────────
        AddNode(new ResearchNode { Id="S1", Name="Service Training I", Tree="staff", Tier=1, Cost=400, ResearchDays=3, EffectDescription="Staff: Charisma +10" });
        AddNode(new ResearchNode { Id="S2", Name="Negotiation Workshop", Tree="staff", Tier=1, Cost=400, ResearchDays=3, EffectDescription="Staff: Negotiation +10" });
        AddNode(new ResearchNode { Id="S3", Name="Advanced Discretion", Tree="staff", Tier=2, Prerequisites=new[]{"S1"}, Cost=1000, ResearchDays=5, EffectDescription="Staff: Discretion +15" });
        AddNode(new ResearchNode { Id="S4", Name="Crisis Counseling", Tree="staff", Tier=2, Prerequisites=new[]{"S2"}, Cost=1000, ResearchDays=5, EffectDescription="Staff: Trauma decay +50%" });
        AddNode(new ResearchNode { Id="S5", Name="Masterclass Program", Tree="staff", Tier=3, Prerequisites=new[]{"S3","S4"}, Cost=2500, ResearchDays=10, EffectDescription="Staff: All stats +15" });

        // ── Influence & Underground ─────────────────────────────────
        AddNode(new ResearchNode { Id="I1", Name="Bribe Networks", Tree="influence", Tier=1, Cost=600, ResearchDays=4, EffectDescription="Bribes: 25% discount" });
        AddNode(new ResearchNode { Id="I2", Name="Intel Gathering I", Tree="influence", Tier=1, Cost=600, ResearchDays=4, EffectDescription="Blackmail: +15% discovery chance" });
        AddNode(new ResearchNode { Id="I3", Name="Deep Connections", Tree="influence", Tier=2, Prerequisites=new[]{"I1"}, Cost=1500, ResearchDays=6, EffectDescription="Bribes: 50% discount, favor gain +30%" });
        AddNode(new ResearchNode { Id="I4", Name="Blackmail Mastery", Tree="influence", Tier=2, Prerequisites=new[]{"I2"}, Cost=1500, ResearchDays=6, EffectDescription="Blackmail: CapitalFavor rarity +1" });
        AddNode(new ResearchNode { Id="I5", Name="Shadow Network", Tree="influence", Tier=3, Prerequisites=new[]{"I3","I4"}, Cost=3000, ResearchDays=10, EffectDescription="All political favor +20, rival sabotage 2x effective" });
    }

    private void AddNode(ResearchNode node) => _nodes[node.Id] = node;

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var mainVbox = new VBoxContainer();
        mainVbox.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(mainVbox);

        // Tab bar
        var tabBar = new HBoxContainer();
        tabBar.AddThemeConstantOverride("separation", 4);
        mainVbox.AddChild(tabBar);

        foreach (var (tab, label) in new[] { ("facility","🏗 Facility"), ("staff","👥 Staff"), ("influence","🕵️ Influence") })
        {
            var btn = new Button();
            btn.Text = label;
            btn.Pressed += () => { _selectedTab = tab; RefreshDisplay(); };
            tabBar.AddChild(btn);
        }

        mainVbox.AddChild(new HSeparator());

        // Info
        _infoLabel = new Label();
        _infoLabel.Text = "Select a research node to view details.";
        _infoLabel.AddThemeFontSizeOverride("font_size", 12);
        mainVbox.AddChild(_infoLabel);

        // Scrollable tree
        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVbox.AddChild(scroll);

        _treeContainer = new VBoxContainer();
        _treeContainer.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_treeContainer);

        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        foreach (Node child in _treeContainer.GetChildren())
            child.QueueFree();

        var gsm = GameStateManager.Instance;

        foreach (var node in _nodes.Values)
        {
            if (node.Tree != _selectedTab) continue;
            if (!PrerequisitesMet(node)) continue;

            var card = new PanelContainer();
            var cardStyle = new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.14f) };
            card.AddThemeStyleboxOverride("panel", cardStyle);

            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 8);
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 8);
            margin.AddThemeConstantOverride("margin_right", 8);
            margin.AddThemeConstantOverride("margin_top", 6);
            margin.AddThemeConstantOverride("margin_bottom", 6);
            card.AddChild(margin);
            margin.AddChild(hbox);

            // Status icon
            var statusLabel = new Label();
            statusLabel.Text = node.IsUnlocked ? "✅" : node.IsResearching ? $"⏳{node.DaysRemaining}d" : "🔒";
            statusLabel.AddThemeFontSizeOverride("font_size", 16);
            hbox.AddChild(statusLabel);

            // Info
            var infoVbox = new VBoxContainer();
            var nameLabel = new Label();
            nameLabel.Text = $"{node.Name} (T{node.Tier})";
            nameLabel.AddThemeFontSizeOverride("font_size", 14);
            nameLabel.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.3f));
            infoVbox.AddChild(nameLabel);

            var descLabel = new Label();
            descLabel.Text = $"{node.Description}\n{node.EffectDescription}";
            descLabel.AddThemeFontSizeOverride("font_size", 10);
            descLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.8f));
            infoVbox.AddChild(descLabel);

            hbox.AddChild(infoVbox);

            // Spacer
            var spacer = new Control();
            spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            hbox.AddChild(spacer);

            // Research button
            if (!node.IsUnlocked && !node.IsResearching)
            {
                var btn = new Button();
                bool canAfford = gsm != null && gsm.Cash >= node.Cost;
                btn.Text = $"Research\n${node.Cost:N0}\n({node.ResearchDays}d)";
                btn.Disabled = !canAfford;
                string capturedId = node.Id;
                btn.Pressed += () => StartResearch(capturedId);
                hbox.AddChild(btn);
            }

            // Click for details
            card.GuiInput += (evt) =>
            {
                if (evt is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
                    EmitSignal(SignalName.OnNodeClicked, node.Id);
            };

            _treeContainer.AddChild(card);
        }
    }

    private bool PrerequisitesMet(ResearchNode node)
    {
        if (node.Prerequisites == null || node.Prerequisites.Length == 0) return true;
        foreach (var preq in node.Prerequisites)
            if (!_nodes.TryGetValue(preq, out var n) || !n.IsUnlocked) return false;
        return true;
    }

    public void StartResearch(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return;
        if (node.IsUnlocked || node.IsResearching) return;

        var gsm = GameStateManager.Instance;
        if (gsm != null && gsm.Cash >= node.Cost)
        {
            gsm.Cash -= node.Cost;
            node.DaysRemaining = node.ResearchDays;
            node.IsResearching = true;
            _activeResearch.Add(node);
            EmitSignal(SignalName.OnResearchStarted, nodeId, node.Name);
            GD.Print($"[ResearchTree] Started: {node.Name} (${node.Cost}, {node.ResearchDays}d)");
            RefreshDisplay();
        }
    }

    private void ApplyUnlockEffect(string nodeId)
    {
        // Effects are applied by game systems listening to OnResearchCompleted
    }

    public override string ToString() =>
        $"[ResearchTree] Nodes={_nodes.Count} Active={_activeResearch.Count}";
}
