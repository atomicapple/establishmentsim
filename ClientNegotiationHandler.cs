using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Domain Types ───────────────────────────────────────────────────────

/// <summary>Client preference priorities when choosing services.</summary>
public enum ClientPreference
{
    Luxury,       // Wants the best room/experience, price-insensitive
    Discretion,   // Privacy-focused, willing to pay premium for secrecy
    Speed,        // Wants fast service, impatient by nature
    Price,        // Budget-conscious, haggles hard
    Exclusivity   // Wants VIP treatment, rare services
}

/// <summary>Phases of the negotiation state machine.</summary>
public enum NegotiationState
{
    Idle,           // No active negotiation
    Opening,        // Initial offer presented to client
    Haggling,       // Active back-and-forth
    FinalOffer,     // Last counter-offer before walk-away
    Accepted,       // Deal closed successfully
    Rejected,       // Client refused all offers
    WalkedAway      // Client patience exhausted, left
}

/// <summary>Offer direction in a negotiation round.</summary>
public enum OfferDirection
{
    StaffToClient,  // Staff makes an offer to the client
    ClientToStaff   // Client counters with their own offer
}

/// <summary>A single negotiation round record.</summary>
public struct NegotiationRound
{
    public OfferDirection Direction;
    public double Amount;
    public float PatienceRemaining;
    public string Comment;

    public override string ToString() =>
        $"[{Direction}] ${Amount:F2} (patience: {PatienceRemaining:F0}) — {Comment}";
}

/// <summary>
/// Client profile defining budget, personality, and service preferences.
/// </summary>
public class ClientProfile
{
    public string Name { get; set; } = "Client";
    public string Description { get; set; } = "";

    // ── Financial ──────────────────────────────────────────────────
    /// <summary>Maximum the client is willing to spend.</summary>
    public double Budget { get; set; } = 200.0;

    /// <summary>Price point the client considers "fair" (used as opening anchor).</summary>
    public double FairPrice { get; set; } = 100.0;

    // ── Psychology ─────────────────────────────────────────────────
    /// <summary>Patience level, 0–100. Ticks down during negotiation.</summary>
    public float Patience { get; set; } = 70f;

    /// <summary>How fast patience depletes per second during haggling.</summary>
    public float PatienceDecayRate { get; set; } = 2.0f;

    /// <summary>Extra patience decay when staff pushes too hard (offer > budget).</summary>
    public float OverBudgetDecayPenalty { get; set; } = 1.5f;

    // ── Preferences ────────────────────────────────────────────────
    public ClientPreference[] Preferences { get; set; } =
        { ClientPreference.Price, ClientPreference.Discretion };

    /// <summary>Room type the client prefers.</summary>
    public RoomType PreferredRoom { get; set; } = RoomType.Lounge;

    /// <summary>
    /// Decor school the client responds to. A room whose dominant
    /// <see cref="FurnitureStyle"/> matches earns a flat closing bonus —
    /// taste is cheaper to buy than tier.
    /// </summary>
    public FurnitureStyle PreferredStyle { get; set; } = FurnitureStyle.Modern;

    /// <summary>
    /// Room Appointment (0–100) the client considers par for the money.
    /// Rooms above it close more easily; rooms below it cost you the deal.
    /// </summary>
    public float ExpectedAppointment { get; set; } = 45f;

    /// <summary>How much risk/heat the client tolerates (0–100).</summary>
    public float RiskTolerance { get; set; } = 30f;

    /// <summary>Multiplier for luxury preference: higher = pays more for luxury rooms.</summary>
    public float LuxuryPreferenceMultiplier { get; set; } = 1.0f;

    /// <summary>Discount expectation for price-sensitive clients.</summary>
    public float ExpectedDiscount { get; set; }

    public override string ToString() =>
        $"{Name} | Budget=${Budget:F0} Fair=${FairPrice:F0} " +
        $"Patience={Patience:F0} Prefs=[{string.Join(",", Preferences)}]";
}

