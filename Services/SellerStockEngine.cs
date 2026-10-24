using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed record SellerTypeInfo(int TypeId, string Name, int GroupId, int CategoryId, string Category);
public sealed record SellerHubQuote(int TypeId, decimal? Jita, decimal? Amarr, DateTimeOffset CheckedUtc);

public sealed class SellerStockRow
{
    public int TypeId { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Unknown";
    public string Location { get; init; } = "";
    public string ContainerPath { get; init; } = "";
    public long Stock { get; init; }
    public long Listed { get; init; }
    public int OpenOrders { get; init; }
    public decimal? MyPrice { get; init; }
    public string OrderHub { get; init; } = "Other";
    public decimal? Jita { get; init; }
    public decimal? Amarr { get; init; }
    public decimal? PreviousJita { get; init; }
    public decimal? PreviousAmarr { get; init; }
    public bool MarketFresh { get; init; }
    public bool OrdersFresh { get; init; }
    public bool ContainsContents { get; init; }
    public bool IsSingleton { get; init; }
    public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=64";
    public string QtyText => Stock.ToString("N0", CultureInfo.InvariantCulture);
    public string ListedText => Listed.ToString("N0", CultureInfo.InvariantCulture);
    public string JitaText => Price(Jita);
    public string AmarrText => Price(Amarr);
    public string MyPriceText => Price(MyPrice);
    public string JitaShift => Delta(Jita, PreviousJita);
    public string AmarrShift => Delta(Amarr, PreviousAmarr);
    public string State => !OrdersFresh ? "ORDERS STALE" : Listed == 0 ? "NOT LISTED" :
        !MarketFresh ? "PRICE STALE" :
        MyPrice.HasValue && ((OrderHub == "Jita" && Jita.HasValue && MyPrice.Value > Jita.Value) ||
                             (OrderHub == "Amarr" && Amarr.HasValue && MyPrice.Value > Amarr.Value))
            ? "UNDERCUT" : "LISTED";
    public string StateColor => State switch { "NOT LISTED" => "#FFC470", "UNDERCUT" => "#FF848B", "LISTED" => "#70E2BC", "PRICE STALE" => "#E8BB82", _ => "#AABEC2" };
    public string PreferredHub => Jita is > 0 && Amarr is > 0
        ? Amarr > Jita ? "AMARR" : Jita > Amarr ? "JITA" : "EITHER"
        : Amarr is > 0 ? "AMARR*" : Jita is > 0 ? "JITA*" : "PRICE UNAVAILABLE";
    public string HubEdge => Jita is > 0 && Amarr is > 0
        ? Price(Math.Abs(Jita.Value - Amarr.Value)) + " / item" : "-";
    public string Advice => !OrdersFresh ? "Market orders are stale. Refresh before changing listings." :
        Listed == 0 ? $"{Stock:N0} in inventory; no active sell order for this type." :
        $"{Stock:N0} in inventory + {Listed:N0} listed in {OpenOrders} order(s).";
    public string Detail => $"{Name} | {Category} | {Location} | {ContainerPath} | {Advice} " +
                            $"Jita 4-4 {JitaText}, Amarr station {AmarrText}. Best hub {PreferredHub} is price-only, before fees and hauling." +
                            (ContainsContents ? " Contains other items; inspect before repackaging or selling." : "") +
                            (IsSingleton ? " Singleton/assembled item may need repackaging." : "");
    public static string Price(decimal? price) => price.HasValue && price.Value > 0
        ? price.Value.ToString("N2", CultureInfo.InvariantCulture) : "—";
    private static string Delta(decimal? current, decimal? previous) =>
        current is > 0 && previous is > 0 ? (current.Value - previous.Value).ToString("+N2;-N2;0", CultureInfo.InvariantCulture) : "—";
}

public static class SellerStockEngine
{
    // EVE's SKINR license inventory lives on a separate API; also explicitly skip marketable cosmetics.
    private static readonly HashSet<string> CosmeticCategories = new(StringComparer.OrdinalIgnoreCase)
        { "SKINs", "Apparel", "Personalization", "Accessories" };
    private static readonly Regex CosmeticName = new(@"\bSKINs?\b|SKINR|nanocoating|paragon pattern|apparel|clothing|chromodynamic|design element",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FittedFlag = new(@"^(HiSlot|HighSlot|MedSlot|LoSlot|LowSlot|RigSlot|SubSystemSlot|ServiceSlot|Implant)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsSellable(SellerTypeInfo info) =>
        !CosmeticCategories.Contains(info.Category) &&
        !CosmeticName.IsMatch(info.Name) &&
        !info.Category.Equals("Blueprint", StringComparison.OrdinalIgnoreCase) &&
        !info.Category.Equals("Skill", StringComparison.OrdinalIgnoreCase);

    // Only assets rooted in a personal hangar; follow any number of nested containers safely.
    public static bool TryGetHangarPath(EveAssetItem asset, IReadOnlyDictionary<long, EveAssetItem> all,
        IReadOnlyDictionary<long,string> locationNames, IReadOnlyDictionary<long,string> containerNames,
        out string station, out string chain)
    {
        station = ""; chain = "";
        if (FittedFlag.IsMatch(asset.LocationFlag)) return false;
        var parts = new List<string>();
        var seen = new HashSet<long> { asset.ItemId };
        var current = asset;
        for (int depth = 0; depth < 64; depth++)
        {
            if (!all.TryGetValue(current.LocationId, out var parent))
            {
                if (current.LocationFlag != "Hangar" ||
                    (current.LocationType != "station" && current.LocationType != "other")) return false;
                station = locationNames.GetValueOrDefault(current.LocationId, $"Location {current.LocationId}");
                parts.Reverse(); chain = parts.Count == 0 ? "Main hangar" : string.Join(" / ", parts);
                return true;
            }
            if (!seen.Add(parent.ItemId) || FittedFlag.IsMatch(current.LocationFlag)) return false;
            // Containers nested in containers are eligible; fitted modules are not.
            parts.Add(containerNames.GetValueOrDefault(parent.ItemId, $"Container {parent.ItemId}"));
            current = parent;
        }
        return false;
    }

    public static IReadOnlyList<SellerStockRow> Build(
        IReadOnlyList<EveAssetItem> assets, IReadOnlyList<MarketOrder> orders,
        IReadOnlyDictionary<int,SellerTypeInfo> types,
        IReadOnlyDictionary<long,string> locationNames,
        IReadOnlyDictionary<long,string> containerNames,
        IReadOnlyDictionary<int,SellerHubQuote> quotes,
        IReadOnlyDictionary<int,SellerHubQuote> previous,
        bool ordersFresh, DateTimeOffset now)
    {
        var parentMap = assets.Where(a => a.ItemId > 0).GroupBy(a => a.ItemId).ToDictionary(g => g.Key,g => g.First());
        var childIds = assets.Select(x => x.LocationId).ToHashSet();
        var sell = orders.Where(x => !x.Buy && !x.Corporation && x.Remaining > 0).GroupBy(o => o.TypeId)
                         .ToDictionary(g=>g.Key,g=>g.ToArray());
        var positioned = new List<(EveAssetItem Asset,string Location,string Path)>();
        foreach (var asset in assets)
        {
            if (asset.TypeId <= 0 || asset.Quantity <= 0) continue;
            if (!types.TryGetValue(asset.TypeId,out var info) || !IsSellable(info)) continue;
            if (childIds.Contains(asset.ItemId) && info.Category.Equals("Accessories", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetHangarPath(asset,parentMap,locationNames,containerNames,out var location,out var path)) continue;
            // Station and container shells are not stock for listing when acting as a parent.
            if (childIds.Contains(asset.ItemId) && (info.Name.Contains("Container",StringComparison.OrdinalIgnoreCase) || info.Name.Contains("Hangar",StringComparison.OrdinalIgnoreCase))) continue;
            positioned.Add((asset,location,path));
        }
        var result = new List<SellerStockRow>();
        foreach(var group in positioned.GroupBy(x => (x.Asset.TypeId, x.Location, x.Path)))
        {
            var first=group.First();var type=types[first.Asset.TypeId];
            var active=sell.GetValueOrDefault(type.TypeId) ?? Array.Empty<MarketOrder>();
            quotes.TryGetValue(type.TypeId,out var quote);previous.TryGetValue(type.TypeId,out var before);
            result.Add(new SellerStockRow {
                TypeId=type.TypeId,Name=type.Name,Category=type.Category,
                Location=first.Location,ContainerPath=first.Path,Stock=group.Sum(x=>x.Asset.Quantity),
                Listed=active.Sum(o=>o.Remaining),OpenOrders=active.Length,
                MyPrice=active.Length>0?active.Min(o=>o.Price):null,
                OrderHub=active.Length>0 ? active.Select(o=>o.LocationId).Distinct().Count()>1 ? "Multiple" :
                    active[0].LocationId==SellerDashboardService.Jita44 ? "Jita" :
                    active[0].LocationId==SellerDashboardService.AmarrEmperor ? "Amarr" : "Other" : "None",
                Jita=quote?.Jita,Amarr=quote?.Amarr,
                PreviousJita=before?.Jita,PreviousAmarr=before?.Amarr,
                MarketFresh=quote!=null && quote.CheckedUtc>=now.AddMinutes(-35),
                OrdersFresh=ordersFresh,
                ContainsContents=group.Any(x=>childIds.Contains(x.Asset.ItemId)),
                IsSingleton=group.Any(x=>x.Asset.IsSingleton)
            });
        }
        return result.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Location).ToArray();
    }
}
