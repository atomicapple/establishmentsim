using Godot;
using System;
using System.Collections.Generic;

/// <summary>Lifecycle beat of a single cloud.</summary>
public enum CloudPhase
{
    /// <summary>Puffing into existence.</summary>
    Rising,

    /// <summary>Holding, billowing gently while the encounter runs.</summary>
    Billowing,

    /// <summary>Short flare on resolution, tinted by the outcome.</summary>
    Bursting,

    /// <summary>Dissipating.</summary>
    Fading,

    /// <summary>Done. Ready to be recycled.</summary>
    Finished
}

/// <summary>
/// One soft blob inside a cloud. Several of these overlapping, each drifting
/// on its own noise offset, is what reads as "billowing" without a texture.
/// </summary>
internal sealed class CloudPuff
{
    public Vector2 Offset;
    public float Radius;
    public float Phase;
    public float DriftSeed;
    public float RiseRate;
}

/// <summary>
/// One running effect. Pooled — instances are reused rather than allocated
/// per encounter, and nothing here owns a Node, so there is nothing to leak.
/// </summary>
internal sealed class CloudInstance
{
    public string EncounterId = "";
    public Vector3I Cell;
    public Vector2 Anchor;

    public CloudPhase Phase = CloudPhase.Rising;
    public float PhaseTime;
    public float Elapsed;
    public float Duration = 1f;

    public bool Resolved;
    public EncounterQuality Quality = EncounterQuality.Adequate;

    public Color Tint;
    public float Density = 0.6f;
    public float DurationScale = 1f;

    public float Seed;
    public bool InUse;

    public readonly List<CloudPuff> Puffs = new();
}

/// <summary>
/// A small speech bubble for the pre-encounter conversation beat. Drawn, not
/// instanced, for the same reason as the clouds.
/// </summary>
internal sealed class ConversationBeat
{
    public Vector3I Cell;
    public string Line = "";
    public float Elapsed;
    public float Duration = 2f;
    public bool InUse;
}

