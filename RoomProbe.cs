using Godot;
using System.Linq;

/// <summary>
/// Diagnostic: builds one furnished room and reports the node tree it
/// produced, with each mesh's world position and size.
///
/// Exists because "I cannot see the bed" has several possible causes —
/// the piece was never built, it was built at the wrong position, it was
/// built inside the floor slab, or it is simply behind the camera — and a
/// screenshot cannot distinguish them.
///
///   Godot_v4.7.1-stable_win64_console.exe --headless --path . room_probe.tscn
/// </summary>
public partial class RoomProbe : Node
{
    public override void _Ready()
    {
        GD.Print("\n══════════ ROOM PROBE ══════════");

        var venue = new VenueBuilding { Name = "VenueBuilding" };
        AddChild(venue);

        var room = VenueBuilding.CreateRoom(RoomType.PrivateSuite, new Vector3I(0, 0, 1), "Probe Suite");
        venue.PlaceRoom(room);

        // Same set the bootstrap seeds a suite with.
        var pieces = new (string Name, FurnitureCategory Category)[]
        {
            ("Baroque Bed", FurnitureCategory.Bed),
            ("Baroque Armchair", FurnitureCategory.Seating),
            ("Baroque Lamp", FurnitureCategory.Lighting),
            ("Baroque Rug", FurnitureCategory.Rug),
            ("Baroque Mirror", FurnitureCategory.Vanity)
        };

        foreach (var (name, category) in pieces)
            venue.AddFurniture(room.GridPosition, FurnitureItem.Create(name, category, FurnitureStyle.Baroque, 2));

        GD.Print($"room origin {room.GridPosition}, size {room.Size}, " +
                 $"{room.Furniture.Count} pieces");
        GD.Print($"expected floor Y = {VenueSpace.FloorY(room.Floor):F2}, " +
                 $"room centre = {VenueSpace.RoomCenter(room.GridPosition, room.Size)}");

        var built = VenueRoomBuilder.BuildRoom(room);
        if (built == null)
        {
            GD.PrintErr("BuildRoom returned null");
            GetTree().Quit(1);
            return;
        }

        AddChild(built);

        GD.Print("\n── meshes produced ──");
        var count = Report(built, 0);

        GD.Print($"\n{count} mesh instances total.");

        var lights = CountLights(built);
        GD.Print($"{lights} lights.");

        GetTree().Quit(count > 0 ? 0 : 1);
    }

    private static int Report(Node node, int depth)
    {
        var count = 0;

        if (node is MeshInstance3D mesh)
        {
            count++;

            var aabb = mesh.Mesh?.GetAabb() ?? new Aabb();
            var size = aabb.Size * mesh.Scale;

            GD.Print($"{new string(' ', depth * 2)}{mesh.Name,-26} " +
                     $"pos {Fmt(mesh.GlobalPosition)}  size {Fmt(size)}");
        }

        foreach (var child in node.GetChildren())
            count += Report(child, depth + (node is MeshInstance3D ? 1 : 0));

        return count;
    }

    private static int CountLights(Node node)
    {
        var count = node is Light3D ? 1 : 0;
        foreach (var child in node.GetChildren()) count += CountLights(child);
        return count;
    }

    private static string Fmt(Vector3 v) => $"({v.X,6:F2},{v.Y,6:F2},{v.Z,6:F2})";
}
