using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Canonical owner of the staff roster.
///
/// This exists because every consumer previously reached into
/// <c>PsychologicalBreakSystem.GetStaffAtRisk()</c> and treated its result as
/// the full roster — but that method filters to <c>Stress >= 80</c>. The
/// consequence was that healthy staff were invisible to save/load, to the
/// union calculation, to poaching, and to the AI context layer.
///
/// Anything that needs "the staff" asks this. <see cref="GetAll"/> is
/// unfiltered and is the only correct source.
///
/// Registered as an Autoload in project.godot:
///   StaffRoster="*res://StaffRoster.cs"
/// </summary>
public partial class StaffRoster : Node
{
    // ── Singleton ──────────────────────────────────────────────────────
    public static StaffRoster Instance { get; private set; }

    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired when a staff member joins the house.</summary>
    [Signal]
    public delegate void OnStaffHiredEventHandler(string staffId, string staffName, int origin);

    /// <summary>Fired when a staff member leaves, for any reason.</summary>
    [Signal]
    public delegate void OnStaffDepartedEventHandler(string staffId, string staffName, string reason);

    /// <summary>Fired when a bond or rivalry crosses a threshold.</summary>
    [Signal]
    public delegate void OnRelationshipFormedEventHandler(string staffIdA, string staffIdB, bool isBond);

    /// <summary>Fired when the roster composition changes, for UI refresh.</summary>
    [Signal]
    public delegate void OnRosterChangedEventHandler(int count);

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>
    /// Maximum roster size. Deliberately small — this is a named-character
    /// game, not a headcount game. Raised by venue expansion.
    /// </summary>
    [Export]
    public int RosterCap { get; set; } = 6;

    /// <summary>Affinity at or above which a pair counts as bonded.</summary>
    [Export]
    public float BondThreshold { get; set; } = 40f;

    /// <summary>Affinity at or below which a pair counts as rivals.</summary>
    [Export]
    public float RivalryThreshold { get; set; } = -40f;

    /// <summary>Stress relief per night for each bonded colleague on the same floor.</summary>
    [Export]
    public float BondStressRelief { get; set; } = 2.5f;

    /// <summary>Extra stress per night for each rival on the same floor.</summary>
    [Export]
    public float RivalryStressPenalty { get; set; } = 3.5f;

    // ── State ──────────────────────────────────────────────────────────

    private readonly Dictionary<string, StaffMember> _staff = new();

    /// <summary>Every staff member currently employed. Unfiltered.</summary>
    public IReadOnlyCollection<StaffMember> GetAll() => _staff.Values;

    /// <summary>Current headcount.</summary>
    public int Count => _staff.Count;

    /// <summary>Whether the roster has room for another hire.</summary>
    public bool HasVacancy => _staff.Count < RosterCap;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        if (Instance != null && Instance != this)
        {
            GD.PrintErr("[StaffRoster] Duplicate singleton detected. Destroying self.");
            QueueFree();
            return;
        }

        Instance = this;
        GD.Print($"[StaffRoster] Initialized. Cap={RosterCap}.");
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    // ── Membership ─────────────────────────────────────────────────────

    /// <summary>
    /// Add a staff member to the roster. Fails if the roster is full or the
    /// Id already exists.
    /// </summary>
    /// <returns>True if added.</returns>
    public bool Add(StaffMember staff)
    {
        if (staff == null)
        {
            GD.PrintErr("[StaffRoster] Add called with null staff.");
            return false;
        }

        if (string.IsNullOrEmpty(staff.Id))
        {
            GD.PrintErr($"[StaffRoster] Staff '{staff.StaffName}' has no Id. Rejected.");
            return false;
        }

        if (_staff.ContainsKey(staff.Id))
        {
            GD.PrintErr($"[StaffRoster] Duplicate staff Id '{staff.Id}'. Rejected.");
            return false;
        }

        if (!HasVacancy)
        {
            GD.Print($"[StaffRoster] Roster full ({_staff.Count}/{RosterCap}). Cannot add {staff.StaffName}.");
            return false;
        }

        _staff[staff.Id] = staff;

        // The psychological system keeps its own registry for break tracking.
        // Its RegisterStaff was previously never called by anything, which is
        // why the break system sat inert. This is that missing call.
        FindBreakSystem()?.RegisterStaff(staff);

        EmitSignal(SignalName.OnStaffHired, staff.Id, staff.StaffName, (int)staff.Origin);
        EmitSignal(SignalName.OnRosterChanged, _staff.Count);

        GD.Print($"[StaffRoster] Hired {staff.StaffName} ({staff.Origin}). Roster {_staff.Count}/{RosterCap}.");
        return true;
    }

