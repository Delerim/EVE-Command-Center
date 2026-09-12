using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public static class PlanetaryExtractors
{
    public static List<PiGroupView> Build(PiAnalysis analysis, ISet<string> expanded, DateTimeOffset now)
    {
        double Number(JsonElement p, string key) => p.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) ? n : 0;
        DateTimeOffset? Date(JsonElement p, string key) => p.TryGetProperty(key, out var v) && v.TryGetDateTimeOffset(out var d) ? d : null;
        string Duration(double seconds) => seconds <= 0 ? "Now" : $"{(int)(seconds / 86400)}d {TimeSpan.FromSeconds(seconds).Hours}h {TimeSpan.FromSeconds(seconds).Minutes}m {TimeSpan.FromSeconds(seconds).Seconds}s";
        var groups = PlanetaryGroups.Build(analysis, expanded);
        foreach (var group in groups)
        {
            group.Key = "extractors:" + group.Key;
            group.Expanded = !expanded.Contains("closed:" + group.Key);
            foreach (var planet in group.Planets)
            {
                var colony = planet.Row.Colony!;
                var rows = new List<PiRow>();
                foreach (var pin in colony.Layout.GetProperty("pins").EnumerateArray())
                {
                    if (!pin.TryGetProperty("extractor_details", out var details)) continue;
                    var end = Date(pin, "expiry_time");
                    var start = Date(pin, "install_time");
                    var last = Date(pin, "last_cycle_start") ?? start;
                    double cycle = Number(details, "cycle_time");
                    double left = end.HasValue ? (end.Value - now).TotalSeconds : double.NaN;
                    bool active = left > 0 && cycle > 0 && (!start.HasValue || start <= now);
                    string status = end.HasValue && left <= 0 ? "RESTART DUE" : active ? left <= 3600 ? "UNDER 1 HOUR" : left <= 14400 ? "UNDER 4 HOURS" : "EXTRACTING" : "CHECK PROGRAM";
                    double next = active && last.HasValue ? cycle - Math.Max(0, (now - last.Value).TotalSeconds) % cycle : double.NaN;
                    int product = (int)Number(details, "product_type_id");
                    var type = PlanetaryAnalysis.Types.GetValueOrDefault(product);
                    int heads = details.TryGetProperty("heads", out var h) ? h.GetArrayLength() : 0;
                    rows.Add(new PiRow { Colony = colony, Name = (type?.Name ?? "Unconfigured resource") + $" | {heads} heads", Icon = type?.Icon ?? "", Detail = "Extractor " + Number(pin, "pin_id"),
                        Status = colony.Error.Length > 0 ? "STALE / " + status : status,
                        Color = left <= 0 ? "#FF7777" : !active || left <= 14400 ? "#FFD166" : "#74D6C9",
                        Quantity = cycle > 0 ? Duration(cycle) : "Not configured",
                        Rate = $"{Number(details, "qty_per_cycle"):N0} nominal units/cycle",
                        Next = double.IsFinite(next) && next <= left ? Duration(next) : active ? "Program ending" : "--",
                        Remaining = end.HasValue ? Duration(left) : "Unknown",
                        ResetAt = end.HasValue ? end.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm") : "Unknown" });
                }
                planet.Pins = rows;
                planet.Products = $"{rows.Count} extractors | snapshot {colony.LastUpdate.ToLocalTime():dd MMM HH:mm}";
            }
            group.Planets = group.Planets.Where(p => p.Pins.Count > 0).OrderBy(p => p.Row.Name).ToList();
            int due = group.Planets.Sum(p => p.Pins.Count(r => r.Color == "#FF7777"));
            group.Summary = $"{group.Planets.Count} planets | {group.Planets.Sum(p => p.Pins.Count)} extractors | {due} restart due";
            group.Color = due > 0 ? "#FF7777" : group.Planets.Any(p => p.Pins.Any(r => r.Color == "#FFD166")) ? "#FFD166" : "#74D6C9";
        }
        return groups.Where(g => g.Planets.Count > 0).OrderBy(g => g.Name).ToList();
    }
}
