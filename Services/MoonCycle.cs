using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public static class MoonCycle
{
    public static long ChooseAnchor(IEnumerable<MoonPullRecord> pulls) => pulls
        .Where(p => p.FracturedUtc.HasValue)
        .OrderBy(p => p.FracturedUtc).Select(p => p.StructureId).FirstOrDefault();

    public static MoonCycleSummary Build(MoonReportState state, DateTimeOffset now,
        Func<MoonPullRecord, double> loss, Func<MoonPullRecord, bool>? visible = null)
    {
        var displayed = state.Pulls.Values.Where(p => visible == null || visible(p)).ToArray();
        var anchor = state.CycleAnchorStructureId;
        if (anchor == 0 || !displayed.Any(p => p.StructureId == anchor))
            anchor = ChooseAnchor(displayed);
        var starts = displayed.Where(p => p.StructureId == anchor && p.FracturedUtc <= now).OrderBy(p => p.FracturedUtc).ToArray();
        var first = starts.LastOrDefault();
        if (first?.FracturedUtc is not { } start) return new();
        var pulls = displayed.Where(p => p.FracturedUtc >= start && p.FracturedUtc <= now).ToArray();
        var next = displayed.Where(p => p.StructureId == anchor && p.ChunkArrivalUtc > now).OrderBy(p => p.ChunkArrivalUtc).FirstOrDefault();
        return new MoonCycleSummary
        {
            Start = start, Anchor = first.StructureName, NextExpected = next?.ChunkArrivalUtc,
            Fractured = pulls.Length, Jackpots = pulls.Count(p => p.JackpotObserved),
            MinedM3 = pulls.Sum(p => p.MinedM3ByOre.Values.Sum()),
            LostM3 = pulls.Where(p => p.ExpiredUtc.HasValue && !p.OutcomeUnobserved).Sum(loss),
            ExpiredWithOre = pulls.Count(p => p.ExpiredUtc.HasValue && !p.OutcomeUnobserved && loss(p) >= 1000)
        };
    }
}
