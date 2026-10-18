using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Reporting deliberately uses only persisted ESI contract history and individually
// verified ESI contents. No projected mining, wallet payments, margins or invented
// missing contracts are added to these totals.
public sealed class BuybackRankRow
{
    public string Label { get; init; } = "";
    public decimal Isk { get; init; }
    public double CubicMetres { get; init; }
    public int Contracts { get; init; }
    public double Width { get; init; }
    public string IskText => Isk.ToString("N0", CultureInfo.InvariantCulture) + " ISK";
    public string VolumeText => CubicMetres.ToString("N0", CultureInfo.InvariantCulture) + " m³";
    public string Subtitle => Contracts.ToString("N0", CultureInfo.InvariantCulture) + " contracts";
}

public sealed class BuybackAnalyticsSnapshot
{
    public DateTime StartUtc { get; init; }
    public DateTime EndExclusiveUtc { get; init; }
    public int Count { get; init; }
    public int PreviousCount { get; init; }
    public decimal ValueIsk { get; init; }
    public decimal PreviousValueIsk { get; init; }
    public double KnownVolumeM3 { get; init; }
    public int IndexedContracts { get; init; }
    public int ContentCoveragePercent => Count == 0 ? 0 : (int)Math.Round(100.0 * IndexedContracts / Count);
    public int Outstanding { get; init; }
    public int ExpiredOrCancelled { get; init; }
    public int PeriodContracts { get; init; }
    public IReadOnlyList<BuybackBucket> TimeBuckets { get; init; } = Array.Empty<BuybackBucket>();
    public IReadOnlyList<BuybackRankRow> Suppliers { get; init; } = Array.Empty<BuybackRankRow>();
    public IReadOnlyList<BuybackRankRow> Contents { get; init; } = Array.Empty<BuybackRankRow>();
    public IReadOnlyList<ContractRow> AcceptedContracts { get; init; } = Array.Empty<ContractRow>();
    public string ValueText => ValueIsk >= 1_000_000_000m ? $"{ValueIsk / 1_000_000_000m:N2}B ISK" : $"{ValueIsk / 1_000_000m:N2}M ISK";
    public string PreviousText => PreviousValueIsk >= 1_000_000_000m ? $"{PreviousValueIsk / 1_000_000_000m:N2}B" : $"{PreviousValueIsk / 1_000_000m:N2}M";
    public string ChangeText => PreviousValueIsk == 0 ? "No comparable previous-period total" : $"{(ValueIsk - PreviousValueIsk) / PreviousValueIsk * 100:+0.0;-0.0;0.0}% vs prior period";
    public string VolumeText => IndexedContracts == 0 ? "Not indexed" : $"{KnownVolumeM3:N0} m³";
    public string CoverageText => $"{IndexedContracts:N0} / {Count:N0} buybacks indexed ({ContentCoveragePercent}%)";
}

public static class BuybackAnalytics
{
    public static string Key(ContractRow row) => $"{row.CorporationId}:{row.Contract.Id}";

    public static (DateTime Start, DateTime End) Range(DateTime anchorUtc, string period,
        DateTime? customFromUtc = null, DateTime? customToUtc = null)
    {
        if (period == "Custom")
        {
            DateTime from = (customFromUtc ?? anchorUtc).Date;
            DateTime to = (customToUtc ?? from).Date;
            if (to < from) (from, to) = (to, from);
            return (from, to.AddDays(1));
        }
        DateTime first = BuybackReport.Start(anchorUtc, period);
        return (first, BuybackReport.End(first, period));
    }

