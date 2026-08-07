using Godot;
using System.Collections.Generic;

/// <summary>Graphics quality tier for particle scaling.</summary>
public enum GraphicsQuality { Low, Medium, High, Ultra }

/// <summary>VFX effect type for a room.</summary>
public enum RoomVFXType { Smoke, LuxuryShimmer, AlarmFlash, Steam, Dust }

/// <summary>Active VFX instance in a room.</summary>
public class RoomVFX
{
    public Vector2I GridPosition;
    public RoomVFXType Type;
    public GpuParticles3D Particles;
    public OmniLight3D Light;
}

/// <summary>
/// Manages particle systems inside venue rooms.
/// Smoke for lounges, shimmer for luxury suites, alarm flashes for breaches.
/// Emitter counts auto-adjust based on graphics quality.
/// </summary>
public partial class VenueAtmosphereVFX : Node
{
    private readonly Dictionary<Vector2I, List<RoomVFX>> _effects = new();
    private GraphicsQuality _quality = GraphicsQuality.High;

    private static readonly Dictionary<GraphicsQuality, float> QualityMultipliers = new()
    {
        [GraphicsQuality.Low]   = 0.25f,
        [GraphicsQuality.Medium] = 0.5f,
        [GraphicsQuality.High]  = 1.0f,
        [GraphicsQuality.Ultra] = 1.5f
    };

    private const int BaseSmokeCount = 60;
    private const int BaseShimmerCount = 100;
    private const int BaseAlarmCount = 20;

    public GraphicsQuality Quality
    {
        get => _quality;
        set { _quality = value; UpdateAllEmitterCounts(); }
    }

    public override void _Ready()
    {
        GD.Print($"[VenueVFX] Initialized. Quality: {_quality}.");
    }

    /// <summary>Add smoke VFX to a lounge room.</summary>
    public void AddLoungeSmoke(Vector2I gridPos)
    {
        var vfx = CreateParticleEffect(RoomVFXType.Smoke, gridPos);
        if (vfx == null) return;

        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.4f, 0.1f, 0.4f),
            Direction = new Vector3(0, 1, 0),
            Spread = 25f,
            Gravity = new Vector3(0, 0.3f, 0),
            InitialVelocityMin = 0.3f,
            InitialVelocityMax = 0.8f,
            ScaleMin = 0.3f,
            ScaleMax = 0.9f,
            Color = new Color(0.6f, 0.55f, 0.5f, 0.3f),
            LifetimeRandomness = 0.5f
        };
        vfx.Particles.ProcessMaterial = mat;
        ApplyEmitterCount(vfx.Particles, BaseSmokeCount);
    }

    /// <summary>Add shimmer VFX to a luxury suite.</summary>
    public void AddLuxuryShimmer(Vector2I gridPos)
    {
        var vfx = CreateParticleEffect(RoomVFXType.LuxuryShimmer, gridPos);
        if (vfx == null) return;

        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.5f,
            Direction = new Vector3(0, 0.5f, 0),
            Spread = 45f,
            Gravity = new Vector3(0, -0.1f, 0),
            InitialVelocityMin = 0.1f,
            InitialVelocityMax = 0.4f,
            ScaleMin = 0.05f,
            ScaleMax = 0.15f,
            Color = new Color(1f, 0.9f, 0.6f, 0.4f),
            LifetimeRandomness = 0.8f
        };
        vfx.Particles.ProcessMaterial = mat;
        ApplyEmitterCount(vfx.Particles, BaseShimmerCount);

        // Add subtle light
        vfx.Light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.85f, 0.5f),
            LightEnergy = 0.3f,
            OmniRange = 0.8f
        };
        vfx.Particles.AddChild(vfx.Light);
    }

    /// <summary>Add alarm flash VFX for security breaches.</summary>
    public void AddAlarmFlash(Vector2I gridPos)
    {
        var vfx = CreateParticleEffect(RoomVFXType.AlarmFlash, gridPos);
        if (vfx == null) return;

        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.6f,
            Direction = new Vector3(0, 0, 0),
            Spread = 180f,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 0f,
            ScaleMin = 0.8f,
            ScaleMax = 1.5f,
            Color = new Color(1f, 0.15f, 0.1f, 0.6f),
            LifetimeRandomness = 0.3f
        };
        vfx.Particles.ProcessMaterial = mat;
        ApplyEmitterCount(vfx.Particles, BaseAlarmCount);

        // Rotating alarm light
        vfx.Light = new OmniLight3D
        {
            LightColor = new Color(1f, 0.1f, 0.05f),
            LightEnergy = 1.5f,
            OmniRange = 1.5f
        };
        vfx.Particles.AddChild(vfx.Light);
    }

    /// <summary>Remove VFX from a room.</summary>
    public void RemoveRoomVFX(Vector2I gridPos, RoomVFXType type)
    {
        if (!_effects.TryGetValue(gridPos, out var effects)) return;
        foreach (var vfx in effects.FindAll(e => e.Type == type))
            vfx.Particles?.QueueFree();
        effects.RemoveAll(e => e.Type == type);
    }

    /// <summary>Remove all VFX at a position.</summary>
    public void ClearRoomVFX(Vector2I gridPos)
    {
        if (!_effects.TryGetValue(gridPos, out var effects)) return;
        foreach (var vfx in effects) vfx.Particles?.QueueFree();
        effects.Clear();
    }

    private RoomVFX CreateParticleEffect(RoomVFXType type, Vector2I gridPos)
    {
        var particles = new GpuParticles3D();
        particles.Position = GridToWorld(gridPos);
        particles.Emitting = true;
        particles.OneShot = false;
        particles.Explosiveness = 0f;
        particles.Amount = 50;
        particles.Lifetime = 2.5f;
        particles.DrawPasses = 1;

        // Simple quad mesh for particles
        var quadMesh = new QuadMesh { Size = new Vector2(0.15f, 0.15f) };
        particles.DrawPass1 = quadMesh;

        AddChild(particles);

        var vfx = new RoomVFX { GridPosition = gridPos, Type = type, Particles = particles };
        if (!_effects.ContainsKey(gridPos))
            _effects[gridPos] = new List<RoomVFX>();
        _effects[gridPos].Add(vfx);

        return vfx;
    }

    private void ApplyEmitterCount(GpuParticles3D particles, int baseCount)
    {
        particles.Amount = (int)(baseCount * QualityMultipliers[_quality]);
    }

    private void UpdateAllEmitterCounts()
    {
        float mult = QualityMultipliers[_quality];
        foreach (var kvp in _effects)
            foreach (var vfx in kvp.Value)
                vfx.Particles.Amount = (int)(vfx.Particles.Amount / QualityMultipliers[GraphicsQuality.High] * mult);
    }

    private static Vector3 GridToWorld(Vector2I gridPos)
    {
        const float cellSize = 1.8f;
        return new Vector3((gridPos.X - 4) * cellSize, 0.5f, (gridPos.Y - 3) * cellSize);
    }

    public override string ToString() =>
        $"[VenueVFX] Effects={_effects.Count} Quality={_quality}";
}
