using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Loads and caches the rigged character models.
///
/// The .glb files have no .import sidecars, so <c>GD.Load</c> cannot see
/// them — they are parsed at runtime with GltfDocument instead. Parsing is
/// expensive (these are 50–60 MB each), so every model is loaded once and
/// duplicated per instance.
///
/// Adding a character is a matter of dropping a folder under
/// Assets/Characters and listing it in <see cref="Models"/>.
/// </summary>
public partial class CharacterLibrary : Node
{
    /// <summary>A character model available to the game.</summary>
    public class CharacterModel
    {
        public string Id { get; init; }
        public string DisplayName { get; init; }
        public string ScenePath { get; init; }

        /// <summary>
        /// Separate animation .glb, when the rig ships its clips apart from
        /// the mesh. Null when the model already carries its own.
        /// </summary>
        public string AnimationPath { get; init; }

        /// <summary>Uniform scale correction. Meshy rigs vary in unit size.</summary>
        public float Scale { get; init; } = 1f;
    }

    /// <summary>
    /// The roster of available models. Both current entries are merged-
    /// animation exports, so the mesh and clips live in the same file.
    /// </summary>
    public static readonly List<CharacterModel> Models = new()
    {
        new CharacterModel
        {
            Id = "elegant_dark",
            DisplayName = "Dark-haired",
            ScenePath = "res://Assets/Characters/BlackHairWhite/Meshy_AI_Meshy_Merged_Animations.glb",
            Scale = 1f
        },
        new CharacterModel
        {
            Id = "classic_blonde",
            DisplayName = "Blonde",
            ScenePath = "res://Assets/Characters/Blonde1/Meshy_AI_Meshy_Merged_Animations.glb",
            Scale = 1f
        }
    };

    // ── Animation name matching ────────────────────────────────────────

    /// <summary>
    /// Meshy names its clips inconsistently, so animations are matched by
    /// substring against these preference lists rather than by exact name.
    /// First match wins.
    /// </summary>
    private static readonly Dictionary<CharacterAnimation, string[]> AnimationHints = new()
    {
        [CharacterAnimation.Idle] = new[] { "idle", "stand", "breath" },
        [CharacterAnimation.Walk] = new[] { "walk", "stroll" },
        [CharacterAnimation.Talk] = new[] { "talk", "gesture", "agree", "wave" },
        [CharacterAnimation.Sit] = new[] { "sit", "chair" },
        [CharacterAnimation.Dance] = new[] { "dance", "groove", "hop" }
    };

    private readonly Dictionary<string, Node3D> _cache = new();

    public static CharacterLibrary Instance { get; private set; }

