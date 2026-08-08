using Godot;

/// <summary>
/// The one place a seed enters the simulation.
///
/// Every system used to call <c>_rng.Randomize()</c> in its own <c>_Ready</c>,
/// which meant <see cref="GameBootstrap.WorldSeed"/> seeded exactly one
/// generator — the bootstrap's own — and nothing else. The balance harness
/// therefore advertised a fixed seed and produced a different run every time:
/// three consecutive 20-night runs finished on +3537, +5497 and +5281, a 55%
/// spread. Any tuning change smaller than that was unmeasurable, and the
/// instruction to "change numbers against the harness, not intuition" was
/// resting on noise.
///
/// Systems now draw a <em>named stream</em>. Each stream is deterministic
/// given the world seed, and independent of the others — so adding a call to
/// one system's generator does not shift the sequence every other system
/// sees. That property is what makes a run comparable to the run before it
/// after the code has changed.
/// </summary>
public static class WorldRandom
{
    private static ulong _worldSeed;

    /// <summary>The seed in force. Zero until <see cref="Initialize"/> runs.</summary>
    public static ulong WorldSeed => _worldSeed;

    /// <summary>True once a seed has been fixed for this session.</summary>
    public static bool IsDeterministic { get; private set; }

    /// <summary>
    /// Bumped on every <see cref="Initialize"/>. Long-lived static streams
    /// compare against it so a second world in the same process re-seeds
    /// instead of continuing the first one's sequence.
    /// </summary>
    public static int Generation { get; private set; }

    /// <summary>
    /// Fix the seed for the whole simulation. Pass zero for a genuinely
    /// random session, which is what ordinary play wants.
    /// </summary>
    public static void Initialize(ulong seed)
    {
        IsDeterministic = seed != 0;

        if (seed == 0)
        {
            var scratch = new RandomNumberGenerator();
            scratch.Randomize();
            seed = scratch.Seed;
        }

        _worldSeed = seed;
        Generation++;

        // Godot's global generator backs GD.Randf/GD.Randi, which the
        // negotiation handler leans on heavily. Unseeded, it alone was enough
        // to make a run irreproducible.
        GD.Seed(seed);

        GD.Print($"[WorldRandom] Seed {seed}" +
                 (IsDeterministic ? " (fixed)." : " (random)."));
    }

    /// <summary>
    /// The seed for a named stream. Stable across runs, and unrelated to
    /// every other stream's.
    /// </summary>
    public static ulong SeedFor(string stream)
    {
        // Mix the name into the world seed with a splitmix64 round. Cheap,
        // and it decorrelates streams whose names differ by one character —
        // "Heat" and "Heap" must not produce neighbouring sequences.
        var z = _worldSeed + StableHash(stream) * 0x9E3779B97F4A7C15UL;

        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

        return z ^ (z >> 31);
    }

    /// <summary>
    /// FNV-1a over the name.
    ///
    /// Not <c>string.GetHashCode()</c>: .NET randomizes string hashing per
    /// process, so using it here made every stream's seed different on every
    /// launch — a fixed world seed that still produced a different run. The
    /// bug this whole class exists to fix, reintroduced one level down.
    /// </summary>
    private static ulong StableHash(string text)
    {
        var hash = 14695981039346656037UL;
        if (string.IsNullOrEmpty(text)) return hash;

        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Seed an existing generator from a named stream.</summary>
    public static void Seed(RandomNumberGenerator rng, string stream)
    {
        if (rng != null) rng.Seed = SeedFor(stream);
    }

    /// <summary>A fresh generator on a named stream.</summary>
    public static RandomNumberGenerator Stream(string stream) =>
        new() { Seed = SeedFor(stream) };
}
