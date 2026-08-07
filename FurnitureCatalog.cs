using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A purchasable furnishing: the item itself plus what it costs and what it
/// would do to the room it lands in.
/// </summary>
public class CatalogOffer
{
    /// <summary>A fresh, unplaced instance. Buying hands this to the room.</summary>
    public FurnitureItem Item { get; set; }

    public double Price => Item?.PurchasePrice ?? 0.0;

    /// <summary>
    /// Appointment the target room would score if this piece were added.
    /// Computed against a specific room, so it is only meaningful alongside
    /// the room it was quoted for.
    /// </summary>
    public float ProjectedAppointment { get; set; }

    /// <summary>Change in Appointment this purchase would produce.</summary>
    public float AppointmentDelta { get; set; }

    /// <summary>Whether this fills a category the room is required to have.</summary>
    public bool FillsRequiredGap { get; set; }

    /// <summary>Whether this matches the room's existing dominant style.</summary>
    public bool MatchesRoomStyle { get; set; }

    /// <summary>Appointment gained per dollar. The honest value metric.</summary>
    public float ValuePerCost => Price <= 0 ? 0f : AppointmentDelta / (float)Price * 100f;

    public override string ToString() =>
        $"{Item?.ItemName} — ${Price:F0}, {AppointmentDelta:+0.0;-0.0;0} appt";
}

/// <summary>
/// The furniture shop.
///
/// Generates purchasable offers and executes purchases. It also quotes what
/// each piece would do to a specific room before you buy it, which is the
/// point: style coherence is 20% of a room's Appointment score and the single
/// biggest anti-optimization pressure in the economy, but it is completely
/// invisible unless the shop explains it. A player who cannot see that five
/// cheap matching pieces beat five expensive clashing ones will never
/// discover the mechanic.
///
/// Registered in the scene tree as "FurnitureCatalog".
/// </summary>
public partial class FurnitureCatalog : Node
{
    // ── Signals ────────────────────────────────────────────────────────

    [Signal]
    public delegate void OnFurniturePurchasedEventHandler(
        string itemName, int x, int y, int floor, double price);

    [Signal]
    public delegate void OnPurchaseRejectedEventHandler(string reason);

    [Signal]
    public delegate void OnFurnitureSoldEventHandler(string itemName, double refund);

    // ── Configuration ──────────────────────────────────────────────────

    /// <summary>Highest tier the shop will offer. Raised by research later.</summary>
    [Export] public int MaxAvailableTier { get; set; } = 3;

    /// <summary>Share of purchase price returned when selling a piece back.</summary>
    [Export] public double ResaleRate { get; set; } = 0.45;

    // ── Naming ─────────────────────────────────────────────────────────

    /// <summary>
    /// Evocative nouns per category and style, so the catalogue reads like a
    /// dealer's list rather than a generated matrix.
    /// </summary>
    private static readonly Dictionary<FurnitureCategory, string[]> CategoryNouns = new()
    {
        [FurnitureCategory.Bed] = new[] { "Bed", "Four-Poster", "Divan", "Chaise Bed" },
        [FurnitureCategory.Seating] = new[] { "Armchair", "Settee", "Chaise Longue", "Ottoman" },
        [FurnitureCategory.Lighting] = new[] { "Lamp", "Chandelier", "Sconce", "Candelabra" },
        [FurnitureCategory.Rug] = new[] { "Rug", "Carpet", "Runner", "Hide" },
        [FurnitureCategory.Decor] = new[] { "Portrait", "Mirror", "Tapestry", "Sculpture" },
        [FurnitureCategory.Vanity] = new[] { "Vanity", "Dressing Table", "Console", "Bureau" },
        [FurnitureCategory.Screen] = new[] { "Folding Screen", "Room Divider", "Privacy Panel", "Shoji" },
        [FurnitureCategory.Bath] = new[] { "Slipper Bath", "Basin", "Copper Tub", "Wash Stand" },
        [FurnitureCategory.Bar] = new[] { "Drinks Cabinet", "Counter", "Sideboard", "Bar Cart" }
    };

