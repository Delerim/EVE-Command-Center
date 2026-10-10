using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public static class IndustryBlueprintLibrary
{
    public sealed record Blueprint(long OwnerId, string Owner, long ItemId, int TypeId, long LocationId,
        string Location, int ME, int TE, long Runs, bool Copy, bool Available, string Status)
    {
        public string? ResolvedName { get; init; }
        public string Name => ResolvedName ?? IndustryCatalog.Name(TypeId);
        public string Icon => $"https://images.evetech.net/types/{TypeId}/{(Copy ? "bpc" : "bp")}?size=64";
        public string Efficiency => $"{(Copy ? "BPC" : "BPO")} | ME {ME}% / TE {TE}%";
        public string RunText => Copy ? Runs.ToString("N0") : "Original";
        public string Color => Available ? "#74D6C9" : "#FFD166";
    }

    public static IReadOnlyList<Blueprint> Read(IEnumerable<IndustryPilot> pilots, ISet<long> authorized, DateTimeOffset now)
    {
        var result = new List<Blueprint>();
        foreach (var pilot in pilots)
        {
            var assets = pilot.Assets.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.First());
            foreach (var bp in pilot.Blueprints)
            {
                long id = IndustryCatalog.Num(bp, "item_id"), location = IndustryCatalog.Num(bp, "location_id");
                int type = checked((int)IndustryCatalog.Num(bp, "type_id"));
                bool fresh = authorized.Contains(pilot.Id) && IndustryWorkspaceInventory.Recent(pilot.Updated, now) && string.IsNullOrWhiteSpace(pilot.Error);
                bool busy = pilot.Jobs.Any(j => IndustryCatalog.Num(j, "blueprint_id") == id && IndustryCatalog.Text(j, "status") is "active" or "paused" or "ready");
                bool copy = IndustryCatalog.Num(bp, "quantity") == -2;
                long runs = IndustryCatalog.Num(bp, "runs");
                bool valid = id > 0 && type > 0 && location > 0 && (!copy || runs > 0);
                result.Add(new(pilot.Id, pilot.Name, id, type, location, IndustryDisplayNames.Path(pilot, location, assets),
                    (int)IndustryCatalog.Num(bp, "material_efficiency"), (int)IndustryCatalog.Num(bp, "time_efficiency"), runs, copy,
                    fresh && !busy && valid, !fresh ? "SNAPSHOT / PERMISSIONS NEED REVIEW" : busy ? "BLUEPRINT IN USE" : !valid ? "RUNS / INSTANCE NEED REVIEW" : "AVAILABLE TO PLAN") { ResolvedName = IndustryDisplayNames.Type(pilot, type) });
            }
        }
        return result.OrderBy(b => b.Name).ThenBy(b => b.Owner).ThenBy(b => b.ItemId).ToArray();
    }
}
