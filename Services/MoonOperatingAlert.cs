using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public sealed record MoonOperatingAlert(string Key, string StructureName, string Message)
{
    public static IReadOnlyList<MoonOperatingAlert> Evaluate(
        IEnumerable<EsiCorporationStructure> structures,
        IEnumerable<EsiMoonExtraction> extractions, DateTimeOffset now)
    {
        var active = extractions.Select(e => e.StructureId).ToHashSet();
        var alerts = new List<MoonOperatingAlert>();
        foreach (var structure in structures)
        {
            if (!active.Contains(structure.StructureId) && !structure.Services.Any(s =>
                s.Name.Contains("moon", StringComparison.OrdinalIgnoreCase))) continue;
            if (structure.FuelExpires is { } expiry && expiry < now.AddDays(14))
                alerts.Add(new($"fuel:{structure.StructureId}", structure.Name,
                    expiry <= now ? "Fuel has expired. Refuel the structure." :
                    $"Fuel expires {expiry.ToLocalTime():dd MMM HH:mm} • {(expiry - now).TotalDays:F1} days left."));
            if (!active.Contains(structure.StructureId))
                alerts.Add(new($"reset:{structure.StructureId}", structure.Name,
                    "No extraction scheduled. Set the next moon drill cycle."));
        }
        return alerts;
    }
}