/// <summary>
/// The abstract "something is happening in there" effect.
///
/// The encounter itself is never depicted. What the player sees is a puff of
/// smoke over the room, and on resolution that puff retints, flares and
/// dissipates. Colour, density and duration all come from
/// <see cref="EncounterResolver.GetVfxParameters"/>, so this is ONE effect
/// driven by parameters rather than five hand-authored animations — adding an
/// outcome band costs nothing here.
///
/// Everything is drawn procedurally in <see cref="_Draw"/> against
/// <see cref="IsoTheme"/>'s projection, so the cloud lands on exactly the same
/// screen point as the room the dollhouse renderer drew. No external assets,
/// no per-effect child nodes, nothing to leak.
/// </summary>
public partial class EncounterCloudVfx : Node2D
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>Fired once a cloud has fully dissipated and been recycled.</summary>
    [Signal]
    public delegate void OnCloudFinishedEventHandler(string encounterId, int quality);

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>How far above the cell centre the cloud floats, in pixels.</summary>
    [Export] public float CloudLift { get; set; } = 44f;

    /// <summary>Base radius of the cloud body before density scaling.</summary>
    [Export] public float CloudRadius { get; set; } = 26f;

    /// <summary>Soft blobs in a cloud at full density.</summary>
    [Export] public int MaxPuffs { get; set; } = 7;

    /// <summary>Seconds the cloud takes to puff into existence.</summary>
    [Export] public float RiseSeconds { get; set; } = 0.35f;

    /// <summary>Seconds the resolution flare lasts, before duration scaling.</summary>
    [Export] public float BurstSeconds { get; set; } = 0.45f;

    /// <summary>Seconds the cloud takes to dissipate, before duration scaling.</summary>
    [Export] public float FadeSeconds { get; set; } = 0.9f;

    /// <summary>
    /// Grace period past the stated duration before an unresolved cloud gives
    /// up and dissipates on its own. Without it a dropped resolve signal would
    /// leave a puff hanging over the room for the rest of the night.
    /// </summary>
    [Export] public float HoldGraceSeconds { get; set; } = 8f;

    /// <summary>How far noise pushes each blob around, in pixels.</summary>
    [Export] public float DriftAmount { get; set; } = 7f;

    /// <summary>Tint used while the outcome is still unknown — neutral smoke.</summary>
    [Export] public Color UnresolvedTint { get; set; } = new(0.72f, 0.70f, 0.74f);

    /// <summary>Peak opacity of the cloud body.</summary>
    [Export] public float CloudOpacity { get; set; } = 0.68f;

    // ── State ──────────────────────────────────────────────────────────

    private readonly List<CloudInstance> _clouds = new();
    private readonly List<ConversationBeat> _beats = new();
    private readonly RandomNumberGenerator _rng = new();
    private readonly FastNoiseLite _noise = new();

    private float _time;

    /// <summary>Clouds currently running. Useful for tests and debug overlays.</summary>
    public int ActiveCloudCount
    {
        get
        {
            int n = 0;
            foreach (var c in _clouds) if (c.InUse) n++;
            return n;
        }
    }

    /// <summary>Speech bubbles currently on screen.</summary>
    public int ActiveBeatCount
    {
        get
        {
            int n = 0;
            foreach (var b in _beats) if (b.InUse) n++;
            return n;
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready()
    {
        _rng.Randomize();

        _noise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _noise.Frequency = 0.9f;

        // Sits above the building and the pawns; the cloud is a status
        // indicator and must never be occluded by furniture.
        ZIndex = 120;

        GD.Print("[EncounterCloudVfx] Ready.");
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _time += dt;

        bool anything = false;

        for (int i = 0; i < _clouds.Count; i++)
        {
            var cloud = _clouds[i];
            if (!cloud.InUse) continue;

            anything = true;
            AdvanceCloud(cloud, dt);
        }

        for (int i = 0; i < _beats.Count; i++)
        {
            var beat = _beats[i];
            if (!beat.InUse) continue;

            anything = true;
            beat.Elapsed += dt;
            if (beat.Elapsed >= beat.Duration) beat.InUse = false;
        }

        if (anything) QueueRedraw();
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Begin a cloud over <paramref name="cell"/>. Safe to call twice with the
    /// same id — the existing cloud is simply restarted rather than doubled.
    /// </summary>
    /// <param name="encounterId">Key used later by <see cref="ResolveCloud"/>.</param>
    /// <param name="cell">Room tile. Z is the floor index.</param>
    /// <param name="duration">Expected seconds until resolution.</param>
    public void StartCloud(string encounterId, Vector3I cell, float duration)
    {
        if (string.IsNullOrEmpty(encounterId)) return;

        var cloud = Find(encounterId) ?? Rent();

        cloud.EncounterId = encounterId;
        cloud.Cell = cell;
        cloud.Anchor = IsoTheme.GridToScreen(cell) + new Vector2(0f, -CloudLift);

        cloud.Phase = CloudPhase.Rising;
        cloud.PhaseTime = 0f;
        cloud.Elapsed = 0f;
        cloud.Duration = Mathf.Max(0.2f, duration);

        cloud.Resolved = false;
        cloud.Quality = EncounterQuality.Adequate;
        cloud.Tint = UnresolvedTint;
        cloud.Density = 0.6f;
        cloud.DurationScale = 1f;
        cloud.Seed = _rng.Randf() * 100f;
        cloud.InUse = true;

        BuildPuffs(cloud);
        QueueRedraw();
    }

    /// <summary>
    /// Resolve a running cloud. The outcome's presentation parameters are read
    /// straight from <see cref="EncounterResolver.GetVfxParameters"/> so the
    /// cloud and the Ledger's bar chart always agree on what a quality band
    /// looks like.
    /// </summary>
    public void ResolveCloud(string encounterId, EncounterQuality quality)
    {
        var cloud = Find(encounterId);
        if (cloud == null) return;

        ApplyResolution(cloud, quality);
        QueueRedraw();
    }

    /// <summary>
    /// A small speech bubble over the room — the brief conversation beat that
    /// precedes an encounter. Purely decorative; it expires on its own.
    /// </summary>
    public void ShowConversationBeat(Vector3I cell, string line, float duration = 2.5f)
    {
        if (string.IsNullOrEmpty(line)) return;

        ConversationBeat beat = null;
        foreach (var candidate in _beats)
        {
            if (candidate.InUse) continue;
            beat = candidate;
            break;
        }

        if (beat == null)
        {
            beat = new ConversationBeat();
            _beats.Add(beat);
        }

        beat.Cell = cell;
        beat.Line = line;
        beat.Elapsed = 0f;
        beat.Duration = Mathf.Max(0.3f, duration);
        beat.InUse = true;

        QueueRedraw();
    }

    /// <summary>
    /// Stop and recycle everything. Instances are kept for reuse rather than
    /// freed, which is the whole point of the pool — a busy night restarts the
    /// same handful of clouds hundreds of times.
    /// </summary>
    public void ClearAll()
    {
        foreach (var cloud in _clouds)
        {
            cloud.InUse = false;
            cloud.Phase = CloudPhase.Finished;
            cloud.EncounterId = "";
        }

        foreach (var beat in _beats)
            beat.InUse = false;

        QueueRedraw();
    }

    /// <summary>Whether a cloud with this id is currently running.</summary>
    public bool IsCloudActive(string encounterId) => Find(encounterId) != null;

    // ── Pool ───────────────────────────────────────────────────────────

    private CloudInstance Find(string encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return null;

        foreach (var cloud in _clouds)
        {
            if (cloud.InUse && cloud.EncounterId == encounterId) return cloud;
        }

        return null;
    }

    private CloudInstance Rent()
    {
        foreach (var cloud in _clouds)
        {
            if (!cloud.InUse) return cloud;
        }

        var fresh = new CloudInstance();
        _clouds.Add(fresh);
        return fresh;
    }

    private void BuildPuffs(CloudInstance cloud)
    {
        int wanted = Mathf.Max(3, Mathf.RoundToInt(MaxPuffs * Mathf.Clamp(0.5f + cloud.Density, 0.4f, 1.4f)));

        cloud.Puffs.Clear();

        for (int i = 0; i < wanted; i++)
        {
            float angle = Mathf.Tau * i / wanted + _rng.Randf() * 0.4f;
            float spread = CloudRadius * (0.25f + _rng.Randf() * 0.55f);

            cloud.Puffs.Add(new CloudPuff
            {
                Offset = new Vector2(Mathf.Cos(angle) * spread, Mathf.Sin(angle) * spread * 0.62f),
                Radius = CloudRadius * (0.45f + _rng.Randf() * 0.5f),
                Phase = _rng.Randf() * Mathf.Tau,
                DriftSeed = _rng.Randf() * 50f,
                RiseRate = 4f + _rng.Randf() * 6f
            });
        }
    }

    // ── Simulation ─────────────────────────────────────────────────────

    private void ApplyResolution(CloudInstance cloud, EncounterQuality quality)
    {
        var (tint, density, durationScale) = EncounterResolver.GetVfxParameters(quality);

        cloud.Quality = quality;
        cloud.Tint = tint;
        cloud.Density = Mathf.Clamp(density, 0.1f, 1f);
        cloud.DurationScale = Mathf.Max(0.2f, durationScale);
        cloud.Resolved = true;

        cloud.Phase = CloudPhase.Bursting;
        cloud.PhaseTime = 0f;
    }

    private void AdvanceCloud(CloudInstance cloud, float dt)
    {
        cloud.Elapsed += dt;
        cloud.PhaseTime += dt;

        switch (cloud.Phase)
        {
            case CloudPhase.Rising:
                if (cloud.PhaseTime >= RiseSeconds)
                {
                    cloud.Phase = CloudPhase.Billowing;
                    cloud.PhaseTime = 0f;
                }
                break;

            case CloudPhase.Billowing:
                // A resolve signal that never arrives must not strand the puff.
                if (cloud.Elapsed >= cloud.Duration + HoldGraceSeconds)
                    ApplyResolution(cloud, EncounterQuality.Adequate);
                break;

            case CloudPhase.Bursting:
                if (cloud.PhaseTime >= BurstSeconds * cloud.DurationScale)
                {
                    cloud.Phase = CloudPhase.Fading;
                    cloud.PhaseTime = 0f;
                }
                break;

            case CloudPhase.Fading:
                if (cloud.PhaseTime >= FadeSeconds * cloud.DurationScale)
                {
                    cloud.Phase = CloudPhase.Finished;
                    cloud.InUse = false;

                    var id = cloud.EncounterId;
                    cloud.EncounterId = "";
                    EmitSignal(SignalName.OnCloudFinished, id, (int)cloud.Quality);
                }
                break;
        }
    }

    // ── Drawing ────────────────────────────────────────────────────────

    public override void _Draw()
    {
        foreach (var cloud in _clouds)
        {
            if (!cloud.InUse) continue;
            DrawCloud(cloud);
        }

        foreach (var beat in _beats)
        {
            if (!beat.InUse) continue;
            DrawBeat(beat);
        }
    }

    private void DrawCloud(CloudInstance cloud)
    {
        float grow = cloud.Phase == CloudPhase.Rising
            ? Mathf.Clamp(cloud.PhaseTime / Mathf.Max(0.01f, RiseSeconds), 0f, 1f)
            : 1f;

        // Ease-out so the puff arrives with a little pop rather than a ramp.
        grow = 1f - (1f - grow) * (1f - grow);

        float burst = 0f;
        if (cloud.Phase == CloudPhase.Bursting)
            burst = Mathf.Sin(Mathf.Pi * Mathf.Clamp(
                cloud.PhaseTime / Mathf.Max(0.01f, BurstSeconds * cloud.DurationScale), 0f, 1f));

        float fade = 1f;
        if (cloud.Phase == CloudPhase.Fading)
            fade = 1f - Mathf.Clamp(
                cloud.PhaseTime / Mathf.Max(0.01f, FadeSeconds * cloud.DurationScale), 0f, 1f);

        float scale = grow * (1f + burst * 0.45f);
        float alpha = CloudOpacity * grow * fade * (0.55f + cloud.Density * 0.65f);

        // The cloud drifts upward as it dissipates.
        float lift = cloud.Phase == CloudPhase.Fading ? (1f - fade) * 22f : 0f;
        var centre = cloud.Anchor + new Vector2(0f, -lift);

        var tint = cloud.Tint;

        foreach (var puff in cloud.Puffs)
        {
            float drift = _noise.GetNoise2D(puff.DriftSeed + cloud.Seed, _time * 0.55f);
            float driftY = _noise.GetNoise2D(puff.DriftSeed + cloud.Seed + 31.7f, _time * 0.45f);

            float pulse = 1f + Mathf.Sin(_time * 2.1f + puff.Phase) * 0.13f;

            var pos = centre
                    + puff.Offset * scale
                    + new Vector2(drift, driftY * 0.6f) * DriftAmount
                    - new Vector2(0f, puff.RiseRate * 0.06f * Mathf.Sin(_time + puff.Phase));

            float r = puff.Radius * scale * pulse;
            if (r <= 0.5f) continue;

            // Three concentric passes fake a soft edge without a shader.
            DrawCircle(pos, r, new Color(tint.R, tint.G, tint.B, alpha * 0.22f));
            DrawCircle(pos, r * 0.72f, new Color(tint.R, tint.G, tint.B, alpha * 0.28f));
            DrawCircle(pos, r * 0.44f, new Color(tint.R, tint.G, tint.B, alpha * 0.34f));
        }

        if (cloud.Resolved)
            DrawGlyphs(cloud, centre, alpha, burst, fade);
    }

    /// <summary>
    /// Small abstract markers floating above the cloud. They are the only
    /// thing that differs by outcome beyond colour, and they stay deliberately
    /// tiny and stylised — a sparkle, a heart, a dull puff, a red flash.
    /// </summary>
    private void DrawGlyphs(CloudInstance cloud, Vector2 centre, float alpha, float burst, float fade)
    {
        float rise = (cloud.PhaseTime + (cloud.Phase == CloudPhase.Fading ? BurstSeconds : 0f)) * 26f;
        var top = centre + new Vector2(0f, -CloudRadius * 1.15f - rise * 0.5f);

        float glyphAlpha = Mathf.Clamp(alpha * 1.7f + burst * 0.4f, 0f, 1f) * fade;
        if (glyphAlpha <= 0.02f) return;

        switch (cloud.Quality)
        {
            case EncounterQuality.Exceptional:
                DrawHeart(top + new Vector2(-11f, -3f), 6.5f, WithAlpha(IsoTheme.Gold, glyphAlpha));
                DrawSparkle(top + new Vector2(9f, -8f), 7f, WithAlpha(IsoTheme.LampWarm, glyphAlpha));
                DrawSparkle(top + new Vector2(1f, 6f), 4.5f, WithAlpha(IsoTheme.Gold, glyphAlpha * 0.8f));
                break;

            case EncounterQuality.Good:
                DrawSparkle(top + new Vector2(-7f, 0f), 5.5f, WithAlpha(IsoTheme.LampWarm, glyphAlpha));
                DrawHeart(top + new Vector2(8f, -4f), 5f, WithAlpha(IsoTheme.GoldDim, glyphAlpha * 0.9f));
                break;

            case EncounterQuality.Adequate:
                // A plain grey puff: it happened, that is all there is to say.
                DrawCircle(top, 5f, WithAlpha(IsoTheme.TextMuted, glyphAlpha * 0.55f));
                DrawCircle(top + new Vector2(6f, 2f), 3.5f, WithAlpha(IsoTheme.TextMuted, glyphAlpha * 0.4f));
                break;

            case EncounterQuality.Poor:
                DrawCircle(top, 5.5f, WithAlpha(IsoTheme.FacadeShadow, glyphAlpha * 0.7f));
                DrawCircle(top + new Vector2(-6f, 2f), 4f, WithAlpha(IsoTheme.Facade, glyphAlpha * 0.6f));
                DrawCircle(top + new Vector2(5f, 3f), 3f, WithAlpha(IsoTheme.Facade, glyphAlpha * 0.5f));
                break;

            default:
                DrawFlash(top, 11f + burst * 6f, WithAlpha(IsoTheme.Danger, glyphAlpha));
                break;
        }
    }

    private void DrawSparkle(Vector2 at, float size, Color color)
    {
        // Four-point star: two tapered spokes crossed.
        var points = new[]
        {
            at + new Vector2(0f, -size),
            at + new Vector2(size * 0.26f, -size * 0.26f),
            at + new Vector2(size, 0f),
            at + new Vector2(size * 0.26f, size * 0.26f),
            at + new Vector2(0f, size),
            at + new Vector2(-size * 0.26f, size * 0.26f),
            at + new Vector2(-size, 0f),
            at + new Vector2(-size * 0.26f, -size * 0.26f)
        };

        DrawColoredPolygon(points, color);
    }

    private void DrawHeart(Vector2 at, float size, Color color)
    {
        const int steps = 18;
        var points = new Vector2[steps];

        for (int i = 0; i < steps; i++)
        {
            float t = Mathf.Tau * i / steps;
            float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
            float y = 13f * Mathf.Cos(t)
                    - 5f * Mathf.Cos(2f * t)
                    - 2f * Mathf.Cos(3f * t)
                    - Mathf.Cos(4f * t);

            points[i] = at + new Vector2(x, -y) * (size / 16f);
        }

        DrawColoredPolygon(points, color);
    }

    private void DrawFlash(Vector2 at, float size, Color color)
    {
        // A jagged ring — abstract "that went wrong", no depiction of anything.
        const int spikes = 8;
        var points = new Vector2[spikes * 2];

        for (int i = 0; i < spikes * 2; i++)
        {
            float t = Mathf.Tau * i / (spikes * 2);
            float r = (i % 2 == 0) ? size : size * 0.42f;
            points[i] = at + new Vector2(Mathf.Cos(t) * r, Mathf.Sin(t) * r * 0.8f);
        }

        DrawColoredPolygon(points, new Color(color.R, color.G, color.B, color.A * 0.75f));
    }

    private void DrawBeat(ConversationBeat beat)
    {
        var font = ThemeDB.Singleton?.FallbackFont;
        if (font == null) return;

        int fontSize = 13;

        float t = beat.Elapsed / Mathf.Max(0.01f, beat.Duration);
        float alpha = t < 0.15f
            ? t / 0.15f
            : (t > 0.8f ? Mathf.Max(0f, (1f - t) / 0.2f) : 1f);

        if (alpha <= 0.02f) return;

        var text = beat.Line;
        var textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);

        var padding = new Vector2(9f, 6f);
        var box = textSize + padding * 2f;

        var anchor = IsoTheme.GridToScreen(beat.Cell) + new Vector2(0f, -CloudLift - 42f);
        var topLeft = anchor - new Vector2(box.X * 0.5f, box.Y);

        var fill = new Color(IsoTheme.Facade.R, IsoTheme.Facade.G, IsoTheme.Facade.B, 0.92f * alpha);
        var edge = new Color(IsoTheme.Gold.R, IsoTheme.Gold.G, IsoTheme.Gold.B, 0.85f * alpha);

        DrawRect(new Rect2(topLeft, box), fill, true);
        DrawRect(new Rect2(topLeft, box), edge, false, 1.5f);

        // Little tail pointing down at the room.
        var tail = new[]
        {
            anchor + new Vector2(-6f, 0f),
            anchor + new Vector2(6f, 0f),
            anchor + new Vector2(0f, 9f)
        };
        DrawColoredPolygon(tail, fill);

        DrawString(
            font,
            topLeft + new Vector2(padding.X, padding.Y + font.GetAscent(fontSize)),
            text,
            HorizontalAlignment.Left,
            -1,
            fontSize,
            WithAlpha(IsoTheme.TextPrimary, alpha));
    }

    private static Color WithAlpha(Color color, float alpha) =>
        new(color.R, color.G, color.B, Mathf.Clamp(alpha, 0f, 1f));

    public override string ToString() =>
        $"[EncounterCloudVfx] {ActiveCloudCount} active / {_clouds.Count} pooled, " +
        $"{ActiveBeatCount} beats";
}
