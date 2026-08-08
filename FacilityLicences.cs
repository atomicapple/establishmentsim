using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>What a licence raises.</summary>
public enum LicenceLine
{
    /// <summary>Access to better furniture than the catalogue currently stocks.</summary>
    Craft,

    /// <summary>How many people the house may keep on its books.</summary>
    House,

    /// <summary>How long a night may run, and how many a person may work.</summary>
    Hours,

    /// <summary>How much a room may hold.</summary>
    Space
}

/// <summary>A permission the house can apply for.</summary>
public class FacilityLicence
{
    public string Id { get; init; }
    public string Name { get; init; }
    public LicenceLine Line { get; init; }

    /// <summary>What it costs to apply.</summary>
    public double Cost { get; init; }

    /// <summary>Days between applying and being granted.</summary>
    public int Days { get; init; }

    /// <summary>Licence that must already be held, or null.</summary>
    public string Prerequisite { get; init; }

    /// <summary>Player-facing statement of the ceiling it moves.</summary>
    public string Effect { get; init; }

    public string Flavour { get; init; }
}

/// <summary>
/// Licences: the only thing in the game that raises a ceiling.
///
/// This replaces the old research tree, which was deleted rather than wired
/// up. All fifteen of its nodes duplicated systems that now work and are
/// reachable — soundproofing is a room you can build, training is a button
/// on a staff member, bribe networks are the influence panel — so connecting
/// them would have given the player two menus that do the same thing.
///
/// Everything else the house buys is an *instance*: this room, that
/// armchair, one more hire. Nothing moved a limit, and several limits were
/// hardcoded and quietly binding. The catalogue stocked tiers 1–3 while
/// FurnitureItem defines five, so the top two tiers existed, were scored by
/// the appointment formula, and could never be bought. Licences are how
/// those open.
///
/// They cost money and take time, so they are an investment made now for a
/// ceiling raised later — which is a different decision from any other
/// purchase in the game.
/// </summary>
public partial class FacilityLicences : Node, ISaveableSystem
{
    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnLicenceStartedEventHandler(string licenceId, int daysRemaining);

    [Signal]
    public delegate void OnLicenceGrantedEventHandler(string licenceId, string effect);

    [Signal]
    public delegate void OnLicenceRejectedEventHandler(string reason);

    // ── Catalogue ──────────────────────────────────────────────────────

    /// <summary>
    /// Every licence, in two tiers per line. Deliberately small: eight
    /// decisions that each visibly move a number beat fifteen that grant
    /// percentages nobody can see.
    /// </summary>
    public static readonly List<FacilityLicence> Catalogue = new()
    {
        new FacilityLicence
        {
            Id = "craft_1", Name = "Cabinetmaker's Warrant", Line = LicenceLine.Craft,
            Cost = 2200, Days = 6,
            Effect = "Catalogue stocks tier 4 furniture.",
            Flavour = "A workshop that will take your orders, and not discuss them."
        },
        new FacilityLicence
        {
            Id = "craft_2", Name = "Royal Warrant", Line = LicenceLine.Craft,
            Cost = 6500, Days = 12, Prerequisite = "craft_1",
            Effect = "Catalogue stocks tier 5 furniture.",
            Flavour = "The pieces that get written about. So does the house that owns them."
        },

        new FacilityLicence
        {
            Id = "house_1", Name = "Extended House Licence", Line = LicenceLine.House,
            Cost = 1800, Days = 5,
            Effect = "Roster cap 6 → 8.",
            Flavour = "Two more sets of papers the city agrees not to read closely."
        },
        new FacilityLicence
        {
            Id = "house_2", Name = "Grand House Licence", Line = LicenceLine.House,
            Cost = 5200, Days = 10, Prerequisite = "house_1",
            Effect = "Roster cap 8 → 11.",
            Flavour = "At this size the house stops being discreet and starts being known."
        },

        new FacilityLicence
        {
            Id = "hours_1", Name = "Late Licence", Line = LicenceLine.Hours,
            Cost = 1500, Days = 4,
            Effect = "Each staff member may work 5 encounters a night, up from 4.",
            Flavour = "The doors stay open an hour past the rest of the street."
        },
        new FacilityLicence
        {
            Id = "hours_2", Name = "All-Night Licence", Line = LicenceLine.Hours,
            Cost = 4800, Days = 9, Prerequisite = "hours_1",
            Effect = "Each staff member may work 6 encounters a night.",
            Flavour = "Nobody asks when you close, because you do not."
        },

        new FacilityLicence
        {
            Id = "space_1", Name = "Fittings Permit", Line = LicenceLine.Space,
            Cost = 2000, Days = 6,
            Effect = "Rooms hold 3 furnishings per tile, up from 2.",
            Flavour = "An inspector signs off on a floor plan he did not really read."
        },
        new FacilityLicence
        {
            Id = "space_2", Name = "Structural Permit", Line = LicenceLine.Space,
            Cost = 5600, Days = 11, Prerequisite = "space_1",
            Effect = "Rooms hold 4 furnishings per tile.",
            Flavour = "Walls moved, beams added. The building is a different shape now."
        }
    };

