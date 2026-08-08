using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Headless N-night simulator for tuning.
///
/// Plays a long run with a fixed seed and reports the cash trajectory, the
/// revenue-versus-cost split, and the distribution of encounter outcomes.
/// Balance is not something to guess at from a three-night smoke test —
/// this is the instrument for changing numbers against evidence.
///
/// Run with:
///   Godot_v4.7.1-stable_win64_console.exe --headless --path . balance.tscn
/// </summary>
public partial class BalanceHarness : Node
{
    /// <summary>Nights to simulate.</summary>
    [Export] public int Nights { get; set; } = 20;

    /// <summary>Seed, so a tuning change is measured against the same run.</summary>
    [Export] public ulong Seed { get; set; } = 20260807;

    private GameBootstrap _boot;
    private int _played;

    private readonly List<NightSample> _samples = new();

    private struct NightSample
    {
        public int Night;
        public double Revenue;
        public double Upkeep;
        public double Salaries;
        public double Commission;
        public double CashAfter;
        public int Served;
        public int TurnedAway;
        public float AvgAppointment;
        public float AvgLoyalty;
        public float AvgStress;
        public float Heat;
        public int RosterSize;
        public float Reputation;
        public float Footfall;
        public float Sentiment;
    }

    private readonly Dictionary<EncounterQuality, int> _qualityTotals = new();

    private int _crisesAnswered;

    public override void _Ready()
    {
        _boot = new GameBootstrap
        {
            Name = "GameBootstrap",
            WorldSeed = Seed,
            UseLegacyDayTimer = false
        };

        AddChild(_boot);
        _boot.OnWorldReady += OnWorldReady;
    }

    private void OnWorldReady()
    {
        _boot.Night.ServiceDurationSeconds = 2.0f;
        _boot.Night.EncounterDurationSeconds = 0.10f;

        // Off the wall clock. A night is now a fixed number of beats rather
        // than a fixed number of seconds, so the same seed plays the same run
        // on any machine and under any load.
        _boot.Night.FixedStepSeconds = 1f / 60f;

        _boot.Night.OnEncounterResolved += OnEncounterResolved;

        GD.Print($"\n═══ BALANCE RUN — {Nights} nights, seed {Seed} ═══");
        GD.Print(_boot.GetWorldSummary());

        StartNight();
    }

    private void OnEncounterResolved(string staffId, int quality, double payment, int incident)
    {
        var band = (EncounterQuality)quality;
        _qualityTotals[band] = _qualityTotals.GetValueOrDefault(band) + 1;
    }

    public override void _Process(double delta)
    {
        if (_played >= Nights) return;
        if (_boot?.Night == null) return;

        if (_boot.Night.Phase == NightPhase.Ledger) FinishNight();
    }

    private void StartNight()
    {
        _boot.Night.BeginNight();
        _boot.AutoAssignStaff();
        _boot.Night.OpenDoors();
    }

    private void FinishNight()
    {
        // The harness plays naively, but it cannot ignore a crisis — an
        // unanswered one latches the director and every later crisis is
        // silently suppressed. It always takes the first option, which is
        // the "pay it away" shape, so the run measures a house that responds
        // without thinking rather than one that cannot respond at all.
        var crises = _boot.Crises;
        if (crises?.CrisisActive == true)
        {
            _crisesAnswered++;
            if (!crises.ExecuteChoice(0)) crises.DismissCrisis();
        }

        var r = _boot.Night.CurrentReport;
        var roster = StaffRoster.Instance;
        var gsm = GameStateManager.Instance;

        _boot.Night.ConcludeNight();
        _played++;

        _samples.Add(new NightSample
        {
            Night = r.Night,
            Revenue = r.Revenue,
            Upkeep = r.Upkeep,
            Salaries = r.Salaries,
            Commission = r.StaffCommission,
            CashAfter = gsm.Cash,
            Served = r.ClientsServed,
            TurnedAway = r.ClientsTurnedAway,
            AvgAppointment = _boot.Venue.GetAverageAppointment(),
            AvgLoyalty = roster.GetAverageLoyalty(),
            AvgStress = roster.GetAverageStress(),
            Heat = gsm.Heat,
            RosterSize = roster.Count,
            Reputation = gsm.Reputation,
            Footfall = _boot.Macro?.FootfallMultiplier ?? 1f,
            Sentiment = gsm.PublicSentiment
        });

        if (_played >= Nights)
        {
            Report();
            return;
        }

        StartNight();
    }

