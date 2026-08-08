using Godot;
using System;

/// <summary>
/// Global singleton game state manager.
/// Tracks persistent establishment metrics and broadcasts a daily tick
/// every 60 seconds of game time, pushing updated metrics to connected
/// listeners via C# signals.
///
/// Usage: Add as an Autoload in project.godot under [autoload]:
///   GameStateManager="*res://GameStateManager.cs"
/// </summary>
public partial class GameStateManager : Node
{
    // ── Singleton ──────────────────────────────────────────────────────
    public static GameStateManager Instance { get; private set; }

    // ── Signals ────────────────────────────────────────────────────────
    /// <summary>Fired every 60 seconds of game time with current metric values.</summary>
    [Signal]
    public delegate void OnDailyTickEventHandler(
        double cash,
        float reputation,
        float heat,
        float publicSentiment);

    /// <summary>Fired when Cash changes.</summary>
    [Signal]
    public delegate void OnCashChangedEventHandler(double newValue, double delta);

    /// <summary>Fired when Reputation changes.</summary>
    [Signal]
    public delegate void OnReputationChangedEventHandler(float newValue, float delta);

    /// <summary>Fired when Heat changes.</summary>
    [Signal]
    public delegate void OnHeatChangedEventHandler(float newValue, float delta);

    /// <summary>Fired when PublicSentiment changes.</summary>
    [Signal]
    public delegate void OnPublicSentimentChangedEventHandler(float newValue, float delta);

    // ── Persistent Metrics (backing fields) ────────────────────────────
    // Opening balance. Raised from 1000 when the campaign changed to start
    // with the entrance only: the player now has to build and furnish their
    // first suite before anything can be sold, and 1000 did not cover a room
    // plus a bed. See StartingCash.
    private double _cash = StartingCash;
    private float _reputation = 50.0f;
    private float _heat;
    private float _publicSentiment = 50.0f;

    // ── Public Properties with change notification ─────────────────────

    /// <summary>What a new campaign opens with.</summary>
    public const double StartingCash = 3000.0;

    /// <summary>Available cash in dollars.</summary>
    public double Cash
    {
        get => _cash;
        set
        {
            if (Math.Abs(_cash - value) < 0.005) return;
            var delta = value - _cash;
            _cash = value;
            EmitSignal(SignalName.OnCashChanged, _cash, delta);
        }
    }

    /// <summary>Establishment reputation, 0–100. Starting value: 50.</summary>
    public float Reputation
    {
        get => _reputation;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(_reputation, clamped)) return;
            var delta = clamped - _reputation;
            _reputation = clamped;
            EmitSignal(SignalName.OnReputationChanged, _reputation, delta);
        }
    }

    /// <summary>Police/authority heat level, 0–100. Starting value: 0.</summary>
    public float Heat
    {
        get => _heat;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(_heat, clamped)) return;
            var delta = clamped - _heat;
            _heat = clamped;
            EmitSignal(SignalName.OnHeatChanged, _heat, delta);
        }
    }

    /// <summary>Public sentiment toward the establishment, 0–100. Starting value: 50.</summary>
    public float PublicSentiment
    {
        get => _publicSentiment;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, 100f);
            if (Mathf.IsEqualApprox(_publicSentiment, clamped)) return;
            var delta = clamped - _publicSentiment;
            _publicSentiment = clamped;
            EmitSignal(SignalName.OnPublicSentimentChanged, _publicSentiment, delta);
        }
    }

    // ── Tick Timer ─────────────────────────────────────────────────────
    private Godot.Timer _dailyTimer;
    private int _dayCount;

    /// <summary>Elapsed in-game days since start.</summary>
    public int DayCount => _dayCount;

    /// <summary>
    /// Restore the day counter from a save. Deliberately a method rather than
    /// a setter — advancing the day is otherwise the tick's exclusive job, and
    /// a writable property invites systems to skip days as a side effect.
    /// </summary>
    public void SetDayCount(int day) => _dayCount = Mathf.Max(0, day);

    /// <summary>Interval between daily ticks in real-time seconds. Default: 60.</summary>
    public double TickIntervalSeconds
    {
        get => _dailyTimer?.WaitTime ?? 60.0;
        set
        {
            if (_dailyTimer != null)
                _dailyTimer.WaitTime = value;
        }
    }

    /// <summary>Whether the daily tick timer is currently running.</summary>
    public bool IsTicking => _dailyTimer != null && !_dailyTimer.IsStopped();

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        if (Instance != null && Instance != this)
        {
            GD.PrintErr("[GameStateManager] Duplicate singleton detected. Destroying self.");
            QueueFree();
            return;
        }

        Instance = this;

        _dailyTimer = new Godot.Timer
        {
            Name = "DailyTickTimer",
            WaitTime = 60.0,
            OneShot = false,
            ProcessCallback = Godot.Timer.TimerProcessCallback.Idle
        };
        _dailyTimer.Timeout += OnTimerTimeout;
        AddChild(_dailyTimer);
        _dailyTimer.Start();

        GD.Print($"[GameStateManager] Initialized. Day {_dayCount + 1} begins. " +
                 $"Cash=${_cash:F2} Rep={_reputation:F1} Heat={_heat:F1} Sentiment={_publicSentiment:F1}");
    }

    public override void _ExitTree()
    {
        _dailyTimer?.Stop();
        _dailyTimer = null;

        if (Instance == this)
            Instance = null;
    }

    // ── Tick Logic ─────────────────────────────────────────────────────

    private void OnTimerTimeout() => AdvanceDay();

    /// <summary>
    /// Advance the world by one day and broadcast the tick.
    ///
    /// Split out of the timer callback because the day is no longer paced by
    /// a 60-second wall clock — <see cref="NightDirector"/> calls this when
    /// the player closes the Ledger, so a day is exactly one played night.
    /// Call <see cref="PauseTick"/> to stop the legacy timer when the night
    /// loop is driving.
    /// </summary>
    public void AdvanceDay()
    {
        _dayCount++;
        GD.Print($"[GameStateManager] Day {_dayCount} tick — " +
                 $"Cash=${_cash:F2} Rep={_reputation:F1} Heat={_heat:F1} Sentiment={_publicSentiment:F1}");

        EmitSignal(SignalName.OnDailyTick, _cash, _reputation, _heat, _publicSentiment);
    }

    // ── Utility ────────────────────────────────────────────────────────

    /// <summary>Pause the daily tick cycle.</summary>
    public void PauseTick() => _dailyTimer?.Stop();

    /// <summary>Resume the daily tick cycle.</summary>
    public void ResumeTick() => _dailyTimer?.Start();

    /// <summary>
    /// Apply a batch of metric deltas at once (e.g., from an event resolution).
    /// Each property fires its own change signal if the value actually changed.
    /// </summary>
    public void ApplyDeltas(double cashDelta, float repDelta, float heatDelta, float sentimentDelta)
    {
        Cash += cashDelta;
        Reputation += repDelta;
        Heat += heatDelta;
        PublicSentiment += sentimentDelta;
    }

    /// <summary>Snapshot all metrics into a formatted string.</summary>
    public override string ToString()
    {
        return $"[GameState Day{_dayCount}] Cash=${_cash:F2} Rep={_reputation:F1} Heat={_heat:F1} Sentiment={_publicSentiment:F1}";
    }
}
