using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Headless end-to-end check of the night loop and persistence.
///
/// Plays three consecutive nights, then saves, perturbs the world, reloads,
/// and asserts the restored state matches. This is the milestone gate from
/// the plan: it exercises furniture → appointment → price → cash, staff
/// assignment and stress, encounter resolution, the ledger, and the full
/// save/load round-trip in one pass.
///
/// Run with:
///   Godot_v4.7.1-stable_win64_console.exe --headless --path . smoke_test.tscn
/// </summary>
public partial class SmokeTest : Node
{
    private enum Stage { Waiting, PlayingNights, Persistence, Done }

    private GameBootstrap _boot;
    private Stage _stage = Stage.Waiting;

    private int _nightsPlayed;
    private const int NightsToPlay = 3;

    private readonly List<string> _failures = new();
    private readonly List<string> _checks = new();

    // Captured before save, compared after load.
    private double _savedCash;
    private int _savedDay;
    private int _savedStaffCount;
    private int _savedRoomCount;
    private float _savedHeat;
    private string _probeStaffId;
    private float _probeStaffStress;

    public override void _Ready()
    {
        _boot = new GameBootstrap
        {
            Name = "GameBootstrap",
            WorldSeed = 20260807,          // deterministic run
            UseLegacyDayTimer = false
        };

        AddChild(_boot);
        _boot.OnWorldReady += OnWorldReady;
    }

    private void OnWorldReady()
    {
        GD.Print("\n══════════ SMOKE TEST ══════════");
        GD.Print(_boot.GetWorldSummary());

        // Compress the night so three of them run in a handful of frames.
        _boot.Night.ServiceDurationSeconds = 2.0f;
        _boot.Night.EncounterDurationSeconds = 0.12f;

        Check("world seeded with staff", StaffRoster.Instance?.Count > 0);
        Check("world seeded with bookable rooms", _boot.Venue.GetBookableRoomCount() > 0);
        Check("suites are furnished", _boot.Venue.GetAverageAppointment() > 0f);
        Check("nightly upkeep is non-zero", _boot.Venue.GetNightlyUpkeep() > 0.0);

        _stage = Stage.PlayingNights;
        StartNight();
    }

    public override void _Process(double delta)
    {
        if (_stage != Stage.PlayingNights) return;
        if (_boot?.Night == null) return;

        // The director advances itself on _Process; we only react to it
        // reaching the Ledger beat.
        if (_boot.Night.Phase == NightPhase.Ledger)
            FinishNight();
    }

    private void StartNight()
    {
        _boot.Night.BeginNight();

        var assigned = _boot.AutoAssignStaff();
        Check($"night {_nightsPlayed + 1}: staff posted", assigned > 0);

        _boot.Night.OpenDoors();
        Check($"night {_nightsPlayed + 1}: service began",
            _boot.Night.Phase == NightPhase.Service);
    }

    private void FinishNight()
    {
        var report = _boot.Night.CurrentReport;
        _nightsPlayed++;

        GD.Print($"\n── Night {report.Night} ──");
        GD.Print($"  arrived {report.ClientsArrived}, served {report.ClientsServed}, " +
                 $"turned away {report.ClientsTurnedAway}");
        GD.Print($"  revenue ${report.Revenue:F0}, upkeep ${report.Upkeep:F0}, " +
                 $"net ${report.Net:F0}");
        GD.Print($"  reputation {report.ReputationDelta:+0.0;-0.0;0}, " +
                 $"heat {report.HeatDelta:+0.0;-0.0;0}, regulars {report.NewRegulars}");

        if (report.QualityCounts.Count > 0)
        {
            var bands = string.Join(", ",
                report.QualityCounts.OrderByDescending(k => k.Key).Select(k => $"{k.Key} ×{k.Value}"));
            GD.Print($"  outcomes: {bands}");
        }

        foreach (var h in report.Highlights) GD.Print($"  + {h}");
        foreach (var i in report.Incidents) GD.Print($"  ! {i}");

        Check($"night {report.Night}: clients arrived", report.ClientsArrived > 0);
        Check($"night {report.Night}: someone was served", report.ClientsServed > 0);
        Check($"night {report.Night}: revenue was booked", report.Revenue > 0);

        var dayBefore = GameStateManager.Instance.DayCount;
        _boot.Night.ConcludeNight();

        Check($"night {report.Night}: day advanced",
            GameStateManager.Instance.DayCount == dayBefore + 1);

        if (_nightsPlayed >= NightsToPlay)
        {
            _stage = Stage.Persistence;
            CallDeferred(nameof(RunPersistenceCheck));
            return;
        }

        StartNight();
    }