    private void Report()
    {
        GD.Print("\n─── Per night ───");
        GD.Print(" N |  served |  revenue |  commis |  upkeep |   salary |      cash |  appt | stress | loyal | heat | roster |  rep | foot | sent");

        foreach (var s in _samples)
        {
            GD.Print($"{s.Night,2} | {s.Served,3} ({s.TurnedAway,1}) | " +
                     $"{s.Revenue,8:F0} | {s.Commission,7:F0} | {s.Upkeep,7:F0} | {s.Salaries,8:F0} | " +
                     $"{s.CashAfter,9:F0} | {s.AvgAppointment,5:F1} | {s.AvgStress,6:F1} | " +
                     $"{s.AvgLoyalty,5:F1} | {s.Heat,4:F0} | {s.RosterSize,6} | {s.Reputation,4:F0} | {s.Footfall,4:F2} | {s.Sentiment,4:F0}");
        }

        var totalRevenue = _samples.Sum(s => s.Revenue);
        var totalCosts = _samples.Sum(s => s.Upkeep + s.Salaries + s.Commission);
        var startCash = 1000.0;
        var endCash = _samples.Count > 0 ? _samples[^1].CashAfter : startCash;

        GD.Print("\n─── Totals ───");
        GD.Print($"  revenue            ${totalRevenue,10:F0}");
        GD.Print($"  direct costs       ${totalCosts,10:F0}   ({totalCosts / Math.Max(1, totalRevenue):P0} of revenue)");
        GD.Print($"  cash {startCash:F0} → {endCash:F0}   (net {endCash - startCash:+#;-#;0} over {Nights} nights)");
        GD.Print($"  mean revenue/night ${totalRevenue / Math.Max(1, _samples.Count),10:F0}");

        GD.Print("\n─── Encounter outcomes ───");
        var total = _qualityTotals.Values.Sum();
        foreach (EncounterQuality band in Enum.GetValues<EncounterQuality>())
        {
            var count = _qualityTotals.GetValueOrDefault(band);
            var pct = total == 0 ? 0.0 : count / (double)total;
            var bar = new string('█', (int)Math.Round(pct * 40));
            GD.Print($"  {band,-12} {count,4}  {pct,6:P0}  {bar}");
        }

        GD.Print("\n─── Verdict ───");
        Verdict("costs are 55–85% of revenue",
            totalCosts / Math.Max(1, totalRevenue) is > 0.55 and < 0.85);
        Verdict("Good+Exceptional is 15–45% of outcomes",
            Share(EncounterQuality.Good) + Share(EncounterQuality.Exceptional) is > 0.15 and < 0.45);
        Verdict("Adequate is the modal band",
            _qualityTotals.GetValueOrDefault(EncounterQuality.Adequate) ==
                _qualityTotals.Values.DefaultIfEmpty(0).Max());
        Verdict("Disastrous stays under 10%", Share(EncounterQuality.Disastrous) < 0.10);
        // Against gross, not against the opening balance: a multiple of
        // starting cash silently tightens as the run gets longer, so the same
        // healthy economy failed at 50 nights and passed at 20.
        Verdict("the house is solvent but not printing money",
            endCash > startCash && endCash - startCash < totalRevenue * 0.5);

        // Furniture wears out and only the player replaces it, so a long run
        // is the only place the drift shows. Over 20 nights it is invisible.
        var apptFirst = _samples.Count > 0 ? _samples[0].AvgAppointment : 0f;
        var apptLast = _samples.Count > 0 ? _samples[^1].AvgAppointment : 0f;

        GD.Print($"\n  appointment {apptFirst:F1} → {apptLast:F1} " +
                 $"({apptLast - apptFirst:+0.0;-0.0;0} over {_samples.Count} nights, " +
                 $"unattended)");

        GD.Print($"  crises faced {_crisesAnswered}");

        // The Ledger is meant to ask the player something. It asked nothing
        // at all until the thresholds were lowered, and the only reason
        // anyone noticed was that this line printed a zero. Assert it, so a
        // future tuning change cannot quietly switch the pacing back off.
        //
        // Upper bound as well as lower: a crisis every night is nagging, not
        // pacing. Roughly one per 10–25 nights is what this range encodes.
        var expectedFloor = Nights >= 40 ? 2 : 1;

        Verdict($"crises actually fire, but not constantly " +
                $"({_crisesAnswered} in {Nights} nights)",
            _crisesAnswered >= expectedFloor && _crisesAnswered <= Nights / 5);

        // Reputation used to be a one-way street: arrivals scale with it, so
        // a shock lowered the number of chances to earn it back. It fell 83 →
        // 25 over fifty nights and stayed there. Both halves are asserted —
        // that a good house still reaches a high standing, and that a bad
        // stretch is something it can climb out of.
        var repPeak = _samples.Count > 0 ? _samples.Max(s => s.Reputation) : 0f;
        var repTrough = _samples.Count > 0 ? _samples.Min(s => s.Reputation) : 0f;
        var repFinal = _samples.Count > 0 ? _samples[^1].Reputation : 0f;

        GD.Print($"  reputation peak {repPeak:F0}, trough {repTrough:F0}, " +
                 $"final {repFinal:F0}");

        Verdict($"a well-run stretch earns real standing (peak {repPeak:F0})",
            repPeak >= 70f);

        Verdict($"and a bad one is survivable (trough {repTrough:F0} → {repFinal:F0})",
            _samples.Count < 30 || repFinal > repTrough + 2f);

        Verdict("furniture wear does not collapse the house unattended",
            apptFirst <= 0f || apptLast > apptFirst * 0.6f);

        GetTree().Quit();
    }

    private double Share(EncounterQuality band)
    {
        var total = _qualityTotals.Values.Sum();
        return total == 0 ? 0 : _qualityTotals.GetValueOrDefault(band) / (double)total;
    }

    private static void Verdict(string label, bool ok) =>
        GD.Print($"  {(ok ? "OK  " : "OFF ")} {label}");
}
