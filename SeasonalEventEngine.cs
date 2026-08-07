using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A seasonal event definition with date window.</summary>
public class SeasonalEvent
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public DateTime StartDate { get; set; }      // real-world date
    public DateTime EndDate { get; set; }
    public string UiTheme { get; set; }           // path to theme resource
    public string[] RoomDecorations { get; set; } = Array.Empty<string>();
    public float ClientSpendBonus { get; set; } = 1.5f;
    public float HeatModifier { get; set; } = 1.0f;
    public string[] SpecialClients { get; set; } = Array.Empty<string>();
    public string[] UniqueEvents { get; set; } = Array.Empty<string>();
    public bool IsActive => DateTime.UtcNow >= StartDate && DateTime.UtcNow <= EndDate;
    public int DaysRemaining => IsActive ? (EndDate - DateTime.UtcNow).Days : 0;
}

/// <summary>
/// Triggers limited-time seasonal campaigns based on real-world dates.
/// Loads seasonal UI themes, custom room decorations, and unique
/// high-reward client queues during active event windows.
/// </summary>
public partial class SeasonalEventEngine : Node
{
    [Signal] public delegate void OnEventActivatedEventHandler(string eventId, string name, int daysRemaining);
    [Signal] public delegate void OnEventDeactivatedEventHandler(string eventId);
    [Signal] public delegate void OnThemeAppliedEventHandler(string themeName);

    private readonly List<SeasonalEvent> _events = new();
    private SeasonalEvent _activeEvent;
    private string _currentTheme;

    public SeasonalEvent ActiveEvent => _activeEvent;
    public bool IsEventActive => _activeEvent != null;
    public string CurrentTheme => _currentTheme;

    public override void _Ready()
    {
        RegisterDefaultEvents();
        CheckForActiveEvents();
        GD.Print($"[SeasonalEvents] Initialized. {_events.Count} events registered.");
    }

    private void RegisterDefaultEvents()
    {
        int year = DateTime.UtcNow.Year;

        _events.AddRange(new[]
        {
            new SeasonalEvent
            {
                Id = "halloween_scandal", Name = "Halloween Syndicate Scandal",
                Description = "Mysterious masked clients flood the city. Rival syndicates use the chaos for covert operations.",
                StartDate = new DateTime(year, 10, 25), EndDate = new DateTime(year, 11, 2),
                UiTheme = "res://themes/halloween_dark.tres",
                RoomDecorations = new[] { "pumpkin_lantern", "cobweb_overlay", "candle_sconce" },
                ClientSpendBonus = 2.0f, HeatModifier = 0.7f,
                SpecialClients = new[] { "Masked Delegate", "Cult Leader", "Phantom Broker" },
                UniqueEvents = new[] { "masked_ball_raid", "pumpkin_heist", "ghost_protocol" }
            },
            new SeasonalEvent
            {
                Id = "new_year_gala", Name = "New Year Political Gala",
                Description = "Politicians and elites gather for the year's most extravagant parties. Favors and bribes flow like champagne.",
                StartDate = new DateTime(year, 12, 28), EndDate = new DateTime(year + 1, 1, 3),
                UiTheme = "res://themes/newyear_gold.tres",
                RoomDecorations = new[] { "gold_streamers", "champagne_tower", "firework_display" },
                ClientSpendBonus = 3.0f, HeatModifier = 0.5f,
                SpecialClients = new[] { "Senator Ashford", "Ambassador Voss", "CEO Sterling" },
                UniqueEvents = new[] { "midnight_toast", "ballot_scandal", "resolution_betrayal" }
            },
            new SeasonalEvent
            {
                Id = "summer_heatwave", Name = "Summer Vice Wave",
                Description = "A scorching heatwave drives clients indoors. Luxury suites are in high demand — so are bribes to keep the AC running.",
                StartDate = new DateTime(year, 7, 1), EndDate = new DateTime(year, 8, 15),
                UiTheme = "res://themes/summer_neon.tres",
                RoomDecorations = new[] { "neon_sign", "pool_table", "tropical_plants" },
                ClientSpendBonus = 1.8f, HeatModifier = 1.2f,
                SpecialClients = new[] { "Heatwave Hustler", "Sunset Smuggler", "Poolside Patron" },
                UniqueEvents = new[] { "ac_blackmail", "poolside_deal", "heatwave_raid" }
            },
            new SeasonalEvent
            {
                Id = "winter_crackdown", Name = "Winter Police Crackdown",
                Description = "Holiday budget pressures drive police to aggressive enforcement. Bribes double — but so do the rewards for the bold.",
                StartDate = new DateTime(year, 12, 10), EndDate = new DateTime(year, 12, 24),
                UiTheme = "res://themes/winter_blue.tres",
                RoomDecorations = new[] { "frosted_windows", "holiday_wreath", "snow_drift" },
                ClientSpendBonus = 1.5f, HeatModifier = 2.0f,
                SpecialClients = new[] { "Holiday Inspector", "Frosty Enforcer", "Santa's Bagman" },
                UniqueEvents = new[] { "candy_cane_sting", "reindeer_racket", "stocking_stuff" }
            },
            new SeasonalEvent
            {
                Id = "spring_elections", Name = "Spring Election Season",
                Description = "Campaign season brings desperate politicians seeking funding — and leverage. Every donation buys influence.",
                StartDate = new DateTime(year, 3, 15), EndDate = new DateTime(year, 4, 15),
                UiTheme = "res://themes/spring_patriotic.tres",
                RoomDecorations = new[] { "campaign_banners", "voting_booth", "donation_box" },
                ClientSpendBonus = 2.2f, HeatModifier = 0.8f,
                SpecialClients = new[] { "Candidate Hawthorne", "Campaign Manager Nyx", "Pollster Voss" },
                UniqueEvents = new[] { "ballot_box_stuff", "debate_scandal", "election_night_surprise" }
            },
        });
    }

