using Godot;
using System.Collections.Generic;
using System.Linq;

public partial class _VenueProbe : Node
{
    public override void _Ready()
    {
        var venue = new VenueBuilding { Name = "VenueBuilding" };
        AddChild(venue);

        GD.Print("### floors: " + string.Join(",", venue.Floors));

        // Ground-floor lounge, street-facing.
        var lounge = VenueBuilding.CreateRoom(RoomType.Lounge, new Vector3I(0, 0, 0), "Front Lounge");
        GD.Print("### place lounge: " + venue.PlaceRoom(lounge));

        // Suite on floor 1 directly above the lounge -> should catch nothing
        // (noise falls), but should catch scrutiny (it rises).
        var upstairs = VenueBuilding.CreateRoom(RoomType.PrivateSuite, new Vector3I(0, 0, 1), "Upstairs Suite");
        GD.Print("### place upstairs: " + venue.PlaceRoom(upstairs));

        // Basement suite - should never be bookable.
        var cellar = VenueBuilding.CreateRoom(RoomType.PrivateSuite, new Vector3I(0, 0, -1), "Cellar Suite");
        GD.Print("### place cellar: " + venue.PlaceRoom(cellar));

        // Overlap rejection.
        var overlap = VenueBuilding.CreateRoom(RoomType.PrivateSuite, new Vector3I(1, 0, 0), "Overlap");
        GD.Print("### overlap rejected (expect False): " + venue.PlaceRoom(overlap));

        // Out-of-range floor.
        GD.Print("### floor 5 rejected (expect False): " + venue.AddFloor(5));
        GD.Print("### floor 2 accepted (expect True): " + venue.AddFloor(2));

        // ── Furnishing: cheap-matching vs expensive-mismatched ──────────
        var cheapMatching = new List<FurnitureItem>
        {
            FurnitureItem.Create("Iron Bed", FurnitureCategory.Bed, FurnitureStyle.ArtDeco, 1, new Vector2I(2,1)),
            FurnitureItem.Create("Sconce", FurnitureCategory.Lighting, FurnitureStyle.ArtDeco, 1),
            FurnitureItem.Create("Mirror", FurnitureCategory.Vanity, FurnitureStyle.ArtDeco, 1)
        };

        var pricyMismatch = new List<FurnitureItem>
        {
            FurnitureItem.Create("Gilt Bed", FurnitureCategory.Bed, FurnitureStyle.Baroque, 5, new Vector2I(2,1)),
            FurnitureItem.Create("Neon", FurnitureCategory.Lighting, FurnitureStyle.Modern, 5),
            FurnitureItem.Create("Lacquer Vanity", FurnitureCategory.Vanity, FurnitureStyle.Spartan, 5)
        };

        var bedless = new List<FurnitureItem>
        {
            FurnitureItem.Create("Rug A", FurnitureCategory.Rug, FurnitureStyle.ArtDeco, 5),
            FurnitureItem.Create("Rug B", FurnitureCategory.Rug, FurnitureStyle.ArtDeco, 5),
            FurnitureItem.Create("Lamp", FurnitureCategory.Lighting, FurnitureStyle.ArtDeco, 5),
            FurnitureItem.Create("Vanity", FurnitureCategory.Vanity, FurnitureStyle.ArtDeco, 5),
            FurnitureItem.Create("Decor", FurnitureCategory.Decor, FurnitureStyle.ArtDeco, 5),
            FurnitureItem.Create("Screen", FurnitureCategory.Screen, FurnitureStyle.ArtDeco, 5)
        };

        double cheapCost = cheapMatching.Sum(f => f.PurchasePrice);
        double pricyCost = pricyMismatch.Sum(f => f.PurchasePrice);

        GD.Print($"### cheap-matching  appt={RoomAppointmentCalculator.Calculate(RoomType.PrivateSuite, cheapMatching):F1} cost=${cheapCost:F0}");
        GD.Print($"### pricy-mismatch  appt={RoomAppointmentCalculator.Calculate(RoomType.PrivateSuite, pricyMismatch):F1} cost=${pricyCost:F0}");
        GD.Print($"### bedless-crammed appt={RoomAppointmentCalculator.Calculate(RoomType.PrivateSuite, bedless):F1}");
        GD.Print($"### empty           appt={RoomAppointmentCalculator.Calculate(RoomType.PrivateSuite, new List<FurnitureItem>()):F1}");

        foreach (var item in cheapMatching)
            GD.Print("### add furniture: " + venue.AddFurniture(new Vector3I(0, 0, 1), item));

        GD.Print("### upstairs breakdown:\n" + venue.GetAppointmentBreakdown(new Vector3I(0, 0, 1)));

        // ── Vertical mechanics ─────────────────────────────────────────
        venue.RecalculateSynergies();
        foreach (var s in venue.ActiveSynergies)
            GD.Print("### synergy: " + s);

        GD.Print($"### upstairs scrutiny={upstairs.ScrutinyExposure:F1} noise={upstairs.NoiseExposure:F1} " +
                 $"revMod={upstairs.RevenueModifier:F2} effLux={venue.GetEffectiveLuxury(new Vector3I(0,0,1)):F1} " +
                 $"effDisc={venue.GetEffectiveDiscretion(new Vector3I(0,0,1)):F1}");
        GD.Print($"### cellar revMod={cellar.RevenueModifier:F2} bookable={venue.IsRevenueGenerating(cellar)}");
        GD.Print($"### basement exposure={venue.GetBasementExposure():F1} screening={venue.GetScreeningStrength():F1} upsell={venue.GetUpsellMultiplier():F2}");
        GD.Print($"### bookable rooms={venue.GetBookableRoomCount()} upkeep=${venue.GetNightlyUpkeep():F2}/night");
        GD.Print($"### suggested price on base $200 = ${venue.GetSuggestedPrice(upstairs, 200.0):F2}");

        // Noise: put a bar on floor 2 directly over the suite.
        var bar = VenueBuilding.CreateRoom(RoomType.Bar, new Vector3I(0, 0, 2), "Roof Bar");
        venue.PlaceRoom(bar);
        GD.Print($"### after roof bar: suite noise={upstairs.NoiseExposure:F1} satMod={upstairs.SatisfactionModifier:F3}");

        var proof = VenueBuilding.CreateRoom(RoomType.Soundproofing, new Vector3I(2, 0, 1), "Proofing");
        venue.PlaceRoom(proof);
        GD.Print($"### with proofing cell: bonus={upstairs.SoundproofingBonus:F0} " +
                 $"effSound={upstairs.EffectiveSoundproofing:F0} noise={upstairs.NoiseExposure:F1}");

        // ── Save / load round trip ─────────────────────────────────────
        var state = venue.CaptureState();
        var reloaded = new VenueBuilding { Name = "Reloaded" };
        AddChild(reloaded);
        reloaded.RestoreState(state);
        GD.Print($"### restored rooms={reloaded.Rooms.Count} floors={string.Join(",", reloaded.Floors)} " +
                 $"upkeep=${reloaded.GetNightlyUpkeep():F2}");
        GD.Print($"### restored upstairs appt={reloaded.GetRoomAppointment(new Vector3I(0,0,1)):F1}");

        // Missing-key tolerance.
        var partial = new System.Text.Json.Nodes.JsonObject();
        var tolerant = new VenueBuilding { Name = "Tolerant" };
        AddChild(tolerant);
        tolerant.RestoreState(partial);
        GD.Print($"### tolerant restore floors={string.Join(",", tolerant.Floors)} rooms={tolerant.Rooms.Count}");

        GD.Print("### building:\n" + venue.GetBuildingDebugString());
        GD.Print("### PROBE DONE");
        GetTree().Quit();
    }
}
