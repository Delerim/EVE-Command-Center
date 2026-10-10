using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class IndustryDisplayNames
{
    public static string Type(IndustryPilot? pilot, int typeId) => pilot?.TypeNames.GetValueOrDefault(typeId) ?? IndustryCatalog.Name(typeId);
    public static string Path(IndustryPilot? pilot, long locationId, IReadOnlyDictionary<long, EveAssetItem>? knownAssets = null)
    {
        if (pilot == null) return "Location unavailable";
        var assets = knownAssets ?? pilot.Assets.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.First());
        var names = new List<string>(); var seen = new HashSet<long>(); long cursor = locationId;
        while (assets.TryGetValue(cursor, out var asset) && seen.Add(cursor) && seen.Count < 64)
        {
            names.Add(pilot.AssetNames.GetValueOrDefault(cursor) ?? Type(pilot, asset.TypeId));
            cursor = asset.LocationId;
        }
        names.Add(seen.Contains(cursor) ? "Unresolved containment" : pilot.LocationNames.GetValueOrDefault(cursor) ?? "Location name unavailable");
        names.Reverse(); return string.Join("  /  ", names);
    }
}
