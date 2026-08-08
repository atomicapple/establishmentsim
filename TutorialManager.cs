using Godot;
using System;

/// <summary>Tutorial progression state machine.</summary>
public enum TutorialState
{
    Boot,
    CleanRoom,
    SecureClient,
    ReviewLedger,
    SignUpgrade,
    Completed
}

/// <summary>
/// Core tutorial state machine managing the 4-step onboarding flow.
/// Tracks player interactions (room cleaning, client haggling,
/// ledger bribes, upgrade purchases), blocks out-of-step inputs,
/// and displays guided dialogue boxes with focus overlays.
/// </summary>
public partial class TutorialManager : Node
{
    [Signal] public delegate void OnStateChangedEventHandler(int oldState, int newState);
    [Signal] public delegate void OnTutorialCompletedEventHandler();

    // ── State ───────────────────────────────────────────────────────────
    private TutorialState _state = TutorialState.Boot;
    private bool _inputBlocked;

    // ── UI Overlay Nodes ────────────────────────────────────────────────
    private ColorRect _dimOverlay;
    private PanelContainer _dialogueBox;
    private Label _dialogueTitle, _dialogueBody;
    private ColorRect _focusHighlight;
    private Button _skipTutorialBtn;
    private Label _stepIndicator;

    // ── Event Tracking ──────────────────────────────────────────────────
    private bool _roomCleaned;
    private bool _clientHaggled;
    private bool _bribePaid;
    private bool _upgradePurchased;

    // ── Art Deco Palette ────────────────────────────────────────────────
    private static readonly Color Gold    = new(0.85f, 0.7f, 0.25f);
    private static readonly Color GoldDim = new(0.5f, 0.4f, 0.15f);
    private static readonly Color DarkBg  = new(0.06f, 0.05f, 0.1f);

    public TutorialState CurrentState => _state;
    public bool InputBlocked => _inputBlocked;

    public override void _Ready()
    {
        BuildOverlay();
        WireEventListeners();
        SetState(TutorialState.Boot);
        GD.Print("[TutorialManager] Ready. Boot state.");
    }

    // ── Overlay Construction ────────────────────────────────────────────

    private void BuildOverlay()
    {
        // Full-screen dim overlay (input blocking layer)
        _dimOverlay = new ColorRect();
        _dimOverlay.Color = new Color(0, 0, 0, 0.55f);
        _dimOverlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _dimOverlay.MouseFilter = Control.MouseFilterEnum.Stop; // blocks clicks behind
        AddChild(_dimOverlay);

        // Focus highlight rectangle
        _focusHighlight = new ColorRect();
        _focusHighlight.Color = new Color(1f, 0.85f, 0.2f, 0.25f);
        _focusHighlight.Visible = false;
        AddChild(_focusHighlight);

        // Dialogue box — centered bottom panel
        _dialogueBox = new PanelContainer();
        _dialogueBox.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _dialogueBox.SetSize(new Vector2(0, 150));
        _dialogueBox.OffsetBottom = 0;
        var panelStyle = new StyleBoxFlat
        {
            BgColor = DarkBg,
            BorderWidthTop = 3,
            BorderColor = Gold,
            ContentMarginLeft = 20, ContentMarginRight = 20,
            ContentMarginTop = 12, ContentMarginBottom = 12
        };
        _dialogueBox.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_dialogueBox);

        var vbox = new VBoxContainer();
        _dialogueBox.AddChild(vbox);

        _stepIndicator = new Label();
        _stepIndicator.AddThemeFontSizeOverride("font_size", 11);
        _stepIndicator.AddThemeColorOverride("font_color", GoldDim);
        vbox.AddChild(_stepIndicator);

        _dialogueTitle = new Label();
        _dialogueTitle.AddThemeFontSizeOverride("font_size", 16);
        _dialogueTitle.AddThemeColorOverride("font_color", Gold);
        vbox.AddChild(_dialogueTitle);

