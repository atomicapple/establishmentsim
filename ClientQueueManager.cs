using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── Domain Types ───────────────────────────────────────────────────────

public enum ClientTier { LowTier, MidTier, VIPTier }

public class QueuedClient
{
    public string Name { get; set; }
    public ClientTier Tier { get; set; }
    public double Budget { get; set; }
    public float Patience { get; set; } = 100f;
    public float FightRisk { get; set; }
    public float DiscretionRequirement { get; set; }

    /// <summary>
    /// Room Appointment (0–100) this client will not go below. A VIP will
    /// walk out of a bare room no matter how discreet it is.
    /// </summary>
    public float AppointmentRequirement { get; set; }

    /// <summary>Room this client was dispatched into, for wear and reporting.</summary>
    public Vector3I? AssignedRoom { get; set; }

    public bool IsServed { get; set; }
    public bool Exited { get; set; }

    public override string ToString() =>
        $"[{Tier}] {Name} Budget=${Budget:F0} Patience={Patience:F0} " +
        $"NeedsAppt={AppointmentRequirement:F0}";
}

/// <summary>
/// Manages patron arrival rates, client tiering, room dispatch,
/// and exit logging. Client footfall probability scales with
/// Venue Prestige and District Marketing Spend.
/// </summary>
public partial class ClientQueueManager : Node
{
    [Signal] public delegate void OnClientArrivedEventHandler(string name, int tier);
    [Signal] public delegate void OnClientDispatchedEventHandler(string name, string roomType);
    [Signal] public delegate void OnClientExitedEventHandler(string name, string reason);
    [Signal] public delegate void OnFightTriggeredEventHandler(string clientName, float damage);

    private readonly List<QueuedClient> _queue = new();
    private readonly List<QueuedClient> _history = new();
    private readonly RandomNumberGenerator _rng = new();
    private int _roomsAvailable = 3;
    private double _marketingSpend;

    // ── Configuration ──────────────────────────────────────────────────

    public double MarketingSpend
    {
        get => _marketingSpend;
        set { _marketingSpend = Math.Max(0, value); }
    }

    public float PrestigeBonus { get; set; } = 1.0f;
    public int MaxQueueSize { get; set; } = 12;
    public int AvailableRooms { get => _roomsAvailable; set => _roomsAvailable = Math.Max(0, value); }
    public float FightProbabilityBase { get; set; } = 0.02f;

    /// <summary>
    /// Share of a client's budget a neutral (Appointment 50, ground floor)
    /// room extracts. Room quality scales up from here.
    /// </summary>
    public float BaseTicketShare { get; set; } = 0.7f;

    // Tier weights for arrival probability
    public float LowTierWeight { get; set; } = 0.55f;
    public float MidTierWeight { get; set; } = 0.30f;
    public float VIPTierWeight { get; set; } = 0.15f;

    public IReadOnlyList<QueuedClient> Queue => _queue;
    public int TotalServed { get; private set; }
    public int TotalExited { get; private set; }