    private static readonly Dictionary<FurnitureStyle, string> StyleAdjectives = new()
    {
        [FurnitureStyle.Baroque] = "Gilded",
        [FurnitureStyle.ArtDeco] = "Deco",
        [FurnitureStyle.Oriental] = "Lacquered",
        [FurnitureStyle.Bohemian] = "Bohemian",
        [FurnitureStyle.Modern] = "Modern",
        [FurnitureStyle.Spartan] = "Plain"
    };

    private static readonly string[] TierPrefixes =
        { "Worn", "Plain", "Fine", "Exquisite", "Masterwork" };

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready() =>
        GD.Print($"[Catalog] Ready. Offering up to tier {MaxAvailableTier}.");

    // ── Offers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every piece the shop will sell for a room, each quoted with what it
    /// would do to that room's Appointment.
    /// </summary>
    /// <param name="room">Room being furnished. Null returns unquoted offers.</param>
    /// <param name="styleFilter">Restrict to one style, or null for all.</param>
    /// <param name="categoryFilter">Restrict to one category, or null for all.</param>
    public List<CatalogOffer> GetOffers(
        RoomModule room,
        FurnitureStyle? styleFilter = null,
        FurnitureCategory? categoryFilter = null)
    {
        var offers = new List<CatalogOffer>();

        var styles = styleFilter.HasValue
            ? new[] { styleFilter.Value }
            : Enum.GetValues<FurnitureStyle>();

        var categories = categoryFilter.HasValue
            ? new[] { categoryFilter.Value }
            : Enum.GetValues<FurnitureCategory>();

        foreach (var style in styles)
        foreach (var category in categories)
        for (var tier = 1; tier <= Mathf.Clamp(MaxAvailableTier, 1, 5); tier++)
        {
            var item = FurnitureItem.Create(BuildName(category, style, tier), category, style, tier);
            offers.Add(Quote(room, item));
        }

        return offers;
    }