    public static BuybackAnalyticsSnapshot Build(IEnumerable<ContractRow> history,
        long corporation, DateTime start, DateTime end,
        IReadOnlyDictionary<string, List<ContractItem>> knownItems, string period = "Custom",
        IEnumerable<ContractRow>? liveOutstanding = null)
    {
        if (end <= start) throw new ArgumentOutOfRangeException(nameof(end));
        var all = history.Where(r => r.CorporationId == corporation)
            .GroupBy(r => r.Contract.Id).Select(g => g.OrderByDescending(r => r.Contract.Completed ?? r.Contract.Accepted ?? r.Contract.Issued).First()).ToArray();
        var accepted = BuybackReport.Qualifying(all, corporation, start, end)
            .OrderByDescending(r => r.Contract.Accepted).ToArray();
        TimeSpan duration = end - start;
        // Monthly and yearly comparisons must use the previous CALENDAR period,
        // not an equal-day window that can leak into an extra preceding month.
        DateTime previousStart = period switch
        {
            "Week" => start.AddDays(-7),
            "Month" => start.AddMonths(-1),
            "Year" => start.AddYears(-1),
            _ => start - duration
        };
        var previous = BuybackReport.Qualifying(all, corporation, previousStart, start);
        var rowsInPeriod = all.Where(r => r.Contract.Issued.UtcDateTime >= start && r.Contract.Issued.UtcDateTime < end).ToArray();
        var indexed = accepted.Where(r => knownItems.ContainsKey(Key(r))).ToArray();
        var volumeByLabel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in indexed)
        {
            foreach (var item in knownItems[Key(row)].Where(i => i.Included && !i.BlueprintCopy && i.Quantity > 0 && i.UnitVolume > 0))
            {
                // Content names come from ESI. Unclassified items remain explicitly
                // labelled; no attempt is made to treat every contract item as ore.
                string label = Classify(item.Name);
                volumeByLabel[label] = volumeByLabel.GetValueOrDefault(label) + item.Volume;
            }
        }
        var suppliers = accepted.GroupBy(r => string.IsNullOrWhiteSpace(r.Issuer) ? $"Issuer {r.Contract.IssuerId}" : r.Issuer,
            StringComparer.OrdinalIgnoreCase).Select(g => new { Label = g.Key, Isk = g.Sum(r => r.Contract.Price ?? 0), Count = g.Count() })
            .OrderByDescending(g => g.Isk).Take(8).ToArray();
        decimal maxSupplier = suppliers.Length == 0 ? 0 : suppliers.Max(s => s.Isk);
        var mixes = volumeByLabel.OrderByDescending(v => v.Value).Take(10).ToArray();
        double maxVolume = mixes.Length == 0 ? 0 : mixes.Max(s => s.Value);
        return new BuybackAnalyticsSnapshot
        {
            StartUtc = start, EndExclusiveUtc = end,
            Count = accepted.Length, PreviousCount = previous.Count,
            ValueIsk = accepted.Sum(r => r.Contract.Price ?? 0),
            PreviousValueIsk = previous.Sum(r => r.Contract.Price ?? 0),
            IndexedContracts = indexed.Length,
            KnownVolumeM3 = volumeByLabel.Values.Sum(),
            PeriodContracts = rowsInPeriod.Length,
            Outstanding = liveOutstanding == null
                ? all.Count(r => r.Contract.Status == "outstanding")
                : liveOutstanding.Where(r => r.CorporationId == corporation && r.Contract.Status == "outstanding")
                    .DistinctBy(r => r.Contract.Id).Count(),
            ExpiredOrCancelled = rowsInPeriod.Count(r => r.Contract.Status is "deleted" or "cancelled" or "rejected" or "failed"),
            AcceptedContracts = accepted,
            TimeBuckets = BuildBuckets(accepted, start, end),
            Suppliers = suppliers.Select(g => new BuybackRankRow { Label = g.Label, Isk = g.Isk, Contracts = g.Count,
                Width = maxSupplier > 0 ? (double)(g.Isk / maxSupplier) * 260 : 0 }).ToArray(),
            Contents = mixes.Select(g => new BuybackRankRow { Label = g.Key, CubicMetres = g.Value,
                Width = maxVolume > 0 ? g.Value / maxVolume * 260 : 0 }).ToArray()
        };
    }

    private static string Classify(string? name)
    {
        string value = (name ?? "").Trim();
        foreach (string ore in new[] { "Zeolites", "Sylvite", "Bitumens", "Coesite" })
            if (value.Contains(ore, StringComparison.OrdinalIgnoreCase)) return ore;
        return value.Length == 0 ? "Unidentified item" : value;
    }

    private static IReadOnlyList<BuybackBucket> BuildBuckets(IReadOnlyList<ContractRow> rows, DateTime start, DateTime end)
    {
        TimeSpan range = end - start;
        int step = range.TotalDays > 92 ? 30 : range.TotalDays > 35 ? 7 : 1;
        var groups = new List<BuybackBucket>();
        for (DateTime day = start; day < end; day = day.AddDays(step))
        {
            DateTime last = day.AddDays(step) < end ? day.AddDays(step) : end;
            var dayRows = rows.Where(r => r.Contract.Accepted!.Value.UtcDateTime >= day &&
                r.Contract.Accepted.Value.UtcDateTime < last).ToArray();
            groups.Add(new BuybackBucket { Date = day, Label = step == 1 ? day.ToString("dd") : day.ToString("dd MMM"),
                Count = dayRows.Length, Value = dayRows.Sum(r => r.Contract.Price ?? 0) });
        }
        decimal max = groups.Count == 0 ? 0 : groups.Max(g => g.Value);
        foreach (var bucket in groups) bucket.Height = max == 0 ? 0 : (double)(bucket.Value / max) * 155;
        return groups;
    }
}
