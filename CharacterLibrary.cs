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
        },

        // Clients. Kept in a separate list below so staff are never dressed
        // as patrons and vice versa.
    };

    /// <summary>
    /// Models used for visiting clients. Separate from <see cref="Models"/>
    /// so the house's staff and its patrons never share a face.
    /// </summary>
    public static readonly List<CharacterModel> ClientModels = new()
    {
        new CharacterModel
        {
            Id = "gentleman",
            DisplayName = "Gentleman",
            ScenePath = "res://Assets/Characters/wealthy_gentleman.glb",
            AnimationPath = "res://Assets/Characters/wealthy_gentleman_anims.glb",
            Scale = 1f
        }
    };

    /// <summary>Every model, staff and client alike.</summary>
    private static IEnumerable<CharacterModel> AllModels => Models.Concat(ClientModels);

    // ── Animation name matching ────────────────────────────────────────

    /// <summary>
    /// Meshy names its clips inconsistently, so animations are matched by
    /// substring against these preference lists rather than by exact name.
    /// First match wins.
    /// </summary>
    private static readonly Dictionary<CharacterAnimation, string[]> AnimationHints = new()
    {
        [CharacterAnimation.Idle] = new[]
            { "idle", "stand", "breath", "rest", "relax", "neutral" },

        [CharacterAnimation.Walk] = new[] { "walk", "stroll", "strut" },

        [CharacterAnimation.Talk] = new[]
            { "talk", "speak", "gesture", "agree", "wave", "greet", "converse" },

        [CharacterAnimation.Sit] = new[] { "sit", "chair", "seated" },

        [CharacterAnimation.Dance] = new[] { "dance", "groove", "hop", "sway" }
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
        var model = AllModels.FirstOrDefault(m => m.Id == modelId) ?? Models.FirstOrDefault();
        if (model == null) return null;

        if (!_cache.TryGetValue(model.Id, out var prototype))
        {
            prototype = LoadGltf(model.ScenePath);
            if (prototype == null) return null;

            if (!string.IsNullOrEmpty(model.AnimationPath))
                MergeAnimations(prototype, model.AnimationPath);

            // Fold in every other .glb sitting beside the model. Meshy exports
            // one clip per file, so an idle or a gesture arrives as its own
            // download — dropping it in the character's folder should be the
            // whole integration step, exactly as it is for furniture.
            MergeSiblingAnimations(prototype, model.ScenePath, model.AnimationPath);

            var player = FindAnimationPlayer(prototype);
            GD.Print($"[Characters] {model.DisplayName}: " +
                     $"{player?.GetAnimationList().Length ?? 0} clips " +
                     $"({string.Join(", ", player?.GetAnimationList() ?? Array.Empty<string>())})");

            _cache[model.Id] = prototype;
        }

        var instance = prototype.Duplicate() as Node3D;
        if (instance == null) return null;

        instance.Scale = Vector3.One * model.Scale;
        return instance;
    }

    /// <summary>
    /// Pick a model deterministically from a stable id, so a given person
    /// always looks the same across sessions and rebuilds.
    /// </summary>
    /// <param name="forClient">Draw from the client pool rather than staff.</param>
    public string PickModelFor(string stableId, bool forClient = false)
    {
        var pool = forClient ? ClientModels : Models;
        if (pool.Count == 0) return null;
        if (string.IsNullOrEmpty(stableId)) return pool[0].Id;

        var hash = 0;
        foreach (var c in stableId) hash = (hash * 31 + c) & 0x7FFFFFFF;

        return pool[hash % pool.Count].Id;
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

    /// <summary>
    /// Merge every other .glb in the model's own folder into it, treating
    /// each as a bundle of animation clips.
    ///
    /// Meshy exports one animation per file, so an idle or a talk gesture
    /// arrives as a separate download rather than being folded into the rig.
    /// Scanning the folder means adding one is a file copy, with no code and
    /// no registry entry.
    /// </summary>
    private static void MergeSiblingAnimations(
        Node3D target, string modelPath, string alreadyMergedPath)
    {
        var folder = modelPath.GetBaseDir();

        // Only scan a character's own subfolder. A model sitting directly in
        // Assets/Characters/ shares that folder with every other character,
        // and merging there pulled unrelated rigs' clips onto it.
        if (folder.TrimSuffix("/").GetFile()
            .Equals("Characters", StringComparison.OrdinalIgnoreCase))
            return;

        using var dir = DirAccess.Open(folder);
        if (dir == null) return;

        dir.ListDirBegin();

        string name;
        while ((name = dir.GetNext()) != "")
        {
            if (dir.CurrentIsDir() || name.StartsWith(".")) continue;
            if (!name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) continue;

            var full = folder.TrimSuffix("/") + "/" + name;

            // Skip the mesh itself and any file already merged explicitly,
            // or every clip arrives twice with a _2 suffix.
            if (full == modelPath || full == alreadyMergedPath) continue;

            MergeAnimations(target, full);
        }

        dir.ListDirEnd();
    }

    /// <summary>
    /// Copy clips from an animation .glb onto a loaded rig.
    ///
    /// Clips are merged individually rather than by whole library. Every
    /// Meshy export names its library "" (the default), so adding libraries
    /// wholesale silently dropped the second and every later file — the name
    /// was already taken. Names that collide are suffixed rather than
    /// overwriting, since two files may both call their clip "Armature|mixamo".
    /// </summary>
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

        var destination = GetOrCreateDefaultLibrary(targetPlayer);
        if (destination == null)
        {
            source.QueueFree();
            return;
        }

        // A clip whose own name is uninformative — Meshy often emits
        // "Armature|Take 001" — is renamed after its file, so the substring
        // matcher in ResolveAnimation has something to work with.
        var fileHint = animationPath.GetFile().GetBaseName();

        foreach (var clipName in sourcePlayer.GetAnimationList())
        {
            var clip = sourcePlayer.GetAnimation(clipName);
            if (clip == null) continue;

            var targetName = ChooseClipName(clipName, fileHint);
            var unique = targetName;
            var suffix = 2;

            while (destination.HasAnimation(unique))
                unique = $"{targetName}_{suffix++}";

            destination.AddAnimation(unique, (Animation)clip.Duplicate());
        }

        source.QueueFree();
    }

    /// <summary>
    /// The library new clips are added to, creating the default one if the
    /// rig arrived without any.
    /// </summary>
    private static AnimationLibrary GetOrCreateDefaultLibrary(AnimationPlayer player)
    {
        if (player.HasAnimationLibrary(""))
            return player.GetAnimationLibrary("");

        var library = new AnimationLibrary();
        return player.AddAnimationLibrary("", library) == Error.Ok ? library : null;
    }

    /// <summary>
    /// Prefer the clip's own name, unless it is a generic exporter artefact,
    /// in which case fall back to the filename — which is where the useful
    /// word ("idle", "talk") actually lives.
    /// </summary>
    private static string ChooseClipName(string clipName, string fileHint)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return fileHint;

        var lower = clipName.ToLowerInvariant();

        var generic = lower.Contains("take 001") ||
                      lower.Contains("mixamo.com") ||
                      lower == "animation" ||
                      lower == "armature";

        return generic ? fileHint : clipName;
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
            string best = null;
            var bestScore = 0;

            foreach (var name in available)
            {
                var score = ScoreMatch(name, hints, wanted);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = name;
                }
            }

            if (best != null) return best;
        }

        // Deliberately null rather than "play whatever exists". The current
        // Meshy exports ship only Running and Walking, so a blind fallback
        // made every idle staff member sprint on the spot. Callers handle
        // null by freezing a locomotion clip into a standing pose instead.
        return null;
    }

    /// <summary>
    /// How well a clip name matches a wanted state. Higher wins.
    ///
    /// A plain substring test is not enough: a rig carrying both "Idle_7" and
    /// "Chair_Sit_Idle_M" would answer Idle with the seated one purely on
    /// list order, and staff would sit down in mid-air. Names that *start*
    /// with the hint beat names that merely contain it, and a clip carrying
    /// another state's keyword is penalised so it is only used as a last resort.
    /// </summary>
    private static int ScoreMatch(string clipName, string[] hints, CharacterAnimation wanted)
    {
        var lower = clipName.ToLowerInvariant();
        var score = 0;

        for (var i = 0; i < hints.Length; i++)
        {
            var hint = hints[i];
            if (!lower.Contains(hint)) continue;

            // Earlier hints are stronger; a prefix match is stronger still.
            var weight = (hints.Length - i) * 10;
            score = Mathf.Max(score, lower.StartsWith(hint) ? weight + 25 : weight);
        }

        if (score == 0) return 0;

        // Penalise clips that also advertise a different state.
        foreach (var (state, otherHints) in AnimationHints)
        {
            if (state == wanted) continue;

            foreach (var other in otherHints)
                if (lower.Contains(other)) score -= 12;
        }

        return Mathf.Max(1, score);
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