    /// <summary>
    /// The offers that would most improve a room, best value first.
    ///
    /// This is the teaching surface: because the ranking is by Appointment
    /// gained per dollar, a matching cheap piece naturally outranks a clashing
    /// expensive one, and the player learns coherence by reading the list.
    /// </summary>
    public List<CatalogOffer> GetRecommendations(RoomModule room, int count = 6)
    {
        if (room == null) return new List<CatalogOffer>();

        return GetOffers(room)
            .Where(o => o.AppointmentDelta > 0f)
            .OrderByDescending(o => o.FillsRequiredGap)
            .ThenByDescending(o => o.ValuePerCost)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Price an item against a room by actually scoring the room with it
    /// added, then removing it again. Simulating rather than approximating
    /// keeps the quote honest even as the Appointment formula changes.
    /// </summary>
    public CatalogOffer Quote(RoomModule room, FurnitureItem item)
    {
        var offer = new CatalogOffer { Item = item };
        if (room == null || item == null) return offer;

        var before = RoomAppointmentCalculator.GetBreakdown(room);

        room.Furniture.Add(item);
        var after = RoomAppointmentCalculator.GetBreakdown(room);
        room.Furniture.Remove(item);

        offer.ProjectedAppointment = after.Appointment;
        offer.AppointmentDelta = after.Appointment - before.Appointment;
        offer.FillsRequiredGap =
            before.MissingCategories != null && before.MissingCategories.Contains(item.Category);
        offer.MatchesRoomStyle = before.ItemCount > 0 && before.DominantStyle == item.StyleTag;

        return offer;
    }

    // ── Transactions ───────────────────────────────────────────────────

    /// <summary>
    /// Buy a piece and place it in a room.
    /// </summary>
    /// <returns>True if the purchase completed.</returns>
    public bool Purchase(VenueBuilding venue, Vector3I tile, CatalogOffer offer)
    {
        if (venue == null || offer?.Item == null)
        {
            Reject("Nothing selected.");
            return false;
        }

        var room = venue.GetRoom(tile);
        if (room == null)
        {
            Reject("No room there.");
            return false;
        }

        if (room.FreeFurnitureArea < offer.Item.Area)
        {
            Reject($"{room.RoomName} has no space left.");
            return false;
        }

        var gsm = GameStateManager.Instance;
        if (gsm == null || gsm.Cash < offer.Price)
        {
            Reject($"Cannot afford ${offer.Price:F0}.");
            return false;
        }

        // Place before charging, so a refusal inside AddFurniture cannot
        // leave the player paying for a piece that never arrived.
        if (!venue.AddFurniture(tile, offer.Item))
        {
            Reject("The room refused the piece.");
            return false;
        }

        ChargeExpense(offer.Price, $"{offer.Item.ItemName} for {room.RoomName}");

        EmitSignal(SignalName.OnFurniturePurchased,
            offer.Item.ItemName, tile.X, tile.Y, tile.Z, offer.Price);

        GD.Print($"[Catalog] Bought {offer.Item.ItemName} for {room.RoomName} " +
                 $"(${offer.Price:F0}) — appointment now {venue.GetRoomAppointment(tile):F0}.");
        return true;
    }

    /// <summary>
    /// Sell a placed piece back, refunding a share of its price scaled by
    /// remaining condition. Worn furniture is worth little, which is what
    /// makes replacement a real decision rather than a free swap.
    /// </summary>
    public bool Sell(VenueBuilding venue, Vector3I tile, int index)
    {
        var room = venue?.GetRoom(tile);
        if (room == null || index < 0 || index >= room.Furniture.Count)
        {
            Reject("Nothing to sell.");
            return false;
        }

        var item = room.Furniture[index];
        if (item == null) return false;

        var refund = Math.Round(
            item.PurchasePrice * ResaleRate * (item.Condition / 100.0), 2);

        if (!venue.RemoveFurniture(tile, index))
        {
            Reject("Could not remove the piece.");
            return false;
        }

        if (refund > 0) RecordRefund(refund, $"Sold {item.ItemName}");

        EmitSignal(SignalName.OnFurnitureSold, item.ItemName, refund);
        GD.Print($"[Catalog] Sold {item.ItemName} for ${refund:F0}.");
        return true;
    }

    // ── Naming ─────────────────────────────────────────────────────────

    private static string BuildName(FurnitureCategory category, FurnitureStyle style, int tier)
    {
        var nouns = CategoryNouns.TryGetValue(category, out var list)
            ? list
            : new[] { category.ToString() };

        // Deterministic pick, so the same piece always has the same name.
        var noun = nouns[(tier + (int)style) % nouns.Length];
        var adjective = StyleAdjectives.TryGetValue(style, out var adj) ? adj : style.ToString();
        var prefix = TierPrefixes[Mathf.Clamp(tier - 1, 0, TierPrefixes.Length - 1)];

        return $"{prefix} {adjective} {noun}";
    }

    // ── Money ──────────────────────────────────────────────────────────

    private void ChargeExpense(double amount, string description)
    {
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;

        if (ledger != null)
            ledger.RecordExpense(ExpenseCategory.RoomUpgrades, amount, description);
        else if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash -= amount;
    }

    private void RecordRefund(double amount, string description)
    {
        var ledger = GetTree()?.Root?.FindChild("FinancialLedger", true, false) as FinancialLedger;

        if (ledger != null)
            ledger.RecordRevenue(RevenueCategory.InformationSales, amount, description);
        else if (GameStateManager.Instance != null)
            GameStateManager.Instance.Cash += amount;
    }

    private void Reject(string reason)
    {
        EmitSignal(SignalName.OnPurchaseRejected, reason);
        GD.Print($"[Catalog] {reason}");
    }

    public override string ToString() => $"[Catalog] tiers 1–{MaxAvailableTier}";
}
