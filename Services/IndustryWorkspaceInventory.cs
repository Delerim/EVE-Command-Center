using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public static class IndustryWorkspaceInventory
{
    public static bool Recent(DateTimeOffset time, DateTimeOffset now) =>
        time != default && time <= now.AddMinutes(5) && time >= now.AddHours(-1);

    public static IReadOnlyList<IndustryStockSource> Read(IEnumerable<IndustryPilot> pilots, DateTimeOffset now, ISet<long>? authorizedOwners = null)
    {
        var result = new List<IndustryStockSource>();
        foreach (var pilot in pilots)
        {
            var assets = pilot.Assets.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.First());
            foreach (var asset in pilot.Assets.Where(a => !a.IsSingleton && a.Quantity > 0))
            {
                var path = new List<string>();
                var seen = new HashSet<long>();
                var current = asset;
                bool valid = true;
                while (true)
                {
                    if (!seen.Add(current.ItemId) || seen.Count > 64) { valid = false; path.Add("Containment unresolved"); break; }
                    if (current.LocationFlag != "Hangar" && current.LocationFlag != "Unlocked" && current.LocationFlag != "Locked")
                    { valid = false; path.Add("Not a personal hangar/container"); break; }
                    if (assets.TryGetValue(current.LocationId, out var parent))
                    {
                        path.Add($"{IndustryCatalog.Name(parent.TypeId)} [item {parent.ItemId}]");
                        current = parent;
                        continue;
                    }
                    if (current.LocationType is not ("station" or "other") || current.LocationFlag != "Hangar")
                    { valid = false; path.Add($"Unverified parent {current.LocationId}"); }
                    else path.Add($"Location {current.LocationId} / Hangar");
                    break;
                }
                path.Reverse();
                bool fresh = Recent(pilot.Updated, now) && string.IsNullOrWhiteSpace(pilot.Error) && (authorizedOwners == null || authorizedOwners.Contains(pilot.Id));
                result.Add(new(pilot.Id, pilot.Name, asset.ItemId, asset.TypeId, asset.LocationId,
                    asset.LocationFlag, string.Join(" / ", path), asset.Quantity, pilot.Updated, valid && fresh,
                    !valid ? "Unverified location; cannot reserve" : !fresh ? "Snapshot stale, missing permission or refresh failed; cannot reserve" : "Observed personal stock; delivery not verified"));
            }
        }
        return result;
    }

    public static string Review(IndustryReservation reservation, IEnumerable<IndustryReservation> all,
        IReadOnlyList<IndustryStockSource> stock, DateTimeOffset now)
    {
        var source = stock.FirstOrDefault(s => s.OwnerId == reservation.OwnerId && s.ItemId == reservation.ItemId);
        if (source == null || !source.Verified || !Recent(source.SnapshotUtc, now)) return "UNVERIFIED - retain reservation, review snapshot";
        if (source.TypeId != reservation.TypeId || source.LocationId != reservation.LocationId ||
            source.LocationFlag != reservation.LocationFlag || source.Path != reservation.Path) return "MOVED / CHANGED - review source";
        decimal allocated = all.Where(r => r.OwnerId == source.OwnerId && r.ItemId == source.ItemId).Sum(r => (decimal)r.Quantity);
        return allocated > source.Quantity ? "SHORT STOCK - reservations need review" : "RESERVED - transfer / job delivery unverified";
    }
}
