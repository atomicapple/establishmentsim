using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The encounter effect in 3D: an abstract puff of smoke above a room while
/// something is happening inside it.
///
/// The presentation choice this implements is deliberate and load-bearing.
/// Nothing is ever depicted — a conversation beat, then a cloud, then a
/// result. Because the encounter is a black box, the outcome is *computed*
/// rather than animated, which means all the player's agency lives in
/// preparation and adding a new outcome band costs no art. The cloud's
/// colour, density and duration are the only things that communicate how it
/// went, and they all come from
/// <see cref="EncounterResolver.GetVfxParameters"/> so this layer and the
/// Ledger's charts always agree.
/// </summary>
public partial class EncounterCloud3D : Node3D
{
    [Signal]
    public delegate void OnCloudFinishedEventHandler(string encounterId, int quality);

    /// <summary>Height above the room floor at which the cloud sits.</summary>
    [Export] public float CloudHeight { get; set; } = 2.6f;

    /// <summary>Seconds the burst-and-fade takes once an encounter resolves.</summary>
    [Export] public float ResolveSeconds { get; set; } = 1.4f;

    /// <summary>Tint used before a result is known.</summary>
    [Export] public Color UnresolvedTint { get; set; } = new(0.72f, 0.70f, 0.74f);

    private sealed class Cloud
    {
        public string Id;
        public GpuParticles3D Particles;
        public Node3D Root;
        public Label3D Glyph;
        public bool Resolved;
        public float FadeRemaining;
    }

    private readonly Dictionary<string, Cloud> _clouds = new();
    private VenueBuilding _venue;

    public int ActiveCloudCount => _clouds.Count;

    public void Bind(VenueBuilding venue) => _venue = venue;

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Begin a cloud over a room. Restarts it if the id is already running.</summary>
    public void StartCloud(string encounterId, Vector3I cell, float duration)
    {
        if (string.IsNullOrEmpty(encounterId)) return;

        if (_clouds.TryGetValue(encounterId, out var existing))
        {
            existing.Resolved = false;
            existing.FadeRemaining = 0f;
            return;
        }

        var room = _venue?.GetRoom(cell);
        var position = room != null
            ? VenueSpace.RoomCenter(room.GridPosition, room.Size)
            : VenueSpace.CellCenter(cell);

        var root = new Node3D
        {
            Name = $"Cloud_{encounterId}",
            Position = position + new Vector3(0, CloudHeight, 0)
        };

        AddChild(root);

        var particles = BuildParticles(UnresolvedTint, 0.6f);
        root.AddChild(particles);

        _clouds[encounterId] = new Cloud
        {
            Id = encounterId,
            Root = root,
            Particles = particles
        };
    }

    /// <summary>
    /// Resolve a cloud: retint from the outcome band, add its glyph, and
    /// begin the fade.
    /// </summary>
    public void ResolveCloud(string encounterId, EncounterQuality quality)
    {
        if (!_clouds.TryGetValue(encounterId, out var cloud)) return;
        if (cloud.Resolved) return;

        var (tint, density, durationScale) = EncounterResolver.GetVfxParameters(quality);

        cloud.Resolved = true;
        cloud.FadeRemaining = ResolveSeconds * Mathf.Max(0.3f, durationScale);

        if (cloud.Particles?.ProcessMaterial is ParticleProcessMaterial material)
            material.Color = tint;

        cloud.Particles.Amount = Mathf.Max(4, Mathf.RoundToInt(24 * density));
        cloud.Particles.Restart();

        cloud.Glyph = new Label3D
        {
            Text = GetGlyph(quality),
            FontSize = 64,
            PixelSize = 0.004f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = tint,
            OutlineSize = 10,
            NoDepthTest = true,
            Position = new Vector3(0, 0.5f, 0)
        };

        cloud.Root.AddChild(cloud.Glyph);

        EmitSignal(SignalName.OnCloudFinished, encounterId, (int)quality);
    }

    public void ClearAll()
    {
        foreach (var cloud in _clouds.Values) cloud.Root?.QueueFree();
        _clouds.Clear();
    }

    public bool IsCloudActive(string encounterId) =>
        !string.IsNullOrEmpty(encounterId) && _clouds.ContainsKey(encounterId);

    // ── Frame update ───────────────────────────────────────────────────

    public override void _Process(double delta)
    {
        if (_clouds.Count == 0) return;

        var finished = new List<string>();

        foreach (var cloud in _clouds.Values)
        {
            if (!cloud.Resolved) continue;

            cloud.FadeRemaining -= (float)delta;

            if (cloud.Glyph != null)
            {
                // Drift the glyph upward as it goes.
                cloud.Glyph.Position += new Vector3(0, (float)delta * 0.4f, 0);

                var alpha = Mathf.Clamp(cloud.FadeRemaining / ResolveSeconds, 0f, 1f);
                var colour = cloud.Glyph.Modulate;
                cloud.Glyph.Modulate = new Color(colour.R, colour.G, colour.B, alpha);
            }

            if (cloud.FadeRemaining <= 0f) finished.Add(cloud.Id);
        }

        foreach (var id in finished)
        {
            _clouds[id].Root?.QueueFree();
            _clouds.Remove(id);
        }
    }

    // ── Construction ───────────────────────────────────────────────────

    private static GpuParticles3D BuildParticles(Color tint, float density)
    {
        var material = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 25f,
            InitialVelocityMin = 0.25f,
            InitialVelocityMax = 0.6f,
            Gravity = new Vector3(0, 0.15f, 0),
            ScaleMin = 0.35f,
            ScaleMax = 0.85f,
            Color = tint,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.35f
        };

        var mesh = new SphereMesh { Radius = 0.22f, Height = 0.44f, RadialSegments = 8, Rings = 4 };

        mesh.Material = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 1f, 1f, 0.55f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true
        };

        return new GpuParticles3D
        {
            Amount = Mathf.Max(4, Mathf.RoundToInt(20 * density)),
            Lifetime = 1.6,
            ProcessMaterial = material,
            DrawPass1 = mesh,
            Emitting = true,
            Explosiveness = 0f
        };
    }

    /// <summary>
    /// Small abstract marker per outcome. Kept comic and tasteful — this is a
    /// status indicator, not a depiction.
    /// </summary>
    private static string GetGlyph(EncounterQuality quality) => quality switch
    {
        EncounterQuality.Exceptional => "♥",
        EncounterQuality.Good => "✦",
        EncounterQuality.Adequate => "·",
        EncounterQuality.Poor => "~",
        _ => "!"
    };

    public override string ToString() => $"[EncounterCloud3D] {_clouds.Count} active";
}