    public override void _Ready()
    {
        if (Instance != null && Instance != this)
        {
            QueueFree();
            return;
        }

        Instance = this;
        GD.Print($"[Characters] Ready. {Models.Count} models registered.");
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // ── Loading ────────────────────────────────────────────────────────

    /// <summary>
    /// An instance of a model, ready to add to the scene. Null if the model
    /// could not be loaded — callers must cope, since a missing art file
    /// should degrade to a placeholder rather than crash the game.
    /// </summary>
    public Node3D Instantiate(string modelId)
    {
        var model = Models.FirstOrDefault(m => m.Id == modelId) ?? Models.FirstOrDefault();
        if (model == null) return null;

        if (!_cache.TryGetValue(model.Id, out var prototype))
        {
            prototype = LoadGltf(model.ScenePath);
            if (prototype == null) return null;

            if (!string.IsNullOrEmpty(model.AnimationPath))
                MergeAnimations(prototype, model.AnimationPath);

            _cache[model.Id] = prototype;
        }

        var instance = prototype.Duplicate() as Node3D;
        if (instance == null) return null;

        instance.Scale = Vector3.One * model.Scale;
        return instance;
    }

    /// <summary>Pick a model deterministically from a stable id, so a given staff member always looks the same.</summary>
    public string PickModelFor(string stableId)
    {
        if (Models.Count == 0) return null;
        if (string.IsNullOrEmpty(stableId)) return Models[0].Id;

        var hash = 0;
        foreach (var c in stableId) hash = (hash * 31 + c) & 0x7FFFFFFF;

        return Models[hash % Models.Count].Id;
    }

    /// <summary>
    /// Parse a .glb from disk. These files have no .import sidecars, so the
    /// normal resource loader cannot see them and the document has to be
    /// built from the raw buffer.
    /// </summary>
    private static Node3D LoadGltf(string path)
    {
        try
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file == null)
            {
                GD.PrintErr($"[Characters] Cannot open {path}");
                return null;
            }

            var bytes = file.GetBuffer((long)file.GetLength());
            var basePath = path.GetBaseDir() + "/";

            var document = new GltfDocument();
            var state = new GltfState { BasePath = basePath };

            if (document.AppendFromBuffer(bytes, basePath, state) != Error.Ok)
            {
                GD.PrintErr($"[Characters] Failed to parse {path}");
                return null;
            }

            var scene = document.GenerateScene(state) as Node3D;
            if (scene == null) GD.PrintErr($"[Characters] {path} produced no Node3D");
            else GD.Print($"[Characters] Loaded {path.GetFile()}");

            return scene;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Characters] {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Copy clips from a separate animation .glb onto a loaded rig.</summary>
    private static void MergeAnimations(Node3D target, string animationPath)
    {
        var source = LoadGltf(animationPath);
        if (source == null) return;

        var sourcePlayer = FindAnimationPlayer(source);
        if (sourcePlayer == null)
        {
            source.QueueFree();
            return;
        }

        var targetPlayer = FindAnimationPlayer(target);
        if (targetPlayer == null)
        {
            targetPlayer = new AnimationPlayer { Name = "AnimationPlayer" };
            target.AddChild(targetPlayer);
            targetPlayer.Owner = target;
        }

        foreach (var libraryName in sourcePlayer.GetAnimationLibraryList())
        {
            var library = sourcePlayer.GetAnimationLibrary(libraryName);
            if (library == null) continue;

            if (!targetPlayer.HasAnimationLibrary(libraryName))
                targetPlayer.AddAnimationLibrary(libraryName, library);
        }

        source.QueueFree();
    }

    // ── Animation lookup ───────────────────────────────────────────────

    /// <summary>The AnimationPlayer inside an instantiated character, if any.</summary>
    public static AnimationPlayer FindAnimationPlayer(Node root)
    {
        if (root is AnimationPlayer player) return player;

        foreach (var child in root.GetChildren())
        {
            var found = FindAnimationPlayer(child);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    /// Resolve a logical animation to a clip this rig actually has, matching
    /// by substring because Meshy's clip names are not consistent between
    /// exports. Returns null when nothing plausible exists.
    /// </summary>
    public static string ResolveAnimation(AnimationPlayer player, CharacterAnimation wanted)
    {
        if (player == null) return null;

        var available = player.GetAnimationList();
        if (available.Length == 0) return null;

        if (AnimationHints.TryGetValue(wanted, out var hints))
        {
            foreach (var hint in hints)
            {
                foreach (var name in available)
                    if (name.Contains(hint, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }

        // Deliberately null rather than "play whatever exists". The current
        // Meshy exports ship only Running and Walking, so a blind fallback
        // made every idle staff member sprint on the spot. Callers handle
        // null by freezing a locomotion clip into a standing pose instead.
        return null;
    }

    /// <summary>
    /// A clip to freeze into a static standing pose when the rig has no idle
    /// animation. Prefers a walk over a run, since a paused walk cycle reads
    /// closer to standing.
    /// </summary>
    public static string ResolveStandInPose(AnimationPlayer player)
    {
        if (player == null) return null;

        var available = player.GetAnimationList();
        if (available.Length == 0) return null;

        foreach (var name in available)
            if (name.Contains("walk", StringComparison.OrdinalIgnoreCase)) return name;

        return available[0];
    }
}

/// <summary>Logical animation states the game asks for.</summary>
public enum CharacterAnimation
{
    Idle,
    Walk,
    Talk,
    Sit,
    Dance
}
