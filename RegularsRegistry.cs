using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>How well the house knows a client.</summary>
public enum PatronStanding
{
    /// <summary>Seen once. May never come back.</summary>
    FirstTime,

    /// <summary>Comes back. Worth remembering what they like.</summary>
    Regular,

    /// <summary>A fixture. Spends freely, and has a great deal to lose.</summary>
    Patron
}

/// <summary>
/// A client the house has served before.
///
/// Their preferences persist, which is the point: a Regular who was matched
/// to the right room once expects it again, and disappointing someone who
/// keeps coming back costs more than disappointing a stranger.
/// </summary>
public class Patron
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "Unknown";

    public int Visits { get; set; }
    public double TotalSpend { get; set; }

    /// <summary>Rolling mean of how their nights have gone, 0–100.</summary>
    public float Satisfaction { get; set; } = 50f;

    public int DayFirstSeen { get; set; }
    public int DayLastSeen { get; set; }

    // Remembered preferences, so a return visit expects what they liked.
    public RoomType PreferredRoom { get; set; } = RoomType.PrivateSuite;
    public FurnitureStyle PreferredStyle { get; set; } = FurnitureStyle.Modern;
    public float ExpectedAppointment { get; set; } = 45f;
    public double Budget { get; set; } = 200.0;
    public double FairPrice { get; set; } = 100.0;

    /// <summary>Whether an Informant has already taken something from them.</summary>
    public bool IntelGathered { get; set; }

    /// <summary>Nights they have not shown up. High values mean they are gone.</summary>
    public int NightsAbsent { get; set; }

    public PatronStanding Standing =>
        Visits >= RegularsRegistry.PatronVisitThreshold ? PatronStanding.Patron :
        Visits >= RegularsRegistry.RegularVisitThreshold ? PatronStanding.Regular :
        PatronStanding.FirstTime;

    /// <summary>Average spend per visit, the honest measure of their worth.</summary>
    public double AverageSpend => Visits <= 0 ? 0 : TotalSpend / Visits;

    public override string ToString() =>
        $"{Name} — {Standing}, {Visits} visits, ${TotalSpend:F0} total";
}

