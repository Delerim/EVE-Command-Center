using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed record OreWatchSettings(long CharacterId = 0, bool Enabled = false, bool Notifications = true,
    decimal CorpPercent = 90, decimal SellGapPercent = 15, decimal AvoidGapPercent = 20, decimal MinimumAlertIsk = 100_000_000);
public sealed record OreAssetSnapshot(long CharacterId, IReadOnlyList<EveAssetItem> Items,
    DateTimeOffset ObservedUtc, DateTimeOffset ExpiresUtc, long CurrentShipId = 0);
public sealed record OreWatchType(int Id, string Name, int MarketTypeId = 0, string MarketName = "");
public sealed record OreBookOrder(decimal Price, long Volume, long Minimum = 1);
public sealed record OreOrderBook(int TypeId, IReadOnlyList<OreBookOrder> Bids, IReadOnlyList<OreBookOrder> Asks,
    DateTimeOffset ObservedUtc, DateTimeOffset ExpiresUtc)
{
    public decimal? Buy => Bids.Count > 0 ? Bids.Max(x => x.Price) : null;
    public decimal? Sell => Asks.Count > 0 ? Asks.Min(x => x.Price) : null;
    public decimal? ImmediateBuyValue(long quantity)
    {
        long left = quantity; decimal total = 0;
        foreach (var order in Bids.OrderByDescending(x => x.Price))
        {
            if (left <= 0) break;
            if (left < Math.Min(order.Minimum, order.Volume)) continue;
            long fill = Math.Min(left, order.Volume);
            total += fill * order.Price; left -= fill;
        }
        return left == 0 ? total : null;
    }
}

public sealed record OreStashPosition(int TypeId, string Name, long Quantity, long LocationId, string Place, int MarketTypeId = 0, string MarketName = "");
public sealed record OreStashAdvice(int TypeId, string Name, long Quantity, string Locations, decimal? CorpPayout,
    decimal? FullSell, decimal? GapPercent, decimal? ImmediateJitaBuy, string Advice, string Reason, bool DataVerified = false, long UncompressedUnits = 0)
{
    public string Color => Advice == "SELL" ? "#74D6C9" : Advice == "DON'T SELL" ? "#FFAD83" : "#FFD166";
}

public static class OreStashEngine
{
    public static void Validate(OreWatchSettings s)
    {
        if (s.CharacterId < 0 || s.CorpPercent <= 0 || s.CorpPercent > 100 || s.SellGapPercent < 0 ||
            s.SellGapPercent >= s.AvoidGapPercent || s.AvoidGapPercent > 100 || s.MinimumAlertIsk < 0)
            throw new ArgumentException("Choose a corp percentage up to 100 and SELL gap below DON'T SELL gap. Alert value cannot be negative.");
    }
    public static string Advice(decimal gap, decimal sell = 15, decimal avoid = 20) =>
        gap <= sell ? "SELL" : gap >= avoid ? "DON'T SELL" : "ASSESS";

    private static bool ShipHold(string flag) => flag is "Cargo" or "SpecializedOreHold" or "SpecializedAsteroidHold" or "SpecializedIceHold" or "FleetHangar";
    private static bool StorageFlag(string flag) => ShipHold(flag) || flag is "Hangar" or "Unlocked" or "Locked";