    /// <summary>
    /// Remove a staff member. Refuses to remove a debt-bound member unless
    /// <paramref name="force"/> is set — bound staff cannot simply walk, and
    /// silently letting them would defeat the point of that acquisition channel.
    /// </summary>
    /// <returns>True if removed.</returns>
    public bool Remove(string staffId, string reason, bool force = false)
    {
        if (!_staff.TryGetValue(staffId, out var staff)) return false;

        if (staff.IsBound && !force)
        {
            GD.Print($"[StaffRoster] {staff.StaffName} is contract-bound (${staff.ContractDebt:F0}) and cannot leave.");
            return false;
        }

        _staff.Remove(staffId);
        FindBreakSystem()?.UnregisterStaff(staff);

        // Scrub the departing member from everyone else's relationship map so
        // stale ids don't accumulate across a long campaign.
        foreach (var other in _staff.Values)
            other.Relationships.Remove(staffId);

        EmitSignal(SignalName.OnStaffDeparted, staffId, staff.StaffName, reason);
        EmitSignal(SignalName.OnRosterChanged, _staff.Count);

        GD.Print($"[StaffRoster] {staff.StaffName} departed: {reason}. Roster {_staff.Count}/{RosterCap}.");
        return true;
    }

    // ── Lookup ─────────────────────────────────────────────────────────

    /// <summary>Fetch by stable Id. Null if absent.</summary>
    public StaffMember GetById(string staffId)
    {
        if (string.IsNullOrEmpty(staffId)) return null;
        return _staff.TryGetValue(staffId, out var staff) ? staff : null;
    }

