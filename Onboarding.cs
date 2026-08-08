using Godot;
using System.Linq;

/// <summary>Where the player is in their first few minutes.</summary>
public enum FirstStep
{
    BuildASuite,
    FurnishIt,
    HireOrPost,
    OpenTheDoors,
    ReadTheLedger,
    Done
}

/// <summary>
/// What the player should do next, derived from the world rather than from a
/// script they are stepping through.
///
/// A scripted tutorial has to be obeyed in order and breaks the moment the
/// player does something sensible that it did not anticipate. This reads the
/// actual state every time it is asked, so a player who furnishes before
/// posting staff, or builds two suites before opening, is never told to undo
/// anything — the prompt simply moves to whatever is genuinely still missing.
///
/// It also means there is nothing to reset, nothing to persist, and no way
/// for the guidance to disagree with the game.
/// </summary>
public static class Onboarding
{
    /// <summary>The first thing still undone, or <see cref="FirstStep.Done"/>.</summary>
    public static FirstStep Next(VenueBuilding venue, NightDirector night)
    {
        if (venue == null || night == null) return FirstStep.Done;

        // The Ledger has been read at least once — the loop is complete and
        // the player has seen every beat of it.
        if ((night.CurrentReport?.Night ?? 1) > 1) return FirstStep.Done;

        var earners = venue.Rooms.Values.Where(venue.IsRevenueGenerating).ToList();
        if (earners.Count == 0) return FirstStep.BuildASuite;

        // Bookable means it has what the room type requires, which is the
        // real bar — a suite with a bed and no light cannot take a client.
        if (venue.GetBookableRoomCount() == 0) return FirstStep.FurnishIt;

        if (night.Assignments.Count == 0) return FirstStep.HireOrPost;

        if (night.Phase is NightPhase.Idle or NightPhase.Preparation)
            return FirstStep.OpenTheDoors;

        return FirstStep.ReadTheLedger;
    }

    /// <summary>A short instruction, for the top bar.</summary>
    public static string Caption(FirstStep step) => step switch
    {
        FirstStep.BuildASuite => "Next: build a Private Suite",
        FirstStep.FurnishIt => "Next: furnish it — click the room",
        FirstStep.HireOrPost => "Next: post someone to the suite",
        FirstStep.OpenTheDoors => "Next: open the doors",
        FirstStep.ReadTheLedger => "Serving — the books come at dawn",
        _ => ""
    };

    /// <summary>The reason, for the tooltip. Why, not just what.</summary>
    public static string Detail(FirstStep step) => step switch
    {
        FirstStep.BuildASuite =>
            "The reception and the bar bring people in and set what they will " +
            "spend, but neither earns anything on its own. A Private Suite is " +
            "the first room that makes money. Pick it from Build / Decorate on " +
            "the right; it goes on the floor you are looking at.",

        FirstStep.FurnishIt =>
            "Click the room to open the furniture shop. A suite needs a bed, a " +
            "light and a table before anyone will book it. Pieces that share a " +
            "style are worth more together than expensive pieces that clash — " +
            "that is the whole economy in one sentence.",

        FirstStep.HireOrPost =>
            "Press S for the roster. Somebody has to be working the room, or " +
            "clients arrive to find nobody to see.",

        FirstStep.OpenTheDoors =>
            "Preparation is paused time — decide everything now, because once " +
            "the doors open the night runs on its own. Clients arrive, get " +
            "matched to a room, and the outcome is settled on what you chose " +
            "before they walked in.",

        FirstStep.ReadTheLedger =>
            "Watch. When the last client leaves you get the books, and the " +
            "night's decisions turn into a number.",

        _ => ""
    };
}
