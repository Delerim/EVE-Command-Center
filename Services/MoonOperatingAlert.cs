using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed record MoonOperatingAlert(string Key, string StructureName, string Message)
{
    public static IReadOnlyList<MoonOperatingAlert> Evaluate(
        IEnumerable<EsiCorporationStructure> structures,
        IEnumerable<EsiMoonExtraction> extractions, DateTimeOffset now)
    {
        var active = extractions.Select(e => e.StructureId).ToHashSet();
        var alerts = new List<MoonOperatingAlert>();
        var all = structures.ToArray();
        var low = all.Where(s => s.FuelExpires < now.AddDays(80)).OrderBy(s => s.FuelExpires).ToArray();
        if (low.Length > 0)
            alerts.Add(new("fuel:all", $"Station fuel | {low.Length} need refuelling",
                $"{low.Length} of {all.Length} structures below 80 days. Lowest: {low[0].Name}, {Math.Max(0, (low[0].FuelExpires!.Value - now).TotalDays):N1} days. Open Station Fuel for the full list."));
        foreach (var structure in all)
        {
            if (!active.Contains(structure.StructureId) && !structure.Services.Any(s =>
                s.Name.Contains("moon", StringComparison.OrdinalIgnoreCase))) continue;
            if (!active.Contains(structure.StructureId))
                alerts.Add(new($"reset:{structure.StructureId}", structure.Name,
                    "No extraction scheduled. Set the next moon drill cycle."));
        }
        return alerts;
    }
}
