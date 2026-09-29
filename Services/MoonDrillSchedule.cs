using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public sealed record MoonDrillRow(long StructureId, string System, string Moon, string Structure,
    string Status, string Service, DateTimeOffset? Start, DateTimeOffset? Arrival, string Duration,
    string Elapsed, string Remaining)
{
    public bool NotSet => Status == "NOT SET";
    public string StartText => Start?.ToUniversalTime().ToString("dd MMM yyyy HH:mm") ?? "-";
    public string ArrivalText => Arrival?.ToUniversalTime().ToString("dd MMM yyyy HH:mm") ?? "-";
}

internal static class MoonDrillSchedule
{
    internal static IReadOnlyList<MoonDrillRow> Build(MoonReportState state, DateTimeOffset now)
    {
        var ids = state.Structures.Where(s => s.Services.Any(x => x.Name.Contains("moon", StringComparison.OrdinalIgnoreCase)))
            .Select(s => s.StructureId)
            .Concat(state.Profiles.Values.Select(p => p.StructureId))
            .Concat(state.Pulls.Values.Where(p => p.SeenInLatestExtractionList).Select(p => p.StructureId))
            .Where(id => id > 0).Distinct();
        var rows = new List<MoonDrillRow>();
        foreach (long id in ids)
        {
            var structure = state.Structures.FirstOrDefault(s => s.StructureId == id);
            var profile = state.Profiles.Values.Where(p => p.StructureId == id).OrderByDescending(p => p.MoonId > 0).FirstOrDefault();
            var pull = state.Pulls.Values.Where(p => p.StructureId == id && p.SeenInLatestExtractionList && !p.FracturedUtc.HasValue && !p.ExpiredUtc.HasValue)
                .OrderByDescending(p => p.ExtractionStartUtc).FirstOrDefault();
            bool visibleDrill = structure?.Services.Any(x => x.Name.Contains("moon", StringComparison.OrdinalIgnoreCase)) == true;
            string status = pull != null ? (now < pull.ExtractionStartUtc ? "SCHEDULED" : now < pull.ChunkArrivalUtc ? "RUNNING" : "READY TO FRACTURE")
                : !state.LastRefreshUtc.HasValue ? "NOT CHECKED" : visibleDrill ? "NOT SET" : "NOT VISIBLE";
            TimeSpan? duration = pull == null ? null : pull.ChunkArrivalUtc - pull.ExtractionStartUtc;
            rows.Add(new(id, profile?.SystemName ?? pull?.SystemName ?? state.SystemNames.GetValueOrDefault(structure?.SystemId ?? 0, "Unknown"),
                profile?.MoonName ?? pull?.MoonName ?? "Moon pending ESI", structure?.Name ?? profile?.StructureName ?? pull?.StructureName ?? id.ToString(),
                status, structure?.Services.FirstOrDefault(x => x.Name.Contains("moon", StringComparison.OrdinalIgnoreCase))?.State ?? "Unknown",
                pull?.ExtractionStartUtc, pull?.ChunkArrivalUtc, duration is { } d ? Format(d) : "-",
                pull == null ? "-" : Format((now < pull.ChunkArrivalUtc ? now : pull.ChunkArrivalUtc) - pull.ExtractionStartUtc),
                pull == null ? "-" : Format(pull.ChunkArrivalUtc - now)));
        }
        return rows.OrderBy(r => r.NotSet ? 0 : r.Status == "READY TO FRACTURE" ? 1 : 2)
            .ThenBy(r => r.System).ThenBy(r => r.Moon).ThenBy(r => r.Structure).ToArray();
    }
    private static string Format(TimeSpan value)
    {
        if(value < TimeSpan.Zero) value = TimeSpan.Zero;
        return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
    }
}
