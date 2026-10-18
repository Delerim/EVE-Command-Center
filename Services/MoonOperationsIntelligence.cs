using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class MoonIntelligenceOreRow
{
    public string Name { get; init; } = "";
    public double RemainingM3 { get; init; }
    public double DespawnedM3 { get; init; }
    public double MinedM3 { get; init; }
    public double Width { get; init; }
    public string RemainingText => MoonReportService.FormatM3(RemainingM3);
    public string LostText => MoonReportService.FormatM3(DespawnedM3);
    public string MinedText => MoonReportService.FormatM3(MinedM3);
}

public sealed class MoonIntelligenceTrendRow
{
    public DateTime Date { get; init; }
    public int Fractures { get; init; }
    public double Height { get; init; }
    public string Label => Date.ToString("dd");
    public string Tip => $"{Date:dd MMM yyyy} UTC: {Fractures} recorded fractures";
}

public sealed class MoonIntelligenceReport
{
    public string Period { get; init; } = "Week";
    public DateTimeOffset StartUtc { get; init; }
    public DateTimeOffset EndUtc { get; init; }
    public DateTimeOffset GeneratedUtc { get; init; }
    public DateTimeOffset? DataUpdatedUtc { get; init; }
    public int Structures { get; init; }
    public int DrillCount { get; init; }
    public int Fractures { get; init; }
    public int ActiveFields { get; init; }
    public int InferredFields { get; init; }
    public int NotSet { get; init; }
    public int Running { get; init; }
    public int Ready { get; init; }
    public int UnknownService { get; init; }
    public int NextSevenDays { get; init; }
    public int LostFields { get; init; }
    public int LossesUnquantified { get; init; }
    public int ProfiledActiveFields { get; init; }
    public double? AverageFuelDays { get; init; }
    public double? MedianFuelDays { get; init; }
    public int FuelKnown { get; init; }
    public int FuelLow { get; init; }
    public int FuelUnknown { get; init; }
    public IReadOnlyList<MoonIntelligenceOreRow> Ores { get; init; } = Array.Empty<MoonIntelligenceOreRow>();
    public IReadOnlyList<MoonIntelligenceTrendRow> Trend { get; init; } = Array.Empty<MoonIntelligenceTrendRow>();
    public string Title => $"{Period.ToUpperInvariant()} | {StartUtc:dd MMM yyyy} - {EndUtc.AddDays(-1):dd MMM yyyy} (UTC)";
    public string Freshness => DataUpdatedUtc is { } t
        ? $"Last ESI refresh {t:dd MMM yyyy HH:mm} UTC" +
          (GeneratedUtc - t > TimeSpan.FromMinutes(75) ? " | STALE" : "")
        : "No successful ESI refresh yet | data incomplete";
    public string FuelText => AverageFuelDays is { } d ? $"{d:N1} days average / {MedianFuelDays:N1} median" : "Unknown";
    public string Coverage => $"{ProfiledActiveFields}/{ActiveFields} observed active fields have ore profiles; {LossesUnquantified} expired fields have unknown losses";

    // Keep below Discord's embed description limit; never include secrets, member lists or private raw CSV.
    public string DiscordBody()
    {
        var ores = Ores.ToArray();
        var lines = new List<string>
        {
            $"**{Title}**",
            Freshness,
            $"**Structures:** {Structures:N0} | Moon drills: {DrillCount:N0} | Unscheduled: {NotSet:N0} | Unknown service: {UnknownService:N0}",
            $"**Fractures recorded:** {Fractures:N0} | Recently mined fields: {ActiveFields:N0} | Inferred (not counted): {InferredFields:N0}",
            $"**Scheduled:** {Running:N0} running | {Ready:N0} ready | {NextSevenDays:N0} due in next 7 days",
            $"**Fuel:** {FuelText} | Below 80d: {FuelLow:N0} | Unknown: {FuelUnknown:N0}",
            "**ORE | Estimated in active fields | Estimated lost at expiry**"
        };
        lines.AddRange(ores.Select(o => $"{o.Name}: {o.RemainingText} | {o.LostText}"));
        lines.Add($"**Despawn audit:** {LostFields:N0} recorded expiries in period | {LossesUnquantified:N0} cannot be quantified");
        lines.Add(Coverage);
        lines.Add("_ESI snapshots and saved ledger; ore/losses are estimates, not live rock scans. No verified wallet revenue is implied._");
        return string.Join("\n", lines);
    }
}