    public static IReadOnlyList<OreStashPosition> Positions(OreAssetSnapshot snapshot, IReadOnlyDictionary<int, OreWatchType> ores)
    {
        var all = snapshot.Items.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.First());
        var result = new List<OreStashPosition>();
        foreach (var asset in all.Values.Where(x => x.Quantity > 0 && ores.ContainsKey(x.TypeId)))
        {
            var current = asset; var seen = new HashSet<long> { asset.ItemId };
            var parts = new List<string>(); bool accepted = false; long root = 0;
            for (int depth = 0; depth < 64; depth++)
            {
                if (!StorageFlag(current.LocationFlag)) break;
                if (current.LocationId == snapshot.CurrentShipId && snapshot.CurrentShipId > 0 && ShipHold(current.LocationFlag))
                { root = snapshot.CurrentShipId; parts.Add("Current ship / " + current.LocationFlag); accepted = true; break; }
                if (!all.TryGetValue(current.LocationId, out var parent))
                {
                    if (current.LocationFlag == "Hangar" && current.LocationType is "station" or "other")
                    { root = current.LocationId; parts.Add("Personal hangar"); accepted = true; }
                    break;
                }
                if (!seen.Add(parent.ItemId)) break;
                parts.Add(ShipHold(current.LocationFlag)
                    ? "Ship " + parent.ItemId + " / " + current.LocationFlag : "Container " + parent.ItemId);
                current = parent;
            }
            if (!accepted) continue;
            parts.Reverse();
            var ore = ores[asset.TypeId];
            result.Add(new(asset.TypeId, ore.Name, asset.Quantity, root, string.Join(" / ", parts), ore.MarketTypeId, ore.MarketName));
        }
        return result;
    }

    public static IReadOnlyList<OreStashAdvice> Evaluate(OreAssetSnapshot snapshot, IReadOnlyList<OreStashPosition> positions,
        IReadOnlyDictionary<int, OreOrderBook> books, OreWatchSettings settings, DateTimeOffset now)
    {
        Validate(settings);
        if (snapshot.CharacterId != settings.CharacterId) throw new InvalidOperationException("Asset snapshot belongs to a different character.");
        bool assetsFresh = snapshot.ObservedUtc != default && snapshot.ObservedUtc <= now.AddMinutes(5) &&
            snapshot.ExpiresUtc >= now.AddMinutes(-5);
        var result = new List<OreStashAdvice>();
        foreach (var group in positions.GroupBy(x => x.MarketTypeId > 0 ? x.MarketTypeId : x.TypeId))
        {
            long quantity = group.Aggregate(0L, (sum, x) => checked(sum + x.Quantity));
            string name = string.IsNullOrWhiteSpace(group.First().MarketName) ? group.First().Name : group.First().MarketName;
            long raw = group.Where(x => x.MarketTypeId > 0 && x.MarketTypeId != x.TypeId).Aggregate(0L, (sum, x) => checked(sum + x.Quantity));
            string locations = string.Join("; ", group.Select(x => x.LocationId + ": " + x.Place + (x.MarketTypeId > 0 && x.MarketTypeId != x.TypeId ? $" ({x.Quantity:N0} raw units; compress first)" : "")).Distinct());
            decimal? payout = null, fullSell = null, gap = null, immediate = null;
            bool verified = false;
            string advice = "ASSESS", reason = "Prices unavailable; previous or incomplete data cannot trigger a selling alert.";
            if (books.TryGetValue(group.Key, out var book) && book.TypeId == group.Key && book.Buy is > 0 && book.Sell is > 0)
            {
                payout = quantity * book.Buy.Value * settings.CorpPercent / 100;
                fullSell = quantity * book.Sell.Value;
                gap = (1 - payout.Value / fullSell.Value) * 100;
                immediate = book.ImmediateBuyValue(quantity);
                bool fresh = book.ObservedUtc != default && book.ObservedUtc <= now.AddMinutes(5) && book.ExpiresUtc >= now.AddMinutes(-5);
                if (!assetsFresh) reason = "Inventory snapshot is older or expired. Refresh before deciding.";
                else if (!fresh) reason = "Market quote is older or expired. Refresh before deciding.";
                else if (book.Buy > book.Sell) reason = "Crossed market quotes need review before selling.";
                else if (!immediate.HasValue) reason = "Jita bids cannot cover this stash quantity; assess liquidity before acting.";
                else if (!name.StartsWith("Compressed ", StringComparison.OrdinalIgnoreCase))
                    reason = "Uncompressed ore: assess compression first. Raw and compressed markets are priced separately.";
                else
                {
                    verified = true;
                    advice = Advice(gap.Value, settings.SellGapPercent, settings.AvoidGapPercent);
                    reason = (raw > 0 ? $"After compressing {raw:N0} raw units (1:1 units), " : "") + $"corp {settings.CorpPercent:0.##}% Jita buy is {gap:0.00}% below full Jita sell. " +
                        (advice == "SELL" ? "Reasonable buyback value within your limit." : advice == "DON'T SELL" ? "Large discount; reassess this ore separately." : "Middle ground; weigh convenience, fees and hauling.") +
                        " Quote-based value, not a forecast or guaranteed sell-order proceeds.";
                }
            }
            result.Add(new(group.Key, name, quantity, locations, payout, fullSell, gap, immediate, advice, reason, verified, raw));
        }
        return result.OrderBy(x => x.Name).ToArray();
    }
}