/// <summary>
/// Remembers clients across nights and brings them back.
///
/// This is the piece that closes the game's main loop. Until now a satisfied
/// client set a flag that incremented a counter on the nightly report and was
/// then discarded, so quality of service had no compounding consequence and
/// BlackmailNetwork — fully implemented — had no supply of targets worth
/// blackmailing.
///
/// With regulars in place the chain runs end to end:
/// appointment and staff quality raise satisfaction, satisfaction produces
/// returning clients, returning clients spend more and become Patrons,
/// Patrons are what an Informant can gather intel on, intel buys political
/// favour, favour unlocks the permits that let the house grow.
/// </summary>
public partial class RegularsRegistry : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnRegularGainedEventHandler(string patronId, string name);

    [Signal]
    public delegate void OnPatronGainedEventHandler(string patronId, string name);

    [Signal]
    public delegate void OnPatronLostEventHandler(string patronId, string name, string reason);

    // ── Tuning ─────────────────────────────────────────────────────────

    /// <summary>Visits before a client counts as a Regular.</summary>
    public const int RegularVisitThreshold = 2;

    /// <summary>Visits before a Regular becomes a Patron.</summary>
    public const int PatronVisitThreshold = 5;

    /// <summary>Base chance a Regular returns on a given night.</summary>
    [Export] public float BaseReturnChance { get; set; } = 0.35f;

    /// <summary>Extra return chance at full satisfaction.</summary>
    [Export] public float SatisfactionReturnBonus { get; set; } = 0.40f;

    /// <summary>Budget multiplier a Patron brings over a stranger.</summary>
    [Export] public float PatronBudgetMultiplier { get; set; } = 1.55f;

    /// <summary>Budget multiplier a Regular brings.</summary>
    [Export] public float RegularBudgetMultiplier { get; set; } = 1.2f;

    /// <summary>
    /// Consecutive missed nights before a Regular is written off. Patrons are
    /// given twice as long — a fixture does not vanish over one quiet week.
    /// </summary>
    [Export] public int AbsenceLimit { get; set; } = 14;

    /// <summary>Satisfaction below which a client never returns at all.</summary>
    [Export] public float AbandonSatisfaction { get; set; } = 22f;

    // ── State ──────────────────────────────────────────────────────────

    private readonly Dictionary<string, Patron> _patrons = new();
    private readonly RandomNumberGenerator _rng = new();

    /// <summary>
    /// Who has already been through the door tonight. The return roll runs
    /// once per arrival, so without this the same person could be drawn
    /// several times in an evening — one patron reached nineteen visits in
    /// thirteen nights, which is not a regular, it is a duplicate.
    /// </summary>
    private readonly HashSet<string> _seenTonight = new();

    public IReadOnlyCollection<Patron> All => _patrons.Values;

    public int RegularCount => _patrons.Values.Count(p => p.Standing == PatronStanding.Regular);
    public int PatronCount => _patrons.Values.Count(p => p.Standing == PatronStanding.Patron);

    /// <summary>Patrons an Informant has not yet taken anything from.</summary>
    public List<Patron> GetIntelTargets() =>
        _patrons.Values
            .Where(p => p.Standing == PatronStanding.Patron && !p.IntelGathered)
            .OrderByDescending(p => p.TotalSpend)
            .ToList();

    public override void _Ready()
    {
        _rng.Randomize();
        GD.Print("[Regulars] Ready.");
    }

    // ── Recording visits ───────────────────────────────────────────────

    /// <summary>
    /// Record a completed encounter against a client, creating their record
    /// on a first visit and promoting them as the count rises.
    /// </summary>
    /// <param name="patronId">
    /// Existing patron id when this was a return visit, or null for a stranger.
    /// </param>
    /// <returns>The patron record, or null when the client did not return.</returns>
    public Patron RecordVisit(
        string patronId, ClientProfile client, double spend, float satisfaction, bool wantsToReturn)
    {
        var day = GameStateManager.Instance?.DayCount ?? 0;

        Patron patron = null;
        var isNew = false;

        if (!string.IsNullOrEmpty(patronId)) _patrons.TryGetValue(patronId, out patron);

        if (patron == null)
        {
            // A stranger who did not enjoy themselves is simply forgotten.
            if (!wantsToReturn) return null;

            patron = new Patron
            {
                Name = client?.Name ?? "Unknown",
                DayFirstSeen = day,
                PreferredRoom = client?.PreferredRoom ?? RoomType.PrivateSuite,
                PreferredStyle = client?.PreferredStyle ?? FurnitureStyle.Modern,
                ExpectedAppointment = client?.ExpectedAppointment ?? 45f,
                Budget = client?.Budget ?? 200.0,
                FairPrice = client?.FairPrice ?? 100.0
            };

            isNew = true;
        }

        var previousStanding = patron.Standing;

        patron.Visits++;
        patron.TotalSpend += spend;
        patron.DayLastSeen = day;
        patron.NightsAbsent = 0;

        // Rolling mean, weighted toward recent nights so a run of bad service
        // drives someone away rather than being diluted by old good visits.
        patron.Satisfaction = Mathf.Lerp(patron.Satisfaction, satisfaction, 0.45f);

        // Expectations creep upward as they get used to the place.
        patron.ExpectedAppointment = Mathf.Min(95f, patron.ExpectedAppointment + 1.5f);

        if (isNew) _patrons[patron.Id] = patron;

        AnnouncePromotion(patron, previousStanding, isNew);
        return patron;
    }

    private void AnnouncePromotion(Patron patron, PatronStanding previous, bool isNew)
    {
        var standing = patron.Standing;
        if (!isNew && standing == previous) return;

        switch (standing)
        {
            case PatronStanding.Regular when previous != PatronStanding.Regular:
                EmitSignal(SignalName.OnRegularGained, patron.Id, patron.Name);
                GD.Print($"[Regulars] {patron.Name} is becoming a regular.");
                break;

            case PatronStanding.Patron when previous != PatronStanding.Patron:
                EmitSignal(SignalName.OnPatronGained, patron.Id, patron.Name);
                GD.Print($"[Regulars] {patron.Name} is now a patron of the house " +
                         $"({patron.Visits} visits, ${patron.TotalSpend:F0}).");
                break;
        }
    }

    // ── Return visits ──────────────────────────────────────────────────

    /// <summary>
    /// Choose a known client to walk back through the door tonight, or null
    /// for a stranger. Happier clients return more often, and Patrons more
    /// often than Regulars.
    /// </summary>
    public Patron RollReturningClient()
    {
        // Anyone in the book is a candidate, including someone seen only once.
        //
        // Filtering to Standing != FirstTime was a chicken-and-egg bug: a
        // client needs two visits to stop being FirstTime, but they can only
        // get a second visit by being picked here. Nobody could ever be
        // promoted, so across a dozen nights the book stayed empty and the
        // entire regulars → patrons → intel chain never started.
        var candidates = _patrons.Values
            .Where(p => p.Satisfaction > AbandonSatisfaction)
            .Where(p => !_seenTonight.Contains(p.Id))
            .ToList();

        if (candidates.Count == 0) return null;

        // Shuffled so the same person is not always first in line.
        foreach (var patron in candidates.OrderBy(_ => _rng.Randf()))
        {
            var satisfaction = Mathf.Clamp(patron.Satisfaction / 100f, 0f, 1f);
            var chance = BaseReturnChance + satisfaction * SatisfactionReturnBonus;

            // Standing raises the odds, so the book concentrates over time
            // rather than churning through one-off visitors.
            chance += patron.Standing switch
            {
                PatronStanding.Patron => 0.15f,
                PatronStanding.Regular => 0.08f,
                _ => 0f
            };

            if (_rng.Randf() >= chance) continue;

            _seenTonight.Add(patron.Id);
            return patron;
        }

        return null;
    }

    /// <summary>
    /// Build tonight's client profile for a returning patron, carrying their
    /// remembered preferences and the larger purse their standing brings.
    /// </summary>
    public ClientProfile BuildProfile(Patron patron)
    {
        if (patron == null) return null;

        var multiplier = patron.Standing switch
        {
            PatronStanding.Patron => PatronBudgetMultiplier,
            PatronStanding.Regular => RegularBudgetMultiplier,
            _ => 1f
        };

        var profile = ClientNegotiationHandler.GenerateRandomClient();

        profile.Name = patron.Name;
        profile.PreferredRoom = patron.PreferredRoom;
        profile.PreferredStyle = patron.PreferredStyle;
        profile.ExpectedAppointment = patron.ExpectedAppointment;
        profile.Budget = patron.Budget * multiplier;
        profile.FairPrice = patron.FairPrice * multiplier;

        // Someone who already trusts the house haggles less.
        profile.Patience = Mathf.Min(100f, profile.Patience + patron.Visits * 2f);

        return profile;
    }

    // ── Upkeep ─────────────────────────────────────────────────────────

    /// <summary>
    /// Age the book at the end of each night. Clients who stop showing up are
    /// eventually written off, so the list reflects who actually still comes.
    /// </summary>
    public void AdvanceNight()
    {
        // A new night; everyone is welcome through the door again.
        _seenTonight.Clear();

        var lost = new List<Patron>();

        foreach (var patron in _patrons.Values)
        {
            patron.NightsAbsent++;

            // A fixture is given twice as long to reappear as a regular.
            var limit = patron.Standing == PatronStanding.Patron ? AbsenceLimit * 2 : AbsenceLimit;

            if (patron.NightsAbsent > limit)
                lost.Add(patron);
            else if (patron.Satisfaction <= AbandonSatisfaction)
                lost.Add(patron);
        }

        foreach (var patron in lost)
        {
            var reason = patron.Satisfaction <= AbandonSatisfaction
                ? "stopped enjoying the place"
                : "has not been seen in weeks";

            _patrons.Remove(patron.Id);
            EmitSignal(SignalName.OnPatronLost, patron.Id, patron.Name, reason);

            GD.Print($"[Regulars] Lost {patron.Name} — {reason}.");
        }
    }

    /// <summary>Mark a patron as having been mined for intel.</summary>
    public void MarkIntelGathered(string patronId)
    {
        if (_patrons.TryGetValue(patronId, out var patron)) patron.IntelGathered = true;
    }

    public Patron GetById(string patronId) =>
        string.IsNullOrEmpty(patronId) ? null
            : _patrons.TryGetValue(patronId, out var patron) ? patron : null;

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "regulars";

    public JsonObject CaptureState()
    {
        var list = new JsonArray();

        foreach (var p in _patrons.Values)
        {
            list.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["visits"] = p.Visits,
                ["totalSpend"] = p.TotalSpend,
                ["satisfaction"] = p.Satisfaction,
                ["dayFirstSeen"] = p.DayFirstSeen,
                ["dayLastSeen"] = p.DayLastSeen,
                ["preferredRoom"] = p.PreferredRoom.ToString(),
                ["preferredStyle"] = p.PreferredStyle.ToString(),
                ["expectedAppointment"] = p.ExpectedAppointment,
                ["budget"] = p.Budget,
                ["fairPrice"] = p.FairPrice,
                ["intelGathered"] = p.IntelGathered,
                ["nightsAbsent"] = p.NightsAbsent
            });
        }

        return new JsonObject { ["patrons"] = list };
    }

    public void RestoreState(JsonObject state)
    {
        if (state?["patrons"] is not JsonArray list) return;

        _patrons.Clear();

        foreach (var node in list)
        {
            if (node is not JsonObject entry) continue;

            var patron = new Patron
            {
                Id = (string)entry["id"] ?? Guid.NewGuid().ToString("N")[..10],
                Name = (string)entry["name"] ?? "Unknown",
                Visits = (int?)entry["visits"] ?? 0,
                TotalSpend = (double?)entry["totalSpend"] ?? 0.0,
                Satisfaction = (float?)entry["satisfaction"] ?? 50f,
                DayFirstSeen = (int?)entry["dayFirstSeen"] ?? 0,
                DayLastSeen = (int?)entry["dayLastSeen"] ?? 0,
                ExpectedAppointment = (float?)entry["expectedAppointment"] ?? 45f,
                Budget = (double?)entry["budget"] ?? 200.0,
                FairPrice = (double?)entry["fairPrice"] ?? 100.0,
                IntelGathered = (bool?)entry["intelGathered"] ?? false,
                NightsAbsent = (int?)entry["nightsAbsent"] ?? 0
            };

            if (Enum.TryParse<RoomType>((string)entry["preferredRoom"], true, out var room))
                patron.PreferredRoom = room;

            if (Enum.TryParse<FurnitureStyle>((string)entry["preferredStyle"], true, out var style))
                patron.PreferredStyle = style;

            _patrons[patron.Id] = patron;
        }

        GD.Print($"[Regulars] Restored {_patrons.Count} known clients " +
                 $"({PatronCount} patrons).");
    }

    public override string ToString() =>
        $"[Regulars] {_patrons.Count} known, {RegularCount} regulars, {PatronCount} patrons";
}