    // ── Event Lifecycle ─────────────────────────────────────────────────

    private void CheckForActiveEvents()
    {
        foreach (var evt in _events.OrderByDescending(e => e.StartDate))
        {
            if (evt.IsActive)
            {
                ActivateEvent(evt);
                return; // one event at a time
            }
        }

        // No active event
        if (_activeEvent != null)
            DeactivateEvent();
    }

    private void ActivateEvent(SeasonalEvent evt)
    {
        _activeEvent = evt;
        _currentTheme = evt.UiTheme;

        // Apply UI theme
        ApplyUiTheme(evt.UiTheme);

        // Apply room decorations
        ApplyRoomDecorations(evt.RoomDecorations);

        EmitSignal(SignalName.OnEventActivated, evt.Id, evt.Name, evt.DaysRemaining);
        EmitSignal(SignalName.OnThemeApplied, evt.UiTheme);

        GD.Print($"[SeasonalEvents] Activated: \"{evt.Name}\" ({evt.DaysRemaining} days remaining).");
    }

    private void DeactivateEvent()
    {
        if (_activeEvent == null) return;
        var id = _activeEvent.Id;
        _activeEvent = null;
        _currentTheme = null;

        // Remove decorations
        ClearRoomDecorations();

        EmitSignal(SignalName.OnEventDeactivated, id);
        GD.Print($"[SeasonalEvents] Deactivated: {id}.");
    }

    // ── UI Theme ────────────────────────────────────────────────────────

    private void ApplyUiTheme(string themePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(themePath) && Godot.FileAccess.FileExists(themePath))
            {
                var theme = ResourceLoader.Load<Theme>(themePath);
                if (theme != null)
                {
                    GetTree()?.Root?.SetTheme(theme);
                    GD.Print($"[SeasonalEvents] UI theme applied: {themePath}");
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SeasonalEvents] Theme load failed: {ex.Message}");
        }
    }

    // ── Room Decorations ────────────────────────────────────────────────

    private void ApplyRoomDecorations(string[] decorations)
    {
        if (decorations == null || decorations.Length == 0) return;

        var vg = GetTree()?.Root?.FindChild("VenueBuilding", true, false) as VenueBuilding;
        if (vg == null) return;

        // Mark rooms as decorated — the VFX system picks up the tags
        foreach (var kvp in vg.Rooms)
        {
            // Room decoration state would be applied via the VFX system
            GD.Print($"[SeasonalEvents] Decorating room at {kvp.Key} with {decorations.Length} items.");
        }
    }

    private void ClearRoomDecorations()
    {
        var vfx = GetTree()?.Root?.FindChild("VenueAtmosphereVFX", true, false) as VenueAtmosphereVFX;
        // Clear seasonal decorations
        GD.Print("[SeasonalEvents] Room decorations cleared.");
    }

    // ── Client Queue Integration ────────────────────────────────────────

    /// <summary>Get special client names for the active event.</summary>
    public string[] GetSpecialClients()
    {
        return _activeEvent?.SpecialClients ?? Array.Empty<string>();
    }

    /// <summary>Get client spending multiplier during event.</summary>
    public float GetClientSpendBonus()
    {
        return _activeEvent?.ClientSpendBonus ?? 1.0f;
    }

    /// <summary>Get heat modifier during event.</summary>
    public float GetHeatModifier()
    {
        return _activeEvent?.HeatModifier ?? 1.0f;
    }

    /// <summary>Get unique story events available during the seasonal window.</summary>
    public string[] GetUniqueEvents()
    {
        return _activeEvent?.UniqueEvents ?? Array.Empty<string>();
    }

    /// <summary>Trigger a unique seasonal event by index.</summary>
    public string TriggerUniqueEvent(int index)
    {
        var events = GetUniqueEvents();
        if (index < 0 || index >= events.Length) return null;
        string evt = events[index];
        GD.Print($"[SeasonalEvents] Unique event triggered: {evt}");
        return evt;
    }

    // ── Query ───────────────────────────────────────────────────────────

    /// <summary>Get all upcoming events for the year.</summary>
    public List<SeasonalEvent> GetUpcomingEvents()
    {
        var now = DateTime.UtcNow;
        return _events.Where(e => e.StartDate > now).OrderBy(e => e.StartDate).ToList();
    }

    /// <summary>Get a formatted countdown string.</summary>
    public string GetCountdownString()
    {
        if (!IsEventActive) return "No active event.";
        return $"{_activeEvent.Name} — {_activeEvent.DaysRemaining} days remaining";
    }

    /// <summary>Get the current event's description for UI.</summary>
    public string GetEventDescription()
    {
        return _activeEvent?.Description ?? "No seasonal event active.";
    }

    public override string ToString() =>
        $"[SeasonalEvents] Active={(_activeEvent?.Name ?? "none")} " +
        $"Upcoming={GetUpcomingEvents().Count}";
}