    // ── State ──────────────────────────────────────────────────────────

    private readonly HashSet<string> _granted = new();
    private readonly Dictionary<string, int> _inProgress = new();

    public IReadOnlyCollection<string> Granted => _granted;
    public IReadOnlyDictionary<string, int> InProgress => _inProgress;

    public bool IsGranted(string id) => !string.IsNullOrEmpty(id) && _granted.Contains(id);
    public bool IsInProgress(string id) => !string.IsNullOrEmpty(id) && _inProgress.ContainsKey(id);

    public static FacilityLicence Get(string id) => Catalogue.FirstOrDefault(l => l.Id == id);

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;

        GD.Print($"[Licences] Ready. {Catalogue.Count} available.");
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTick;
    }

    private void OnDailyTick(double cash, float rep, float heat, float sentiment)
    {
        if (_inProgress.Count == 0) return;

        foreach (var id in _inProgress.Keys.ToList())
        {
            _inProgress[id]--;
            if (_inProgress[id] > 0) continue;

            _inProgress.Remove(id);
            Grant(id);
        }
    }

    // ── Applying ───────────────────────────────────────────────────────

    /// <summary>Whether a licence can be applied for right now, and if not, why.</summary>
    public bool CanApply(string id, out string reason)
    {
        reason = "";

        var licence = Get(id);
        if (licence == null)
        {
            reason = "No such licence.";
            return false;
        }

        if (_granted.Contains(id))
        {
            reason = "Already held.";
            return false;
        }

        if (_inProgress.ContainsKey(id))
        {
            reason = $"Already applied for — {_inProgress[id]} days to go.";
            return false;
        }

        if (!string.IsNullOrEmpty(licence.Prerequisite) && !_granted.Contains(licence.Prerequisite))
        {
            reason = $"Needs {Get(licence.Prerequisite)?.Name ?? licence.Prerequisite} first.";
            return false;
        }

        var gsm = GameStateManager.Instance;
        if (gsm != null && gsm.Cash < licence.Cost)
        {
            reason = $"Costs ${licence.Cost:N0}; the house has ${gsm.Cash:N0}.";
            return false;
        }

        return true;
    }

    /// <summary>Pay for a licence and begin the wait.</summary>
    public bool Apply(string id)
    {
        if (!CanApply(id, out var reason))
        {
            EmitSignal(SignalName.OnLicenceRejected, reason);
            GD.Print($"[Licences] {reason}");
            return false;
        }

        var licence = Get(id);
        ChargeExpense(licence.Cost, $"Applied for {licence.Name}");

        _inProgress[id] = Mathf.Max(1, licence.Days);

        EmitSignal(SignalName.OnLicenceStarted, id, _inProgress[id]);
        GD.Print($"[Licences] Applied for {licence.Name} — {licence.Days} days.");
        return true;
    }

    /// <summary>Grant a licence and move the ceiling it controls.</summary>
    private void Grant(string id)
    {
        if (!_granted.Add(id)) return;

        var licence = Get(id);
        ApplyCeilings();

        EmitSignal(SignalName.OnLicenceGranted, id, licence?.Effect ?? "");
        GD.Print($"[Licences] Granted: {licence?.Name}. {licence?.Effect}");
    }

    // ── Ceilings ───────────────────────────────────────────────────────

    /// <summary>
    /// Push every held licence into the system that owns its limit.
    ///
    /// Recomputed from scratch rather than incremented, so restoring a save
    /// and granting a licence take the same path and cannot drift apart.
    /// </summary>
    public void ApplyCeilings()
    {
        var catalog = FindNode<FurnitureCatalog>("FurnitureCatalog");
        if (catalog != null)
        {
            catalog.MaxAvailableTier =
                _granted.Contains("craft_2") ? 5 :
                _granted.Contains("craft_1") ? 4 : 3;
        }

        var roster = StaffRoster.Instance;
        if (roster != null)
        {
            roster.RosterCap =
                _granted.Contains("house_2") ? 11 :
                _granted.Contains("house_1") ? 8 : 6;
        }

        var night = FindNode<NightDirector>("NightDirector");
        if (night != null)
        {
            night.MaxEncountersPerStaff =
                _granted.Contains("hours_2") ? 6 :
                _granted.Contains("hours_1") ? 5 : 4;
        }

        RoomModule.FurnitureSlotsPerTile =
            _granted.Contains("space_2") ? 4 :
            _granted.Contains("space_1") ? 3 : 2;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private T FindNode<T>(string nodeName) where T : Node =>
        GetTree()?.Root?.FindChild(nodeName, true, false) as T;

    private void ChargeExpense(double amount, string description)
    {
        var ledger = FindNode<FinancialLedger>("FinancialLedger");

        if (ledger != null)
            ledger.RecordExpense(ExpenseCategory.RoomUpgrades, amount, description);
        else if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= amount;
    }

    // ── Persistence ────────────────────────────────────────────────────

    public string SaveKey => "licences";

    public JsonObject CaptureState()
    {
        var granted = new JsonArray();
        foreach (var id in _granted) granted.Add(id);

        var pending = new JsonArray();
        foreach (var kvp in _inProgress)
            pending.Add(new JsonObject { ["id"] = kvp.Key, ["days"] = kvp.Value });

        return new JsonObject { ["granted"] = granted, ["inProgress"] = pending };
    }

    public void RestoreState(JsonObject state)
    {
        if (state == null) return;

        _granted.Clear();
        _inProgress.Clear();

        if (state["granted"] is JsonArray granted)
        {
            foreach (var node in granted)
            {
                var id = (string)node;
                if (!string.IsNullOrEmpty(id) && Get(id) != null) _granted.Add(id);
            }
        }

        if (state["inProgress"] is JsonArray pending)
        {
            foreach (var node in pending)
            {
                if (node is not JsonObject entry) continue;

                var id = (string)entry["id"];
                if (string.IsNullOrEmpty(id) || Get(id) == null) continue;

                _inProgress[id] = Math.Max(1, (int?)entry["days"] ?? 1);
            }
        }

        // The ceilings live on other systems, so a load has to push them back
        // out — otherwise a restored save quietly reverts to defaults.
        ApplyCeilings();

        GD.Print($"[Licences] Restored {_granted.Count} held, {_inProgress.Count} pending.");
    }

    public override string ToString() =>
        $"[Licences] {_granted.Count}/{Catalogue.Count} held, {_inProgress.Count} pending";
}