        _dialogueBody = new Label();
        _dialogueBody.AddThemeFontSizeOverride("font_size", 12);
        _dialogueBody.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.9f));
        _dialogueBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(_dialogueBody);

        // Skip button (top right)
        _skipTutorialBtn = new Button();
        _skipTutorialBtn.Text = "SKIP TUTORIAL";
        _skipTutorialBtn.Flat = true;
        _skipTutorialBtn.AddThemeFontSizeOverride("font_size", 10);
        _skipTutorialBtn.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.5f));
        _skipTutorialBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _skipTutorialBtn.Position = new Vector2(-120, 10);
        _skipTutorialBtn.Pressed += CompleteTutorial;
        AddChild(_skipTutorialBtn);
    }

    // ── Event Wiring ───────────────────────────────────────────────────

    private void WireEventListeners()
    {
        // Listen for UI actions from game systems
        // We use CallDeferred to wait for all nodes to be ready in the scene tree
        CallDeferred(nameof(ConnectToSystems));
    }

    private void ConnectToSystems()
    {
        // Room cleaning: RoomAlertOverlay.OnRoomAlert cleared
        var alerts = GetTree()?.Root?.FindChild("RoomAlertOverlay", true, false) as RoomAlertOverlay;

        // Client haggling: ClientNegotiationHandler.OnDealClosed
        var negotiator = GetTree()?.Root?.FindChild("ClientNegotiationHandler", true, false) as ClientNegotiationHandler;
        if (negotiator != null)
            negotiator.OnDealClosed += OnClientDealClosed;

        // Ledger bribe: FinancialLedger records ExpenseCategory.Bribes
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;
        // We'll hook into the ledger's expense recording via the HeatSystem bribe events
        var heat = GetTree()?.Root?.FindChild("HeatSystem", true, false) as HeatSystem;
        if (heat != null)
            heat.OnBribePaid += OnBribePaid;

        // Upgrade purchase: the research tree was deleted — every one of its
        // fifteen nodes duplicated a system that already works and is
        // reachable. FacilityLicences replaces it, and the tutorial hooks
        // onto that instead.
        var licences = GetTree()?.Root?.FindChild("FacilityLicences", true, false)
            as FacilityLicences;

        if (licences != null)
            licences.OnLicenceStarted += (id, _) =>
                OnResearchStarted(id, FacilityLicences.Get(id)?.Name ?? id);
    }

    // ── State Machine ──────────────────────────────────────────────────

    private void SetState(TutorialState newState)
    {
        var old = _state;
        _state = newState;
        EmitSignal(SignalName.OnStateChanged, (int)old, (int)newState);

        GD.Print($"[TutorialManager] State: {old} → {newState}");

        switch (newState)
        {
            case TutorialState.Boot:         SetupBoot(); break;
            case TutorialState.CleanRoom:    SetupCleanRoom(); break;
            case TutorialState.SecureClient: SetupSecureClient(); break;
            case TutorialState.ReviewLedger: SetupReviewLedger(); break;
            case TutorialState.SignUpgrade:  SetupSignUpgrade(); break;
            case TutorialState.Completed:    HandleCompleted(); break;
        }
    }

    // ── State Setups ───────────────────────────────────────────────────

    private void SetupBoot()
    {
        ShowOverlay(false);
        _dialogueBox.Visible = false;
        _inputBlocked = false;

        // Auto-advance after a short delay
        var timer = GetTree().CreateTimer(1.5f);
        timer.Timeout += () => SetState(TutorialState.CleanRoom);
    }

    private void SetupCleanRoom()
    {
        _inputBlocked = true;
        ShowOverlay(true);
        _dialogueBox.Visible = true;

        _stepIndicator.Text = "STEP 1 OF 4 — SPATIAL MICRO-LOOP";
        _dialogueTitle.Text = "Room Management: Clean the Dirty Room";
        _dialogueBody.Text = "A room in your venue needs cleaning — look for the flashing 🧹 icon in the isometric view. Click the room and assign a staff member to clean it. A clean room maintains your Luxury Score and keeps clients happy.";

        HighlightTarget("VenueBuilding");
    }

    private void SetupSecureClient()
    {
        _inputBlocked = true;
        ShowOverlay(true);
        _dialogueBox.Visible = true;

        _stepIndicator.Text = "STEP 2 OF 4 — CORE NEGOTIATION LOOP";
        _dialogueTitle.Text = "Client Haggling: Close the VIP Deal";
        _dialogueBody.Text = "A wealthy VIP client has arrived. The Client Interaction Interface will display their offer. Use your staff's Charisma to counter-offer or Negotiation to upsell premium services. Every counter-offer costs patience — push too hard and the client walks. Secure the deal to advance.";

        HighlightTarget("EventDialogUI");
    }

    private void SetupReviewLedger()
    {
        _inputBlocked = true;
        ShowOverlay(true);
        _dialogueBox.Visible = true;

        _stepIndicator.Text = "STEP 3 OF 4 — ECONOMIC MESO-LOOP";
        _dialogueTitle.Text = "Financial Ledger: Pay Off the Precinct";
        _dialogueBody.Text = "The daily tick has completed. Open the Financial Ledger and locate the highlighted 'Heat Mitigation / Bribes' line. Pay $2,500 to the local precinct captain to reduce Heat by 25 points. Managing Heat is the difference between profitable operations and a police raid.";

        HighlightTarget("FinancialLedger");
    }

    private void SetupSignUpgrade()
    {
        _inputBlocked = true;
        ShowOverlay(true);
        _dialogueBox.Visible = true;

        _stepIndicator.Text = "STEP 4 OF 4 — STRATEGIC MACRO-LOOP";
        _dialogueTitle.Text = "Research Tree: Purchase Security Upgrade";
        _dialogueBody.Text = "To permanently reduce Heat generation, invest in facility upgrades. Open the Research & Upgrades Tree, navigate to Facility Enhancements, and purchase 'Advanced Security Systems' for $4,000. This permanently reduces global room Heat generation. Strategic investments compound — a single upgrade saves thousands in bribes over a campaign.";

        HighlightTarget("ResearchTreeUI");
    }

    private void HandleCompleted()
    {
        _inputBlocked = false;
        ShowOverlay(false);
        _dialogueBox.Visible = false;
        _focusHighlight.Visible = false;

        GD.Print("[TutorialManager] Tutorial complete. Player has full control.");
        EmitSignal(SignalName.OnTutorialCompleted);
    }

    // ── Event Handlers ─────────────────────────────────────────────────

    /// <summary>Call this when a room is cleaned.</summary>
    public void NotifyRoomCleaned()
    {
        if (_state != TutorialState.CleanRoom) return;
        _roomCleaned = true;
        GD.Print("[TutorialManager] Room cleaned — advancing to SecureClient.");
        SetState(TutorialState.SecureClient);
    }

    private void OnClientDealClosed(string clientName, double finalPrice, float charisma, float negotiation)
    {
        if (_state != TutorialState.SecureClient) return;
        _clientHaggled = true;
        GD.Print($"[TutorialManager] Client deal closed: ${finalPrice:F0} — advancing to ReviewLedger.");
        SetState(TutorialState.ReviewLedger);
    }

    private void OnBribePaid(double cost, float heatReduction)
    {
        if (_state != TutorialState.ReviewLedger) return;
        _bribePaid = true;
        GD.Print($"[TutorialManager] Bribe paid: ${cost:F0} — advancing to SignUpgrade.");
        SetState(TutorialState.SignUpgrade);
    }

    private void OnResearchStarted(string nodeId, string nodeName)
    {
        if (_state != TutorialState.SignUpgrade) return;
        _upgradePurchased = true;
        GD.Print($"[TutorialManager] Upgrade purchased: {nodeName} — tutorial complete.");
        SetState(TutorialState.Completed);
    }

    /// <summary>Force-complete the tutorial (used by Skip button).</summary>
    public void CompleteTutorial()
    {
        SetState(TutorialState.Completed);
    }

    // ── Focus Overlay ──────────────────────────────────────────────────

    private void HighlightTarget(string nodeName)
    {
        var target = GetTree()?.Root?.FindChild(nodeName, true, false) as Control;
        if (target == null)
        {
            _focusHighlight.Visible = false;
            GD.Print($"[TutorialManager] Target '{nodeName}' not found for highlight.");
            return;
        }

        var globalPos = target.GlobalPosition;
        var size = target.Size;
        _focusHighlight.Position = globalPos - new Vector2(6, 6);
        _focusHighlight.SetSize(size + new Vector2(12, 12));
        _focusHighlight.Visible = true;

        // Pulsing glow animation
        var tween = CreateTween();
        tween.SetLoops();
        tween.TweenProperty(_focusHighlight, "color:a", 0.35f, 0.6f);
        tween.TweenProperty(_focusHighlight, "color:a", 0.15f, 0.6f);
    }

    private void ShowOverlay(bool show)
    {
        _dimOverlay.Visible = show;
        _dimOverlay.MouseFilter = show ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
    }

    // ── Manual Advancement (for testing / fallback) ────────────────────

    /// <summary>Manually advance to the next tutorial state.</summary>
    public void ForceAdvance()
    {
        var next = _state switch
        {
            TutorialState.Boot         => TutorialState.CleanRoom,
            TutorialState.CleanRoom    => TutorialState.SecureClient,
            TutorialState.SecureClient => TutorialState.ReviewLedger,
            TutorialState.ReviewLedger => TutorialState.SignUpgrade,
            TutorialState.SignUpgrade  => TutorialState.Completed,
            _                          => TutorialState.Completed
        };
        SetState(next);
    }

    public override string ToString() =>
        $"[TutorialManager] State={_state} Blocked={_inputBlocked}";
}