public static class MoonOperationsIntelligence
{
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateTimeOffset now, string period)
    {
        var date = now.UtcDateTime.Date;
        DateTime start = period switch
        {
            "Month" => new DateTime(date.Year, date.Month, 1),
            "Yesterday" => date.AddDays(-1),
            "Last week" => date.AddDays(-((int)date.DayOfWeek + 6) % 7 - 7),
            "Last month" => new DateTime(date.Year, date.Month, 1).AddMonths(-1),
            _ => date.AddDays(-((int)date.DayOfWeek + 6) % 7)
        };
        DateTime end = period switch
        {
            "Week" or "Last week" => start.AddDays(7),
            "Yesterday" => start.AddDays(1),
            _ => start.AddMonths(1)
        };
        return (new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(end, TimeSpan.Zero));
    }

    public static MoonIntelligenceReport Build(MoonReportState state,
        MoonReportSnapshot snapshot, IReadOnlyList<MoonDrillRow> drills,
        DateTimeOffset now, string period,
        Func<MoonPullRecord, bool> lossQuantifiable,
        Func<MoonPullRecord, string, double> remainingAtExpiry)
    {
        var (start, end) = Range(now, period);
        var discovered = state.EsiKnownStructureIds;
        var known = state.Pulls.Values.Where(p =>
            (p.StructureId > 0 && discovered.Contains(p.StructureId)) ||
            (p.MoonId > 0 && state.EsiKnownMoonIds.Contains(p.MoonId))).ToArray();
        var fractures = known.Where(p => p.FracturedUtc >= start && p.FracturedUtc < end).ToArray();
        var expired = known.Where(p => p.ExpiredUtc >= start && p.ExpiredUtc < end).ToArray();
        var quantifiable = expired.Where(lossQuantifiable).ToArray();
        var observed = snapshot.CalendarCards.Where(c => c.Status == "FIELD ACTIVE").ToArray();
        var oreNames = new[] { "Zeolites", "Sylvite", "Bitumens", "Coesite" };
        var families = new[] { "zeolit", "sylvit", "bitumen", "coesite" };
        var lost = families.Select(f => quantifiable.Sum(p => Math.Max(0, remainingAtExpiry(p, f)))).ToArray();
        var mined = snapshot.DailyTotals.Where(d => d.Date >= start.UtcDateTime.Date && d.Date < end.UtcDateTime.Date).ToArray();
        var minedAmounts = new[] { mined.Sum(d => d.ZeolitesM3), mined.Sum(d => d.SylviteM3), mined.Sum(d => d.BitumensM3), mined.Sum(d => d.CoesiteM3) };
        var remaining = new[] { observed.Sum(c => c.ZeolitesRemainingM3), observed.Sum(c => c.SylviteRemainingM3), observed.Sum(c => c.BitumensRemainingM3), observed.Sum(c => c.CoesiteRemainingM3) };
        var max = Math.Max(1, remaining.Max());
        var ores = Enumerable.Range(0, 4).Select(i => new MoonIntelligenceOreRow
        {
            Name = oreNames[i], RemainingM3 = remaining[i], DespawnedM3 = lost[i],
            MinedM3 = minedAmounts[i], Width = Math.Max(0, remaining[i] / max * 230)
        }).ToArray();
        var fuel = snapshot.Fuel.Where(f => discovered.Contains(f.StructureId) && f.Days.HasValue).Select(f => f.Days!.Value).OrderBy(n => n).ToArray();
        double? median = fuel.Length == 0 ? null : (fuel[(fuel.Length-1)/2] + fuel[fuel.Length/2])/2.0;
        var fractureDays = fractures.GroupBy(f => f.FracturedUtc!.Value.UtcDateTime.Date).ToDictionary(g => g.Key, g => g.Count());
        var days = Enumerable.Range(0, (int)(end-start).TotalDays).Select(i => start.UtcDateTime.Date.AddDays(i)).ToArray();
        var maxFractures = Math.Max(1, fractureDays.Count == 0 ? 0 : fractureDays.Values.Max());
        var trend = days.Select(day => new MoonIntelligenceTrendRow
        { Date = day, Fractures = fractureDays.GetValueOrDefault(day), Height = Math.Max(2, fractureDays.GetValueOrDefault(day) * 100.0 / maxFractures) }).ToArray();
        return new MoonIntelligenceReport
        {
            Period = period, StartUtc = start, EndUtc = end, GeneratedUtc = now,
            DataUpdatedUtc = state.LastRefreshUtc,
            Structures = discovered.Count, DrillCount = drills.Count, Fractures = fractures.Length,
            ActiveFields = snapshot.ActiveFieldCount, InferredFields = snapshot.InferredFieldCount,
            ProfiledActiveFields = observed.Count(c => c.HasTargetProfile),
            NotSet = drills.Count(d => d.NotSet), Running = drills.Count(d => d.Status == "RUNNING"),
            Ready = drills.Count(d => d.Status == "READY TO FRACTURE"),
            UnknownService = drills.Count(d => d.Status.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) || d.Status == "NOT VISIBLE"),
            NextSevenDays = drills.Count(d => d.Arrival >= now && d.Arrival < now.AddDays(7)),
            LostFields = expired.Length, LossesUnquantified = expired.Length-quantifiable.Length,
            AverageFuelDays = fuel.Length == 0 ? null : fuel.Average(), MedianFuelDays = median,
            FuelKnown = fuel.Length, FuelUnknown = snapshot.Fuel.Count(f => discovered.Contains(f.StructureId) && !f.Days.HasValue),
            FuelLow = fuel.Count(d => d < 80), Ores = ores, Trend = trend
        };
    }
}
