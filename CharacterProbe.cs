using Godot;
using System.Linq;

/// <summary>
/// Diagnostic: loads every registered character model and reports what came
/// back — mesh count, skeleton, and the animation clips available.
///
/// Worth keeping as a scene of its own because these .glb files are large,
/// externally produced, and inconsistently named. When a new character does
/// not appear in game, running this says whether the problem is the file or
/// the game.
///
///   Godot_v4.7.1-stable_win64_console.exe --headless --path . character_probe.tscn
/// </summary>
public partial class CharacterProbe : Node
{
    public override void _Ready()
    {
        var library = new CharacterLibrary { Name = "CharacterLibrary" };
        AddChild(library);

        GD.Print("\n══════════ CHARACTER PROBE ══════════");

        var failures = 0;

        foreach (var model in CharacterLibrary.Models.Concat(CharacterLibrary.ClientModels))
        {
            GD.Print($"\n── {model.DisplayName} ({model.Id})");
            GD.Print($"   {model.ScenePath}");

            if (!Godot.FileAccess.FileExists(model.ScenePath))
            {
                GD.PrintErr("   FILE MISSING");
                failures++;
                continue;
            }

            var instance = library.Instantiate(model.Id);
            if (instance == null)
            {
                GD.PrintErr("   FAILED TO LOAD");
                failures++;
                continue;
            }

            AddChild(instance);

            var meshes = CountNodes<MeshInstance3D>(instance);
            var skeletons = CountNodes<Skeleton3D>(instance);
            var player = CharacterLibrary.FindAnimationPlayer(instance);

            GD.Print($"   meshes: {meshes}, skeletons: {skeletons}");

            var bounds = GetVisualHeight(instance);
            GD.Print($"   approx height: {bounds:F2} m  " +
                     $"(a 2 m tile wants roughly 1.6–1.9)");

            if (player == null)
            {
                GD.PrintErr("   NO ANIMATION PLAYER — the rig will stand in a T-pose");
                failures++;
            }
            else
            {
                var clips = player.GetAnimationList();
                GD.Print($"   animations ({clips.Length}): {string.Join(", ", clips.Take(12))}" +
                         (clips.Length > 12 ? ", …" : ""));

                foreach (CharacterAnimation wanted in System.Enum.GetValues<CharacterAnimation>())
                {
                    var resolved = CharacterLibrary.ResolveAnimation(player, wanted);
                    GD.Print($"     {wanted,-6} → {resolved ?? "(none)"}");
                }
            }

            instance.QueueFree();
        }

        GD.Print(failures == 0
            ? "\nPROBE PASSED — every model loaded with animations."
            : $"\nPROBE FAILED — {failures} problem(s).");

        GetTree().Quit(failures);
    }

    private static int CountNodes<T>(Node root) where T : Node
    {
        var count = root is T ? 1 : 0;
        foreach (var child in root.GetChildren()) count += CountNodes<T>(child);
        return count;
    }

    /// <summary>Rough model height from its mesh AABBs, to catch unit-scale mismatches.</summary>
    private static float GetVisualHeight(Node root)
    {
        var height = 0f;

        if (root is MeshInstance3D mesh && mesh.Mesh != null)
            height = mesh.Mesh.GetAabb().Size.Y * mesh.Scale.Y;

        foreach (var child in root.GetChildren())
            height = Mathf.Max(height, GetVisualHeight(child));

        return height;
    }
}