/// <summary>Result of a single negotiation round.</summary>
public struct NegotiationResult
{
    public bool AdvanceState;
    public NegotiationState NewState;
    public string Message;

    public static NegotiationResult Continue(string msg) =>
        new() { AdvanceState = false, Message = msg };

    public static NegotiationResult Transition(NegotiationState state, string msg) =>
        new() { AdvanceState = true, NewState = state, Message = msg };
}

// ── ClientNegotiationHandler Node ──────────────────────────────────────

/// <summary>
/// Handles dynamic client negotiations with a real-time haggling
/// state machine. Clients have Budget, Patience, and Preferences.
/// Success probability is computed from assigned StaffMember Charisma
/// and Negotiation stats against client profile factors.
///
/// Usage: Add as a Node. Call StartNegotiation() with a ClientProfile
///   and StaffMember, then use MakeOffer() / AcceptOffer() to advance
///   the state machine.
/// </summary>
public partial class ClientNegotiationHandler : Node
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a negotiation changes state.</summary>
    [Signal]
    public delegate void OnNegotiationStateChangedEventHandler(
        string clientName, int oldState, int newState, string message);

    /// <summary>Fired when a negotiation round completes (offer made).</summary>
    [Signal]
    public delegate void OnOfferMadeEventHandler(
        string clientName, double amount, float patienceRemaining);

    /// <summary>Fired when a deal is closed (Accepted state).</summary>
    [Signal]
    public delegate void OnDealClosedEventHandler(
        string clientName, double finalPrice, float staffCharisma, float staffNegotiation);

    /// <summary>Fired when a client walks away (patience = 0).</summary>
    [Signal]
    public delegate void OnClientWalkedAwayEventHandler(
        string clientName, float patience);

    // ── Active Negotiation State ───────────────────────────────────────

    private ClientProfile _activeClient;
    private StaffMember _activeStaff;
    private RoomModule _activeRoom;
    private NegotiationState _state = NegotiationState.Idle;
    private readonly List<NegotiationRound> _rounds = new();
    private double _currentOffer;
    private double _clientCounterOffer;
    private float _patienceRemaining;
    private bool _patienceTimerRunning;
    private double _patienceTimerAccumulator;

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Base probability of acceptance when offer = FairPrice.</summary>
    public float BaseAcceptProbability { get; set; } = 0.40f;

    /// <summary>Accept probability bonus per point of Charisma above 30.</summary>
    public float CharismaBonus { get; set; } = 0.005f;

    /// <summary>Accept probability bonus per point of Negotiation above 30.</summary>
    public float NegotiationBonus { get; set; } = 0.008f;

    /// <summary>Probability penalty per $1 the offer exceeds the client's FairPrice.</summary>
    public float PriceResistance { get; set; } = 0.002f;

    /// <summary>Bonus when offer matches a client preference.</summary>
    public float PreferenceMatchBonus { get; set; } = 0.08f;

    /// <summary>
    /// Probability swing at a full ±100 Appointment gap against the client's
    /// expectation. A room that beats what they were braced for makes the
    /// price feel fair; a shabby one makes the same number feel like a con.
    /// </summary>
    public float AppointmentInfluence { get; set; } = 0.35f;

    /// <summary>Bonus when the room's dominant style matches the client's taste.</summary>
    public float StyleMatchBonus { get; set; } = 0.10f;

    /// <summary>Maximum rounds before client auto-walks.</summary>
    public int MaxRounds { get; set; } = 6;

    // ── Read-only State ────────────────────────────────────────────────
    public NegotiationState CurrentState => _state;
    public ClientProfile ActiveClient => _activeClient;
    public StaffMember ActiveStaff => _activeStaff;

    /// <summary>Room being sold in this negotiation, or null if none was assigned.</summary>
    public RoomModule ActiveRoom => _activeRoom;

    public double CurrentOffer => _currentOffer;
    public double ClientCounterOffer => _clientCounterOffer;
    public float PatienceRemaining => _patienceRemaining;
    public int RoundCount => _rounds.Count;
    public IReadOnlyList<NegotiationRound> Rounds => _rounds;
    public bool IsActive => _state != NegotiationState.Idle &&
                            _state != NegotiationState.Accepted &&
                            _state != NegotiationState.Rejected &&
                            _state != NegotiationState.WalkedAway;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        GD.Print("[ClientNegotiation] Initialized.");
    }

    public override void _Process(double delta)
    {
        if (!_patienceTimerRunning) return;

        _patienceTimerAccumulator += delta;

        // Tick patience every second
        while (_patienceTimerAccumulator >= 1.0)
        {
            _patienceTimerAccumulator -= 1.0;
            TickPatience();
        }
    }

    // ── Negotiation Lifecycle ──────────────────────────────────────────

    /// <summary>
    /// Start a new negotiation with a client and assigned staff member.
    /// The room being sold is auto-selected from the venue against the
    /// client's <see cref="ClientProfile.PreferredRoom"/>; use the
    /// three-argument overload to pin a specific room.
    /// </summary>
    public bool StartNegotiation(ClientProfile client, StaffMember staff) =>
        StartNegotiation(client, staff, null);

    /// <summary>
    /// Start a negotiation over a specific room. Pass null for
    /// <paramref name="room"/> to let the venue pick the best match.
    /// </summary>
    public bool StartNegotiation(ClientProfile client, StaffMember staff, RoomModule room)
    {
        if (client == null || staff == null) return false;
        if (IsActive)
        {
            GD.PrintErr("[ClientNegotiation] Cannot start: negotiation already in progress.");
            return false;
        }

        _activeClient = client;
        _activeStaff = staff;
        _activeRoom = room ?? FindRoomForClient(client);
        _patienceRemaining = client.Patience;
        _rounds.Clear();
        _patienceTimerAccumulator = 0.0;
        _currentOffer = client.FairPrice;
        _clientCounterOffer = client.Budget * (1f - client.ExpectedDiscount);

        SetState(NegotiationState.Opening, $"Negotiation started with {client.Name}. " +
            $"Staff: {staff.StaffName} (Cha:{staff.Charisma:F0} Neg:{staff.Negotiation:F0})");

        GD.Print($"[ClientNegotiation] Started: {client}");
        return true;
    }

    /// <summary>
    /// Cancel the current negotiation (client leaves, no deal).
    /// </summary>
    public void CancelNegotiation()
    {
        if (_state == NegotiationState.Idle) return;
        SetState(NegotiationState.WalkedAway, "Negotiation cancelled.");
        ResetState();
    }

    // ── State Machine ──────────────────────────────────────────────────

    private void SetState(NegotiationState newState, string message)
    {
        var oldState = _state;
        _state = newState;

        EmitSignal(SignalName.OnNegotiationStateChanged,
            _activeClient?.Name ?? "", (int)oldState, (int)newState, message);

        GD.Print($"[ClientNegotiation] State: {oldState} → {newState} | {message}");

        // Start/stop patience timer based on state
        _patienceTimerRunning = newState is NegotiationState.Opening
                                               or NegotiationState.Haggling
                                               or NegotiationState.FinalOffer;
    }

    private void ResetState()
    {
        _activeClient = null;
        _activeStaff = null;
        _activeRoom = null;
        _state = NegotiationState.Idle;
        _rounds.Clear();
        _patienceTimerRunning = false;
        _patienceTimerAccumulator = 0.0;
    }

    // ── Patience Timer ─────────────────────────────────────────────────

    private void TickPatience()
    {
        if (!IsActive) return;

        // Extra decay if current offer exceeds client budget
        float decay = _activeClient.PatienceDecayRate;
        if (_currentOffer > _activeClient.Budget)
            decay += _activeClient.OverBudgetDecayPenalty;

        _patienceRemaining = Mathf.Clamp(_patienceRemaining - decay, 0f, 100f);

        // Patience exhausted = client walks
        if (_patienceRemaining <= 0f)
        {
            SetState(NegotiationState.WalkedAway,
                $"{_activeClient.Name} ran out of patience and left.");
            EmitSignal(SignalName.OnClientWalkedAway, _activeClient.Name, 0f);
            ResetState();
        }
    }

    // ── Offer Methods ──────────────────────────────────────────────────

    /// <summary>
    /// Staff makes an offer to the client.
    /// Evaluates probability and advances the state machine.
    /// </summary>
    public NegotiationResult MakeOffer(double amount)
    {
        if (!IsActive)
            return NegotiationResult.Continue("No active negotiation.");

        if (_state == NegotiationState.FinalOffer)
            return NegotiationResult.Continue("Already at final offer. Use AcceptOffer() or the client will walk.");

        _currentOffer = amount;

        _rounds.Add(new NegotiationRound
        {
            Direction = OfferDirection.StaffToClient,
            Amount = amount,
            PatienceRemaining = _patienceRemaining,
            Comment = $"Offered ${amount:F2}"
        });

        EmitSignal(SignalName.OnOfferMade, _activeClient.Name, amount, _patienceRemaining);

        // Calculate success probability
        float probability = CalculateAcceptProbability(amount);

        // Client response logic
        if (amount <= _activeClient.FairPrice)
        {
            // Under fair price — client accepts immediately
            CloseDeal(amount);
            return NegotiationResult.Transition(NegotiationState.Accepted,
                $"Offer ${amount:F2} below fair price ${_activeClient.FairPrice:F2}. Instant accept!");
        }
        else if (amount > _activeClient.Budget * 1.3f)
        {
            // Way over budget — client may walk immediately
            float walkChance = (float)((amount - _activeClient.Budget) / _activeClient.Budget);
            if (GD.Randf() < walkChance)
            {
                SetState(NegotiationState.Rejected,
                    $"Offer ${amount:F2} far exceeds budget ${_activeClient.Budget:F2}. Client refused.");
                ResetState();
                return NegotiationResult.Transition(NegotiationState.Rejected, "Client rejected excessive offer.");
            }

            // Otherwise, client counters
            _clientCounterOffer = _activeClient.Budget * (0.7f + GD.Randf() * 0.2f);
            AdvanceState();
            return NegotiationResult.Continue(
                $"Client counters with ${_clientCounterOffer:F2} (budget: ${_activeClient.Budget:F2})");
        }
        else
        {
            // Normal range — probability roll
            if (GD.Randf() < probability)
            {
                CloseDeal(amount);
                return NegotiationResult.Transition(NegotiationState.Accepted,
                    $"Deal accepted at ${amount:F2}! (probability: {probability:P0})");
            }

            // Client counters
            _clientCounterOffer = Math.Min(amount * (0.75f + GD.Randf() * 0.15f), _activeClient.Budget);
            AdvanceState();
            return NegotiationResult.Continue(
                $"Client counter-offers ${_clientCounterOffer:F2} (prob was {probability:P0})");
        }
    }

    /// <summary>
    /// Accept the client's counter-offer and close the deal.
    /// </summary>
    public NegotiationResult AcceptClientOffer()
    {
        if (!IsActive) return NegotiationResult.Continue("No active negotiation.");
        if (_clientCounterOffer <= 0) return NegotiationResult.Continue("No counter-offer on the table.");

        double finalPrice = _clientCounterOffer;
        CloseDeal(finalPrice);
        return NegotiationResult.Transition(NegotiationState.Accepted,
            $"Accepted client counter-offer: ${finalPrice:F2}");
    }

    /// <summary>
    /// Refuse the client's counter and hold firm on current offer.
    /// Transitions to FinalOffer state.
    /// </summary>
    public NegotiationResult HoldFirm()
    {
        if (!IsActive) return NegotiationResult.Continue("No active negotiation.");

        _rounds.Add(new NegotiationRound
        {
            Direction = OfferDirection.StaffToClient,
            Amount = _currentOffer,
            PatienceRemaining = _patienceRemaining,
            Comment = "Held firm on price."
        });

        // Penalty: holding firm costs extra patience
        _patienceRemaining -= 5f;

        if (_rounds.Count >= MaxRounds)
        {
            SetState(NegotiationState.WalkedAway,
                $"Maximum rounds ({MaxRounds}) reached. {_activeClient?.Name} walked.");
            EmitSignal(SignalName.OnClientWalkedAway, _activeClient?.Name ?? "", _patienceRemaining);
            ResetState();
            return NegotiationResult.Transition(NegotiationState.WalkedAway, "Max rounds reached.");
        }

        SetState(NegotiationState.FinalOffer,
            $"Final offer: ${_currentOffer:F2}. Next round is last chance.");
        return NegotiationResult.Transition(NegotiationState.FinalOffer, "This is the final offer.");
    }

    // ── Deal Closure ───────────────────────────────────────────────────

    private void CloseDeal(double finalPrice)
    {
        SetState(NegotiationState.Accepted,
            $"Deal closed at ${finalPrice:F2} with {_activeClient?.Name}");

        EmitSignal(SignalName.OnDealClosed,
            _activeClient?.Name ?? "",
            finalPrice,
            _activeStaff?.Charisma ?? 0f,
            _activeStaff?.Negotiation ?? 0f);

        // Apply financial result
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash += finalPrice;

        // Staff satisfaction: successful deal is satisfying
        if (_activeStaff != null)
            _activeStaff.Satisfaction += 2f;

        GD.Print($"[ClientNegotiation] Deal closed: ${finalPrice:F2}. " +
                 $"Rounds: {_rounds.Count}. " +
                 $"Staff satisfaction +2.");

        ResetState();
    }

    private void AdvanceState()
    {
        if (_rounds.Count >= MaxRounds)
        {
            SetState(NegotiationState.FinalOffer,
                $"Round {_rounds.Count}/{MaxRounds}. Final offer stage.");
        }
        else if (_state == NegotiationState.Opening)
        {
            SetState(NegotiationState.Haggling,
                $"Moving to haggling (round {_rounds.Count}/{MaxRounds}).");
        }
        // else stay in Haggling
    }

    // ── Success Probability Calculation ────────────────────────────────

    /// <summary>
    /// Calculate the probability (0.0–1.0) that the client will accept
    /// the given offer, based on staff stats, client profile, and price.
    /// </summary>
    public float CalculateAcceptProbability(double offer)
    {
        if (_activeClient == null || _activeStaff == null) return 0f;
        if (offer <= _activeClient.FairPrice) return 1.0f;
        if (offer > _activeClient.Budget * 1.5f) return 0.01f;

        // ── Base probability ─────────────────────────────────────────
        float prob = BaseAcceptProbability;

        // ── Staff stat bonuses ───────────────────────────────────────
        // Charisma: smooth talker, builds rapport
        float charismaFactor = Mathf.Max(0, _activeStaff.Charisma - 30f);
        prob += charismaFactor * CharismaBonus;

        // Negotiation: skilled haggler, finds win-win
        float negotiationFactor = Mathf.Max(0, _activeStaff.Negotiation - 30f);
        prob += negotiationFactor * NegotiationBonus;

        // ── Price resistance ─────────────────────────────────────────
        float overFair = (float)(offer - _activeClient.FairPrice);
        prob -= overFair * PriceResistance;

        // ── Budget pressure ──────────────────────────────────────────
        float budgetRatio = (float)(offer / _activeClient.Budget);
        if (budgetRatio > 1.0f)
            prob -= (budgetRatio - 1.0f) * 0.3f;

        // ── Patience factor ──────────────────────────────────────────
        // Impatient clients are harder to close with
        float patienceRatio = _patienceRemaining / 100f;
        prob *= 0.5f + (patienceRatio * 0.5f);

        // ── Preference matching ──────────────────────────────────────
        if (_activeClient.Preferences != null)
        {
            foreach (var pref in _activeClient.Preferences)
            {
                switch (pref)
                {
                    case ClientPreference.Luxury:
                        // Luxury-preferring clients pay more if staff has high Charisma
                        if (_activeStaff.Charisma > 60f)
                            prob += PreferenceMatchBonus;
                        break;

                    case ClientPreference.Discretion:
                        // Discretion-preferring clients value staff Discretion
                        if (_activeStaff.Discretion > 60f)
                            prob += PreferenceMatchBonus;
                        break;

                    case ClientPreference.Price:
                        // Price-sensitive: hard to close above fair price
                        if (offer > _activeClient.FairPrice * 1.2f)
                            prob -= PreferenceMatchBonus;
                        break;

                    case ClientPreference.Speed:
                        // Speed-preferring: patience draining faster hurts
                        prob -= (1f - patienceRatio) * 0.1f;
                        break;

                    case ClientPreference.Exclusivity:
                        // Exclusive clients: high Negotiation impresses them
                        if (_activeStaff.Negotiation > 70f)
                            prob += PreferenceMatchBonus * 1.5f;
                        break;
                }
            }
        }

        // ── Room appointment ─────────────────────────────────────────
        // The room is half the product. A suite that beats what the client
        // was braced for makes the asking price read as fair; a bare shell
        // makes the same number read as a con. LuxuryPreferenceMultiplier
        // scales how much the client cares either way.
        if (_activeRoom != null)
        {
            float appointment = RoomAppointmentCalculator.Calculate(_activeRoom);
            float gap = (appointment - _activeClient.ExpectedAppointment) / 100f;  // −1..+1
            prob += gap * AppointmentInfluence * _activeClient.LuxuryPreferenceMultiplier;

            // Preferred room type delivered as asked.
            if (_activeRoom.Type == _activeClient.PreferredRoom)
                prob += PreferenceMatchBonus * 0.5f;

            // ── Style match ──────────────────────────────────────────
            // Taste is cheaper than tier: a coherent budget room in the
            // client's school can out-close an expensive mismatched one.
            if (_activeRoom.Furniture.Count > 0)
            {
                var decor = RoomAppointmentCalculator.GetBreakdown(_activeRoom);
                if (decor.DominantStyle == _activeClient.PreferredStyle)
                    prob += StyleMatchBonus * _activeClient.LuxuryPreferenceMultiplier;
            }
        }

        // ── Risk tolerance ───────────────────────────────────────────
        // Low risk-tolerance clients are more anxious in high-heat venues
        if (GameStateManager.Instance != null)
        {
            float heat = GameStateManager.Instance.Heat;
            float riskDiscomfort = Mathf.Max(0, heat - _activeClient.RiskTolerance) / 100f;
            prob -= riskDiscomfort * 0.15f;
        }

        // ── Clamp ────────────────────────────────────────────────────
        return Mathf.Clamp(prob, 0.01f, 0.99f);
    }

    // ── Venue Lookup ───────────────────────────────────────────────────

    /// <summary>The building node, or null if it is not in the tree.</summary>
    private VenueBuilding GetVenue() =>
        GetTree()?.Root?.FindChild("VenueBuilding", recursive: true, owned: false) as VenueBuilding;

    /// <summary>
    /// Pick the room this client will actually be sold, honouring their
    /// preferred room type. Returns null when there is no venue in the tree
    /// (tests, headless sims) — the Appointment terms then sit out.
    /// </summary>
    private RoomModule FindRoomForClient(ClientProfile client) =>
        GetVenue()?.GetBestRoomForClient(client.PreferredRoom);

    /// <summary>
    /// Get a breakdown of factors contributing to the current success probability.
    /// </summary>
    public string GetProbabilityBreakdown(double offer)
    {
        if (_activeClient == null || _activeStaff == null) return "No active negotiation.";

        float prob = CalculateAcceptProbability(offer);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"=== Success Probability: {prob:P1} ===");
        sb.AppendLine($"Client: {_activeClient.Name} (Budget=${_activeClient.Budget:F0})");
        sb.AppendLine($"Staff: {_activeStaff.StaffName} " +
                      $"(Cha:{_activeStaff.Charisma:F0} Neg:{_activeStaff.Negotiation:F0})");
        sb.AppendLine($"Offer: ${offer:F2} (Fair: ${_activeClient.FairPrice:F2})");
        sb.AppendLine($"Patience: {_patienceRemaining:F0}/100");
        sb.AppendLine($"Rounds: {_rounds.Count}/{MaxRounds}");
        sb.AppendLine($"Preferences: [{string.Join(", ", _activeClient.Preferences)}]");

        // ── Room terms ───────────────────────────────────────────────
        if (_activeRoom == null)
        {
            sb.AppendLine($"Room: none assigned — Appointment and style terms inactive " +
                          $"(wanted {_activeClient.PreferredRoom}).");
        }
        else
        {
            var decor = RoomAppointmentCalculator.GetBreakdown(_activeRoom);
            float gap = decor.Appointment - _activeClient.ExpectedAppointment;
            float appointmentTerm = gap / 100f * AppointmentInfluence *
                                    _activeClient.LuxuryPreferenceMultiplier;

            sb.AppendLine($"Room: {_activeRoom.RoomName} ({_activeRoom.Type}, floor {_activeRoom.Floor})");
            sb.AppendLine($"  Appointment: {decor.Appointment:F1} vs expected " +
                          $"{_activeClient.ExpectedAppointment:F0} " +
                          $"→ {appointmentTerm:+0.0%;-0.0%}");
            sb.AppendLine($"    Coverage {decor.Coverage:F0} | Quality {decor.Quality:F0} | " +
                          $"Coherence {decor.Coherence:F0} | Condition ×{decor.ConditionMultiplier:F2}");

            if (_activeRoom.Type == _activeClient.PreferredRoom)
                sb.AppendLine($"  Preferred room type matched → {PreferenceMatchBonus * 0.5f:+0.0%}");
            else
                sb.AppendLine($"  Preferred room type was {_activeClient.PreferredRoom} → no bonus");

            if (_activeRoom.Furniture.Count == 0)
            {
                sb.AppendLine("  Style: room is unfurnished → no style bonus");
            }
            else if (decor.DominantStyle == _activeClient.PreferredStyle)
            {
                sb.AppendLine($"  Style: {decor.DominantStyle} matches taste → " +
                              $"{StyleMatchBonus * _activeClient.LuxuryPreferenceMultiplier:+0.0%}");
            }
            else
            {
                sb.AppendLine($"  Style: room reads {decor.DominantStyle}, " +
                              $"client wants {_activeClient.PreferredStyle} → no bonus");
            }
        }

        return sb.ToString();
    }

    // ── Quick Client Factory ───────────────────────────────────────────

    // ── Names ──────────────────────────────────────────────────────────

    /// <summary>
    /// The stream every generated client is drawn from. Created lazily so it
    /// picks up the world seed, which is fixed after the static initializer
    /// would otherwise have run.
    /// </summary>
    private static RandomNumberGenerator ClientRng
    {
        get
        {
            if (_clientRng != null && _clientGeneration == WorldRandom.Generation)
                return _clientRng;

            _clientGeneration = WorldRandom.Generation;
            return _clientRng = WorldRandom.Stream(nameof(GenerateRandomClient));
        }
    }

    private static RandomNumberGenerator _clientRng;
    private static int _clientGeneration = -1;

    /// <summary>
    /// Honorifics, weighted by how they read. Most clients arrive with a
    /// title and a surname; the plainer forms are commonest because the house
    /// should feel like it serves a city, not a costume party.
    /// </summary>
    private static readonly string[] Honorifics =
    {
        "Mr.", "Mr.", "Mr.", "Mrs.", "Ms.", "Ms.", "Madame", "Madame",
        "Dr.", "Dr.", "Captain", "Colonel", "Father", "Judge",
        "Lady", "Lord", "Baron", "Baroness", "Councillor", "Inspector"
    };

    private static readonly string[] Surnames =
    {
        "Sterling", "Ashford", "Vex", "Nyx", "Calloway", "Rennick", "Thorne",
        "Wexley", "Marlowe", "Delacroix", "Fenwick", "Okonkwo", "Sable",
        "Ravensworth", "Blackwood", "Ivanova", "Castellane", "Duval",
        "Hargrave", "Petrossian", "Quill", "Ashgrove", "Vandermeer",
        "Lindqvist", "Moreau", "Castellan", "Beaumont", "Kowalski",
        "Ferro", "Draycott", "Yusupov", "Mercer", "Halloran", "Osei",
        "Winterbourne", "Sagara", "Voss", "Bellweather", "Crane", "Amari",
        "Rothery", "Nakamura", "Sorrel", "Ballantine", "Whitlock", "Rey",
        "Dumont", "Fairweather", "Achterberg", "Costa"
    };

    /// <summary>
    /// The ones who do not give a name. Rarer than the titled clients, and
    /// more memorable for it — these are the entries a player recognises in
    /// the book six nights later.
    /// </summary>
    private static readonly string[] Aliases =
    {
        "The Broker", "The Professor", "Big Tony", "Slick Rick", "The Widow",
        "The Cartographer", "Little Mercy", "The Auditor", "Grey Sunday",
        "The Sommelier", "Uncle Bess", "The Understudy", "The Nightjar",
        "Doctor Nobody", "The Second Son", "Quiet Ilse", "The Collector",
        "Handsome Otto", "The Envoy", "Saint Kaspar"
    };

    /// <summary>
    /// A client's name.
    ///
    /// This used to draw from a list of eight, with replacement, which meant
    /// the patrons book routinely showed the same name as two different
    /// people. The book is built on the premise of recognising someone, and
    /// that premise does not survive a collision. Roughly a thousand
    /// combinations now, so a repeat inside one campaign is a curiosity
    /// rather than the norm.
    /// </summary>
    private static string GenerateClientName(RandomNumberGenerator rng)
    {
        // Roughly one client in six arrives under a name they made up.
        if (rng.Randi() % 6 == 0) return Aliases[rng.Randi() % Aliases.Length];

        return $"{Honorifics[rng.Randi() % Honorifics.Length]} " +
               $"{Surnames[rng.Randi() % Surnames.Length]}";
    }

    /// <summary>Generate a random client profile for testing.</summary>
    public static ClientProfile GenerateRandomClient()
    {
        // One long-lived stream rather than a freshly randomized generator per
        // call — a new Randomize() every client made this the single largest
        // source of irreproducibility in the whole simulation.
        var rng = ClientRng;

        var allPrefs = (ClientPreference[])Enum.GetValues(typeof(ClientPreference));
        var prefs = allPrefs.OrderBy(_ => rng.Randi()).Take((int)(rng.Randi() % 3) + 1).ToArray();

        var rooms = new[] { RoomType.Lounge, RoomType.VIPSuite, RoomType.PrivateSuite, RoomType.Bar };
        var styles = Enum.GetValues<FurnitureStyle>();

        return new ClientProfile
        {
            Name = GenerateClientName(rng),
            Description = "A randomly generated client.",
            Budget = 100 + rng.Randi() % 400,
            FairPrice = 50 + rng.Randi() % 150,
            Patience = 30 + rng.Randf() * 70f,
            PatienceDecayRate = 1.0f + rng.Randf() * 3.0f,
            OverBudgetDecayPenalty = 1.0f + rng.Randf() * 2.0f,
            Preferences = prefs,
            PreferredRoom = rooms[rng.Randi() % rooms.Length],
            PreferredStyle = styles[rng.Randi() % (uint)styles.Length],
            ExpectedAppointment = 20f + rng.Randf() * 60f,
            RiskTolerance = rng.Randf() * 80f,
            LuxuryPreferenceMultiplier = 0.8f + rng.Randf() * 0.4f,
            ExpectedDiscount = rng.Randf() * 0.2f
        };
    }

    public override string ToString()
    {
        return $"[ClientNegotiation] State={_state} Client={_activeClient?.Name ?? "none"} " +
               $"Staff={_activeStaff?.StaffName ?? "none"} Rounds={_rounds.Count}";
    }
}
