using Godot;
using System;
using System.Collections.Generic;

/// <summary>Music state driven by game tension.</summary>
public enum MusicState { Ambient, Tense, Action, Silence }

/// <summary>
/// Manages background music stems and sound effects with dynamic
/// crossfading based on Heat level and game events.
/// Heat-driven states: ambient jazz (0-40), tense noir (41-70),
/// frantic brass (71+ or active raid/strike).
/// Exposes global volume controls for Master, Music, SFX, Ambient.
/// </summary>
public partial class AudioManager : Node
{
    [Signal] public delegate void OnMusicStateChangedEventHandler(int oldState, int newState);

    // ── Volume Controls (0-100) ───────────────────────────────────────
    private float _masterVolume = 80f;
    private float _musicVolume = 75f;
    private float _sfxVolume = 85f;
    private float _ambientVolume = 60f;

    public float MasterVolume  { get => _masterVolume;  set => SetVolume(ref _masterVolume, value, AudioBus.Master); }
    public float MusicVolume   { get => _musicVolume;   set => SetVolume(ref _musicVolume, value, AudioBus.Music); }
    public float SfxVolume     { get => _sfxVolume;     set => SetVolume(ref _sfxVolume, value, AudioBus.Sfx); }
    public float AmbientVolume { get => _ambientVolume; set => SetVolume(ref _ambientVolume, value, AudioBus.Ambient); }

    // ── Music State ────────────────────────────────────────────────────
    private MusicState _currentState = MusicState.Ambient;
    private MusicState _targetState = MusicState.Ambient;
    private float _crossfadeProgress = 1f;
    private float _crossfadeSpeed = 0.5f;

    private readonly Dictionary<MusicState, AudioStreamPlayer> _players = new();
    private AudioStreamPlayer _activePlayer;
    private AudioStreamPlayer _fadingPlayer;

    private static class AudioBus
    {
        public const string Master  = "Master";
        public const string Music   = "Music";
        public const string Sfx     = "SFX";
        public const string Ambient = "Ambient";
    }

    public MusicState CurrentMusicState => _currentState;

    public override void _Ready()
    {
        CreateAudioBuses();
        CreateMusicPlayers();

        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnHeatChanged += OnHeatChanged;

        GD.Print("[AudioManager] Initialized. State: Ambient.");
    }

    public override void _Process(double delta)
    {
        // Evaluate target music state
        EvaluateMusicState();

        // Crossfade between music players
        if (_crossfadeProgress < 1f)
        {
            _crossfadeProgress = Mathf.Min(1f, _crossfadeProgress + _crossfadeSpeed * (float)delta);

            if (_activePlayer != null)
                _activePlayer.VolumeDb = LinearToDb(Mathf.Lerp(-40f, 0f, _crossfadeProgress));
            if (_fadingPlayer != null)
                _fadingPlayer.VolumeDb = LinearToDb(Mathf.Lerp(0f, -40f, _crossfadeProgress));

            if (_crossfadeProgress >= 1f)
            {
                _fadingPlayer?.Stop();
                _fadingPlayer = null;
            }
        }
    }

    public override void _ExitTree()
    {
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnHeatChanged -= OnHeatChanged;
    }

    private void OnHeatChanged(float newValue, float delta)
    {
        EvaluateMusicState();
    }

    // ── Music State Evaluation ─────────────────────────────────────────

    private void EvaluateMusicState()
    {
        float heat = GameStateManager.Instance?.Heat ?? 0f;
        bool raidActive = IsRaidActive();
        bool strikeActive = IsStrikeActive();

        MusicState desired;
        if (raidActive || strikeActive)
            desired = MusicState.Action;
        else if (heat > 70f)
            desired = MusicState.Tense;
        else
            desired = MusicState.Ambient;

        if (desired != _targetState)
        {
            var old = _currentState;
            _targetState = desired;
            BeginCrossfade(desired);
            EmitSignal(SignalName.OnMusicStateChanged, (int)old, (int)desired);
        }
    }

    private void BeginCrossfade(MusicState newState)
    {
        _fadingPlayer = _activePlayer;
        _activePlayer = _players.TryGetValue(newState, out var player) ? player : null;
        _crossfadeProgress = 0f;
        _currentState = newState;

        _activePlayer?.Play();
        GD.Print($"[AudioManager] Crossfading to {newState}.");
    }

    // ── SFX ────────────────────────────────────────────────────────────

    public void PlaySfx(string sfxName, float pitchVariation = 0.1f)
    {
        var player = new AudioStreamPlayer();
        player.Bus = AudioBus.Sfx;
        player.VolumeDb = LinearToDb(_sfxVolume / 100f * _masterVolume / 100f);
        player.PitchScale = 1f + (GD.Randf() - 0.5f) * pitchVariation;
        AddChild(player);
        player.Finished += () => player.QueueFree();
        player.Play();
    }

    public void PlayAmbient(string name)
    {
        var player = new AudioStreamPlayer();
        player.Bus = AudioBus.Ambient;
        player.VolumeDb = LinearToDb(_ambientVolume / 100f * _masterVolume / 100f);
        AddChild(player);
        player.Play();
    }

    // ── Audio Setup ────────────────────────────────────────────────────

    private void CreateAudioBuses()
    {
        // Buses are assumed to exist in the project's audio bus layout.
        // If they don't exist, audio will play on the Master bus.
        int masterIdx = AudioServer.GetBusIndex(AudioBus.Master);
        if (masterIdx >= 0) AudioServer.SetBusVolumeDb(masterIdx, LinearToDb(_masterVolume / 100f));
    }

    private void CreateMusicPlayers()
    {
        foreach (MusicState state in Enum.GetValues<MusicState>())
        {
            if (state == MusicState.Silence) continue;
            var player = new AudioStreamPlayer();
            player.Bus = AudioBus.Music;
            player.VolumeDb = -40f; // start silent
            AddChild(player);
            _players[state] = player;
        }
    }

    // ── Volume Helpers ─────────────────────────────────────────────────

    private void SetVolume(ref float field, float value, string bus)
    {
        field = Mathf.Clamp(value, 0f, 100f);
        int idx = AudioServer.GetBusIndex(bus);
        if (idx >= 0)
            AudioServer.SetBusVolumeDb(idx, LinearToDb((field / 100f) * (_masterVolume / 100f)));
    }

    private static float LinearToDb(float linear)
    {
        if (linear <= 0.0001f) return -80f;
        return Mathf.Clamp(20f * Mathf.Log(linear), -80f, 24f);
    }

    private bool IsRaidActive()
    {
        var hs = GetTree()?.Root?.FindChild("HeatSystem", true, false) as HeatSystem;
        return hs != null && hs.Heat > 85f;
    }

    private bool IsStrikeActive()
    {
        var um = GetTree()?.Root?.FindChild("UnionizationManager", true, false) as UnionizationManager;
        return um?.StrikeActive ?? false;
    }

    public override string ToString() =>
        $"[AudioManager] Music={_currentState} Master={_masterVolume:F0}%";
}