    /// <summary>
    /// Fetch by display name. Null if absent or ambiguous. Prefer
    /// <see cref="GetById"/> — names are display text and may collide.
    /// </summary>
    public StaffMember GetByName(string staffName)
    {
        if (string.IsNullOrEmpty(staffName)) return null;
        var matches = _staff.Values.Where(s => s.StaffName == staffName).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Whether this Id is on the roster.</summary>
    public bool Contains(string staffId) => !string.IsNullOrEmpty(staffId) && _staff.ContainsKey(staffId);

    // ── Filtered views ─────────────────────────────────────────────────

    /// <summary>Staff at or above the burnout stress threshold.</summary>
    public List<StaffMember> GetBurningOut() =>
        _staff.Values.Where(s => s.IsBurningOut).ToList();

    /// <summary>Staff who want to leave (whether or not they are able to).</summary>
    public List<StaffMember> GetQuitRisks() =>
        _staff.Values.Where(s => s.IsQuitRisk).ToList();

    /// <summary>Staff who would take a rival's offer or turn on the house.</summary>
    public List<StaffMember> GetDefectionRisks() =>
        _staff.Values.Where(s => s.IsDefectionRisk).ToList();

    /// <summary>Staff holding a given specialization.</summary>
    public List<StaffMember> GetBySpecialization(StaffSpecialization spec) =>
        _staff.Values.Where(s => s.Specialization == spec).ToList();

    /// <summary>Staff with no specialization committed yet.</summary>
    public List<StaffMember> GetUnspecialized() =>
        _staff.Values.Where(s => s.Specialization == StaffSpecialization.None).ToList();

    /// <summary>
    /// The most effective available staff member, for auto-assignment.
    /// Null if the roster is empty.
    /// </summary>
    public StaffMember GetMostEffective() =>
        _staff.Values.OrderByDescending(s => s.EffectivenessRating).FirstOrDefault();

    // ── Aggregates ─────────────────────────────────────────────────────

    /// <summary>Mean stress across the whole roster. Zero when empty.</summary>
    public float GetAverageStress() =>
        _staff.Count == 0 ? 0f : _staff.Values.Average(s => s.Stress);

    /// <summary>Mean satisfaction across the whole roster. Zero when empty.</summary>
    public float GetAverageSatisfaction() =>
        _staff.Count == 0 ? 0f : _staff.Values.Average(s => s.Satisfaction);

    /// <summary>Mean loyalty across the whole roster. Zero when empty.</summary>
    public float GetAverageLoyalty() =>
        _staff.Count == 0 ? 0f : _staff.Values.Average(s => s.Loyalty);

    /// <summary>Total monthly salary obligation.</summary>
    public double GetTotalSalaries() =>
        _staff.Values.Sum(s => s.Salary);

    // ── Relationships ──────────────────────────────────────────────────

    /// <summary>
    /// Adjust affinity between two staff members. Applies to both directions
    /// by default; pass <paramref name="mutual"/> false for one-sided feelings.
    /// Emits <see cref="OnRelationshipFormedEventHandler"/> on threshold crossings.
    /// </summary>
    public void AdjustRelationship(string idA, string idB, float delta, bool mutual = true)
    {
        var a = GetById(idA);
        var b = GetById(idB);
        if (a == null || b == null || idA == idB) return;

        var wasBonded = a.IsBondedWith(idB);
        var wasRival = a.IsRivalOf(idB);

        a.AdjustRelationship(idB, delta);
        if (mutual) b.AdjustRelationship(idA, delta);

        var isBonded = a.IsBondedWith(idB);
        var isRival = a.IsRivalOf(idB);

        if (!wasBonded && isBonded)
            EmitSignal(SignalName.OnRelationshipFormed, idA, idB, true);
        else if (!wasRival && isRival)
            EmitSignal(SignalName.OnRelationshipFormed, idA, idB, false);
    }

    /// <summary>
    /// Net stress modifier for a staff member from the colleagues sharing
    /// their floor tonight. Bonds relieve, rivalries aggravate. Negative
    /// values mean relief.
    /// </summary>
    /// <param name="staffId">Who to evaluate.</param>
    /// <param name="colleagueIds">Ids of staff assigned to the same floor.</param>
    public float GetSocialStressModifier(string staffId, IEnumerable<string> colleagueIds)
    {
        var staff = GetById(staffId);
        if (staff == null || colleagueIds == null) return 0f;

        var modifier = 0f;
        foreach (var otherId in colleagueIds)
        {
            if (otherId == staffId) continue;
            if (staff.IsBondedWith(otherId)) modifier -= BondStressRelief;
            else if (staff.IsRivalOf(otherId)) modifier += RivalryStressPenalty;
        }

        return modifier;
    }

    /// <summary>Every bonded pair on the roster, each reported once.</summary>
    public List<(StaffMember A, StaffMember B)> GetBondedPairs() => GetPairsWhere(true);

    /// <summary>Every rival pair on the roster, each reported once.</summary>
    public List<(StaffMember A, StaffMember B)> GetRivalPairs() => GetPairsWhere(false);

    private List<(StaffMember, StaffMember)> GetPairsWhere(bool bonded)
    {
        var result = new List<(StaffMember, StaffMember)>();
        var all = _staff.Values.ToList();

        for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
        {
            var match = bonded
                ? all[i].IsBondedWith(all[j].Id)
                : all[i].IsRivalOf(all[j].Id);

            if (match) result.Add((all[i], all[j]));
        }

        return result;
    }

    // ── Weekly review ──────────────────────────────────────────────────

    /// <summary>
    /// Weekly upkeep on the human layer: ambition neglect, loyalty drift,
    /// and specialization offers. Called by the night loop on week boundaries,
    /// not nightly — these should be slow enough for the player to notice and act on.
    /// </summary>
    public void RunWeeklyReview()
    {
        foreach (var staff in _staff.Values.ToList())
        {
            staff.ApplyAmbitionNeglect();

            // Sustained misery erodes commitment even when the ambition is met.
            if (staff.Satisfaction < 30f)
                staff.AdjustLoyalty(-2f, "low satisfaction");
            else if (staff.Satisfaction > 70f && staff.Stress < 40f)
                staff.AdjustLoyalty(1.5f, "good conditions");

            // Debt bondage is corrosive by design: it holds them in place
            // while steadily making them someone who would testify.
            if (staff.IsBound)
                staff.AdjustLoyalty(-1.5f, "contract debt");
        }

        GD.Print($"[StaffRoster] Weekly review — avg stress {GetAverageStress():F0}, " +
                 $"satisfaction {GetAverageSatisfaction():F0}, loyalty {GetAverageLoyalty():F0}.");
    }

    // ── Persistence support ────────────────────────────────────────────

    /// <summary>
    /// Replace the entire roster. Used by save loading — the caller is
    /// responsible for having constructed fully-populated StaffMember
    /// instances first.
    /// </summary>
    public void ReplaceAll(IEnumerable<StaffMember> staff)
    {
        var breakSystem = FindBreakSystem();

        foreach (var existing in _staff.Values)
            breakSystem?.UnregisterStaff(existing);

        _staff.Clear();

        foreach (var member in staff)
        {
            if (member == null || string.IsNullOrEmpty(member.Id)) continue;
            _staff[member.Id] = member;
            breakSystem?.RegisterStaff(member);
        }

        EmitSignal(SignalName.OnRosterChanged, _staff.Count);
        GD.Print($"[StaffRoster] Roster replaced from save: {_staff.Count} staff.");
    }

    // ── Internal ───────────────────────────────────────────────────────

    private PsychologicalBreakSystem FindBreakSystem() =>
        GetTree()?.Root?.FindChild("PsychologicalBreakSystem", recursive: true, owned: false)
            as PsychologicalBreakSystem;

    public override string ToString() =>
        $"[StaffRoster] {_staff.Count}/{RosterCap} — " +
        $"stress {GetAverageStress():F0} sat {GetAverageSatisfaction():F0} loy {GetAverageLoyalty():F0}";
}