    // ── Persistence round-trip ─────────────────────────────────────────

    private void RunPersistenceCheck()
    {
        GD.Print("\n── Persistence round-trip ──");

        var gsm = GameStateManager.Instance;
        var roster = StaffRoster.Instance;

        // The ledger must actually reflect the nights we played.
        Check("ledger recorded revenue", _boot.Ledger.GetTotalRevenue() > 0);
        Check("ledger recorded expenses", _boot.Ledger.GetTotalExpenses() > 0);

        // Heat must be able to fall on a quiet day — the old gate made this
        // impossible, so it is worth asserting directly.
        var heatBefore = gsm.Heat;
        gsm.Heat = 50f;
        _boot.Heat.RestoreState(new System.Text.Json.Nodes.JsonObject { ["heat"] = 50f });
        gsm.AdvanceDay();
        Check($"heat decays on a quiet day ({50f:F1} → {gsm.Heat:F1})", gsm.Heat < 50f);

        _savedCash = gsm.Cash;
        _savedDay = gsm.DayCount;
        _savedHeat = gsm.Heat;
        _savedStaffCount = roster.Count;
        _savedRoomCount = _boot.Venue.Rooms.Count;

        var probe = roster.GetAll().FirstOrDefault();
        _probeStaffId = probe?.Id;
        _probeStaffStress = probe?.Stress ?? 0f;

        Check("save succeeded", _boot.SaveLoad.SaveGame("smoketest"));

        // Perturb everything the load must put back.
        gsm.Cash = 999999;
        gsm.Heat = 5f;
        gsm.SetDayCount(1);
        roster.ReplaceAll(Array.Empty<StaffMember>());
        Check("world was perturbed before reload", roster.Count == 0);

        var loaded = _boot.SaveLoad.LoadGame("smoketest");
        Check("load succeeded (checksum verified)", loaded != null);

        if (loaded != null)
        {
            Check($"cash restored (${_savedCash:F0})",
                Math.Abs(gsm.Cash - _savedCash) < 0.01);
            Check($"day restored ({_savedDay})", gsm.DayCount == _savedDay);
            Check($"heat restored ({_savedHeat:F1})",
                Mathf.Abs(gsm.Heat - _savedHeat) < 0.1f);
            Check($"roster restored ({_savedStaffCount} staff)",
                roster.Count == _savedStaffCount);
            Check($"floorplan restored ({_savedRoomCount} rooms)",
                _boot.Venue.Rooms.Count == _savedRoomCount);

            var restored = roster.GetById(_probeStaffId);
            Check("staff identity survived by Id", restored != null);
            Check("staff stress survived",
                restored != null && Mathf.Abs(restored.Stress - _probeStaffStress) < 0.1f);
            Check("furniture survived the round-trip",
                _boot.Venue.GetAverageAppointment() > 0f);
            Check("relationships survived",
                roster.GetBondedPairs().Count > 0 || roster.GetRivalPairs().Count > 0);
        }

        _boot.SaveLoad.DeleteSave("smoketest");
        Finish();
    }

    // ── Reporting ──────────────────────────────────────────────────────

    private void Check(string label, bool passed)
    {
        _checks.Add($"{(passed ? "  PASS" : "  FAIL")}  {label}");
        if (!passed) _failures.Add(label);
    }

    private void Finish()
    {
        _stage = Stage.Done;

        GD.Print("\n══════════ RESULTS ══════════");
        foreach (var line in _checks) GD.Print(line);

        GD.Print($"\n{_checks.Count - _failures.Count}/{_checks.Count} passed.");

        if (_failures.Count > 0)
        {
            GD.PrintErr($"SMOKE TEST FAILED — {_failures.Count} failure(s):");
            foreach (var f in _failures) GD.PrintErr($"  - {f}");
        }
        else
        {
            GD.Print("SMOKE TEST PASSED");
        }

        GD.Print("\nFinal world state:");
        GD.Print(_boot.GetWorldSummary());

        GetTree().Quit(_failures.Count > 0 ? 1 : 0);
    }
}
