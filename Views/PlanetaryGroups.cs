using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;
public sealed class PiGroupView
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Color { get; set; } = "#74D6C9";
    public string Icon { get; set; } = "";
    public bool Expanded { get; set; }
    public List<PiPlanetView> Planets { get; set; } = new();
}
public sealed class PiPlanetView
{
    public string Key { get; set; } = "";
    public bool Expanded { get; set; }
    public PiRow Row { get; set; } = new();
    public string PlanetIcon => "https://images.evetech.net/types/" + (Row.Colony?.PlanetType.ToLowerInvariant() switch { "temperate" => 11, "ice" => 12, "gas" => 13, "oceanic" => 2014, "lava" => 2015, "barren" => 2016, "storm" => 2017, "plasma" => 2063, _ => 2016 }) + "/icon?size=64";
    public string Facilities { get; set; } = "";
    public string Products { get; set; } = "";
    public bool FactoryWorld { get; set; }
    public List<PiRow> Pins { get; set; } = new();
    public List<PiRefill> Refills { get; set; } = new();
    public string RefillSummary => $"{Refills.Sum(r => r.Need):N0} T1 units to haul | {Refills.Sum(r => r.Missing):N0} shortfall";
}
public static class PlanetaryGroups
{
    public static List<PiGroupView> Build(PiAnalysis analysis, ISet<string> expanded, bool refills = false)
    {
        return analysis.Colonies.GroupBy(r => r.Colony!.CharacterId).Select(g =>
        {
            var planets = g.Select(row =>
            {
                var c = row.Colony!;
                var pins = c.Layout.GetProperty("pins").EnumerateArray().ToArray();
                var extractors = pins.Where(p => p.TryGetProperty("extractor_details", out _)).ToArray();
                var factories = pins.Count(p => p.TryGetProperty("type_id", out var t) && PlanetaryAnalysis.Types.GetValueOrDefault(t.GetInt32())?.Group == 1028);
                var heads = extractors.Sum(p => p.GetProperty("extractor_details").TryGetProperty("heads", out var h) ? h.GetArrayLength() : 0);
                string level = c.UpgradeLevel?.ToString() ?? "pending refresh";
                var details = analysis.Pins.Where(p => p.Colony?.CharacterId == c.CharacterId && p.Colony?.PlanetId == c.PlanetId).ToList();
                string key = (refills ? "refill:" : "planet:") + c.CharacterId + ":" + c.PlanetId;
                return new PiPlanetView { Key = key, Expanded = expanded.Contains(key), Row = row,
                    FactoryWorld = factories > 0 && extractors.Length == 0,
                    Facilities = $"Command centre level {level} | {extractors.Length} extractors / {heads} heads | {factories} factories",
                    Products = string.Join(" | ", details.Where(p => p.Status != "PIN" && p.Status != "STORAGE SNAPSHOT").Select(p => p.Name).Distinct()),
                    Pins = details,
                    Refills = analysis.Refills.Where(r => r.CharacterId == c.CharacterId && r.PlanetId == c.PlanetId && PlanetaryAnalysis.Tier(r.TypeId) == 1).ToList() };
            }).Where(p => !refills || p.Refills.Count > 0).ToList();
            var first = g.First().Colony!;
            int attention = planets.Count(p => p.Row.Color == "#FFD166");
            int collection = planets.Count(p => p.Pins.Any(r => r.Status.StartsWith("COLLECT")));
            string key = (refills ? "refill-pilot:" : "pilot:") + first.CharacterId;
            var haul=analysis.Hauls.FirstOrDefault(h=>h.CharacterId==first.CharacterId);
            return new PiGroupView { Key = key, Expanded = expanded.Contains(key), Name = first.Character, Icon = first.Portrait, Planets = planets,
                Color = !refills && haul?.Stage>0 ? haul.Color : attention > 0 ? "#FFD166" : collection > 0 ? "#80BFFF" : "#74D6C9",
                Summary = $"{planets.Count} colonies | {planets.Count(p => p.FactoryWorld)} factory worlds | " + (refills ? $"{planets.Sum(p => p.Refills.Sum(r => r.Need)):N0} T1 units to haul | {planets.Sum(p => p.Refills.Sum(r => r.Missing)):N0} shortfall" : attention > 0 ? $"{attention} colonies need attention" : collection > 0 ? $"{collection} planets collect / refill" : "Monitoring") + (!refills && haul!=null ? " | " + haul.Summary : "") };
        }).Where(g => g.Planets.Count > 0).ToList();
    }
}
