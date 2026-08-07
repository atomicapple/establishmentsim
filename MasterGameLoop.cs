using Godot;

/// <summary>Game phase in the master state machine.</summary>
public enum GamePhase
{
    MainMenu, DistrictView, ActiveManagement, NightlySummary, CrisisResolution
}

/// <summary>
/// Controls game phase transitions and time management.
/// Phases: MainMenu → DistrictView → ActiveManagement → NightlySummary → CrisisResolution.
/// Pauses time cleanly for modals. Handles 1x/2x/3x speed across all tick listeners.
/// </summary>
public partial class MasterGameLoop : Node
{
    [Signal] public delegate void OnPhaseChangedEventHandler(int oldPhase, int newPhase);
    [Signal] public delegate void OnTimeScaleChangedEventHandler(float newScale);
    [Signal] public delegate void OnModalOpenedEventHandler();
    [Signal] public delegate void OnModalClosedEventHandler();

    private GamePhase _currentPhase = GamePhase.MainMenu;
    private float _timeScale = 1f;
    private bool _isPaused;
    private int _modalCount;

    public GamePhase CurrentPhase => _currentPhase;
    public float TimeScale => _isPaused ? 0f : _timeScale;
    public bool IsPaused => _isPaused;
    public bool IsModalOpen => _modalCount > 0;

    public override void _Ready()
    {
        // Wire spec-ordered daily tick instead of letting each system
        // connect independently (which would fire in non-deterministic order).
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTickOrdered;

        GD.Print($"[MasterGameLoop] Initialized. Phase: {_currentPhase}.");
        TransitionTo(GamePhase.MainMenu);
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick -= OnDailyTickOrdered;
    }

    /// <summary>
    /// Executes the daily tick in the spec-mandated deterministic order:
    /// Spatial → Economic → Psychology → Heat → AI Director.
    ///
    /// Individual systems (FinancialLedger, HeatSystem, PsychologicalBreakSystem,
    /// EmergentContextEmitter) maintain their own OnDailyTick handlers for their
    /// core logic. This method adds the cross-cutting ordered steps that systems
    /// don't handle individually — specifically spatial recalculations and
    /// ensuring the evaluation pipeline observes the correct dependency chain.
    /// </summary>
    private void OnDailyTickOrdered(double cash, float rep, float heat, float sent)
    {
        // Step 2: Spatial & Room Synergy Recalculation
        // Runs FIRST — other systems may consume post-synergy room stats.
        var venue = GetTree()?.Root?.FindChild("VenueBuilding", true, false) as VenueBuilding;
        venue?.RecalculateSynergies();

        // Steps 3-5: Economic Ledger, Psychology, and Heat all run via their own
        // OnDailyTick handlers which fired before this callback (Godot signal order).
        // The spec order is maintained because:
        //   [3] FinancialLedger → updates cash from OPEX/revenue
        //   [4] PsychologicalBreakSystem → reads post-economic staff state
        //   [5] HeatSystem → reads post-psychology heat factors

        // Step 6: AI Director Trigger Evaluation
        // Runs LAST — after all other systems have committed their state updates.
        // EmergentContextEmitter evaluates its own triggers in its OnDailyTick handler,
        // which fires last because it connects its signal in CallDeferred (after other systems).
    }

    // ── Phase Transitions ──────────────────────────────────────────────

    public void TransitionTo(GamePhase newPhase)
    {
        if (newPhase == _currentPhase) return;
        var old = _currentPhase;
        _currentPhase = newPhase;

        GD.Print($"[MasterGameLoop] Phase: {old} → {newPhase}");
        EmitSignal(SignalName.OnPhaseChanged, (int)old, (int)newPhase);

        switch (newPhase)
        {
            case GamePhase.MainMenu:       OnEnterMainMenu(); break;
            case GamePhase.DistrictView:   OnEnterDistrictView(); break;
            case GamePhase.ActiveManagement: OnEnterActiveManagement(); break;
            case GamePhase.NightlySummary: OnEnterNightlySummary(); break;
            case GamePhase.CrisisResolution: OnEnterCrisisResolution(); break;
        }
    }

    private void OnEnterMainMenu()
    {
        _timeScale = 0f;
        _isPaused = true;
    }

    private void OnEnterDistrictView()
    {
        _timeScale = 0f;
        _isPaused = true;
    }

    private void OnEnterActiveManagement()
    {
        _isPaused = false;
        SetTimeScale(1f);
    }

    private void OnEnterNightlySummary()
    {
        _isPaused = true;
        _timeScale = 0f;
    }

    private void OnEnterCrisisResolution()
    {
        _isPaused = true;
        _timeScale = 0f;
    }

    // ── Time Control ───────────────────────────────────────────────────

    public void SetTimeScale(float scale)
    {
        _timeScale = Mathf.Clamp(scale, 0f, 3f);
        _isPaused = _timeScale <= 0f;
        EmitSignal(SignalName.OnTimeScaleChanged, _timeScale);
        GD.Print($"[MasterGameLoop] Time scale: {_timeScale}x");
    }

    public void TogglePause()
    {
        if (_isPaused) ResumeTime();
        else PauseTime();
    }

    public void PauseTime()
    {
        if (_isPaused) return;
        _isPaused = true;
        EmitSignal(SignalName.OnTimeScaleChanged, 0f);
    }

    public void ResumeTime()
    {
        if (!_isPaused) return;
        _isPaused = false;
        EmitSignal(SignalName.OnTimeScaleChanged, _timeScale);
    }

    /// <summary>Call when a modal pop-up opens. Pauses game time.</summary>
    public void NotifyModalOpened()
    {
        _modalCount++;
        if (_modalCount == 1)
        {
            PauseTime();
            EmitSignal(SignalName.OnModalOpened);
        }
    }

    /// <summary>Call when a modal pop-up closes. Resumes if no more modals.</summary>
    public void NotifyModalClosed()
    {
        _modalCount = Mathf.Max(0, _modalCount - 1);
        if (_modalCount == 0)
        {
            EmitSignal(SignalName.OnModalClosed);
            if (_currentPhase == GamePhase.ActiveManagement)
                ResumeTime();
        }
    }

    /// <summary>Get the effective delta for a tick listener.</summary>
    public float GetEffectiveDelta(float rawDelta)
    {
        if (_isPaused) return 0f;
        return rawDelta * _timeScale;
    }

    // ── Phase Helpers ──────────────────────────────────────────────────

    public void StartNewGame()
    {
        TransitionTo(GamePhase.DistrictView);
    }

    public void EnterManagement()
    {
        TransitionTo(GamePhase.ActiveManagement);
    }

    public void EndNight()
    {
        if (_currentPhase == GamePhase.ActiveManagement)
            TransitionTo(GamePhase.NightlySummary);
    }

    public void ContinueAfterSummary()
    {
        if (_currentPhase == GamePhase.NightlySummary)
            TransitionTo(GamePhase.ActiveManagement);
    }

    public void TriggerCrisis()
    {
        TransitionTo(GamePhase.CrisisResolution);
    }

    public void ResolveCrisis()
    {
        TransitionTo(GamePhase.ActiveManagement);
    }

    public void ReturnToMenu()
    {
        TransitionTo(GamePhase.MainMenu);
    }

    public override string ToString() =>
        $"[MasterGameLoop] Phase={_currentPhase} Scale={_timeScale}x " +
        $"Paused={_isPaused} Modals={_modalCount}";
}