    public override void _Ready()
    {
        _rng.Randomize();
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        GD.Print("[ClientQueue] Initialized.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    // ── Daily Tick ─────────────────────────────────────────────────────

    private void OnDailyTick(double cash, float reputation, float heat, float sentiment)
    {
        // Decrease patience of waiting clients
        foreach (var client in _queue.Where(c => !c.IsServed))
        {
            client.Patience -= 5f + (_rng.Randf() * 10f);
            if (client.Patience <= 0)
                ExitClient(client, "patience exhausted");
        }

        // Generate new arrivals
        GenerateArrivals(reputation);

        // Dispatch queued clients to rooms
        DispatchClients();

        // Check for fights among unserved low-tier clients
        CheckFights();
    }

    // ── Arrival Generation ─────────────────────────────────────────────

    private void GenerateArrivals(float reputation)
    {
        if (_queue.Count(c => !c.IsServed) >= MaxQueueSize) return;

        // Footfall probability = base + prestige bonus + marketing
        float prestigeFactor = (reputation / 100f) * PrestigeBonus;
        float marketingFactor = (float)(_marketingSpend / 500.0);
        float footfallProb = Mathf.Clamp(0.15f + prestigeFactor * 0.3f + marketingFactor * 0.2f, 0.05f, 0.95f);

        // How many arrivals this tick? 0-3 based on probability
        int arrivals = 0;
        for (int i = 0; i < 3; i++)
            if (_rng.Randf() < footfallProb) arrivals++;

        for (int i = 0; i < arrivals; i++)
        {
            if (_queue.Count >= MaxQueueSize) break;

            var tier = RollClientTier();
            var client = GenerateClient(tier);
            _queue.Add(client);
            EmitSignal(SignalName.OnClientArrived, client.Name, (int)tier);
        }
    }

    private ClientTier RollClientTier()
    {
        float roll = _rng.Randf();
        if (roll < VIPTierWeight) return ClientTier.VIPTier;
        if (roll < VIPTierWeight + MidTierWeight) return ClientTier.MidTier;
        return ClientTier.LowTier;
    }

    private QueuedClient GenerateClient(ClientTier tier)
    {
        var names = new[] { "Patron", "Guest", "Customer", "Visitor", "Regular", "New Face" };
        var name = $"{names[(int)(_rng.Randi() % (uint)names.Length)]}_{_rng.Randi() % 1000}";

        return tier switch
        {
            ClientTier.VIPTier => new QueuedClient
            {
                Name = name, Tier = tier,
                Budget = 500 + _rng.Randi() % 1500,
                FightRisk = 0.01f,
                DiscretionRequirement = 60f + _rng.Randf() * 40f,
                AppointmentRequirement = 60f + _rng.Randf() * 20f
            },
            ClientTier.MidTier => new QueuedClient
            {
                Name = name, Tier = tier,
                Budget = 100 + _rng.Randi() % 400,
                FightRisk = 0.08f,
                DiscretionRequirement = 30f + _rng.Randf() * 30f,
                AppointmentRequirement = 30f + _rng.Randf() * 20f
            },
            _ => new QueuedClient
            {
                Name = name, Tier = tier,
                Budget = 20 + _rng.Randi() % 80,
                FightRisk = 0.25f,
                DiscretionRequirement = 0f,
                AppointmentRequirement = 5f + _rng.Randf() * 10f
            }
        };
    }

    // ── Room Dispatch ──────────────────────────────────────────────────

    /// <summary>The building node, or null when running headless.</summary>
    private VenueBuilding GetVenue() =>
        GetTree()?.Root?.FindChild("VenueBuilding", recursive: true, owned: false) as VenueBuilding;

    /// <summary>
    /// Match waiting clients to actual rooms. A client is gated on the
    /// room's Appointment against their expectation — not on a raw
    /// DiscretionRating that furniture never touched — and each room takes
    /// one client per night.
    ///
    /// Rooms are assigned worst-adequate-first so a VIP-grade suite is not
    /// burned on a low-tier walk-in while a VIP is still queueing.
    /// </summary>
    private void DispatchClients()
    {
        var unserved = _queue.Where(c => !c.IsServed && !c.Exited).ToList();
        var venue = GetVenue();

        // Rooms that can actually take a paying client tonight. Basement
        // rooms are excluded by the venue — they generate no revenue.
        var freeRooms = venue == null
            ? new List<RoomModule>()
            : venue.Rooms.Values
                .Where(venue.IsRevenueGenerating)
                .OrderBy(r => RoomAppointmentCalculator.Calculate(r))
                .ToList();

        if (venue != null)
            AvailableRooms = freeRooms.Count;

        int dispatched = 0;

        // VIPs first, then mid, then low
        foreach (var tier in new[] { ClientTier.VIPTier, ClientTier.MidTier, ClientTier.LowTier })
        {
            foreach (var client in unserved.Where(c => c.Tier == tier))
            {
                if (dispatched >= _roomsAvailable) break;

                RoomModule room = null;

                if (venue != null)
                {
                    // Cheapest room that still clears both bars.
                    room = freeRooms.FirstOrDefault(r =>
                        RoomAppointmentCalculator.Calculate(r) >= client.AppointmentRequirement &&
                        venue.GetEffectiveDiscretion(r.GridPosition) >= client.DiscretionRequirement);

                    if (room == null) continue;
                    freeRooms.Remove(room);
                    client.AssignedRoom = room.GridPosition;
                }

                client.IsServed = true;
                TotalServed++;
                dispatched++;

                // ── Revenue ────────────────────────────────────────────
                // The ticket is the client's baseline spend scaled by what
                // the room is actually worth: Appointment, floor prestige,
                // and ground-floor upselling. Never above their budget.
                double baseTicket = client.Budget * BaseTicketShare;
                double revenue = venue == null || room == null
                    ? client.Budget
                    : Math.Min(client.Budget, venue.GetSuggestedPrice(room, baseTicket));

                if (GameStateManager.Instance != null)
                    GameStateManager.Instance.Cash += revenue;

                // A night's use wears the furnishings.
                if (venue != null && room != null)
                    venue.ApplyRoomUsage(room.GridPosition, 1f);

                EmitSignal(SignalName.OnClientDispatched,
                    client.Name, room?.RoomName ?? tier.ToString());
            }
        }

        // Exit overflow clients
        foreach (var client in unserved.Where(c => !c.IsServed && !c.Exited))
        {
            if (client.Patience <= 30f)
                ExitClient(client, "no suitable room available");
        }
    }

    // ── Fight Logic ────────────────────────────────────────────────────

    private void CheckFights()
    {
        var lowTiers = _queue.Where(c => c.Tier == ClientTier.LowTier && !c.IsServed).ToList();
        if (lowTiers.Count < 2) return;

        float fightChance = FightProbabilityBase * lowTiers.Count;
        if (_rng.Randf() < fightChance)
        {
            var instigator = lowTiers[(int)(_rng.Randi() % (uint)lowTiers.Count)];
            float damage = 50 + _rng.Randf() * 200;
            EmitSignal(SignalName.OnFightTriggered, instigator.Name, damage);

            // Reputation hit
            if (GameStateManager.Instance != null)
                GameStateManager.Instance.Reputation -= 2f;

            ExitClient(instigator, "fight");
        }
    }

    private void ExitClient(QueuedClient client, string reason)
    {
        client.Exited = true;
        TotalExited++;
        _history.Add(client);
        _queue.Remove(client);
        EmitSignal(SignalName.OnClientExited, client.Name, reason);
    }

    public override string ToString() =>
        $"[ClientQueue] Queue={_queue.Count} Rooms={_roomsAvailable} Served={TotalServed} Exited={TotalExited}";
}
