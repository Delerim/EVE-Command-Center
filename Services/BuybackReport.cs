using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class BuybackBucket
{
    public DateTime Date { get; init; }
    public int Count { get; init; }
    public decimal Value { get; init; }
    public double Height { get; set; }
    public string Label { get; init; } = "";
    public string Tooltip => $"{Date:dd MMM yyyy} UTC | {Count} contracts | {Value:N2} ISK";
}

public static class BuybackReport
{
    public static DateTime Start(DateTime anchor, string period) => period switch
    {
        "Week" => anchor.Date.AddDays(-((int)anchor.DayOfWeek + 6) % 7),
        "Year" => new DateTime(anchor.Year, 1, 1),
        _ => new DateTime(anchor.Year, anchor.Month, 1)
    };
    public static DateTime End(DateTime start, string period) => period == "Week" ? start.AddDays(7) : period == "Year" ? start.AddYears(1) : start.AddMonths(1);
    public static IReadOnlyList<BuybackBucket> Build(IEnumerable<ContractRow> history, long corporation, DateTime anchor, string period)
    {
        var start = Start(anchor, period);
        var end = End(start, period);
        var rows = history.Where(r => r.CorporationId == corporation && r.Contract.AssigneeId == corporation &&
            r.Contract.Type == "item_exchange" && r.JaniceUrl != null && r.Contract.Price > 0 &&
            r.Contract.Status is "finished" or "finished_issuer" or "finished_contractor" &&
            r.Contract.Accepted is { } accepted && accepted.UtcDateTime >= start && accepted.UtcDateTime < end).ToArray();
        var result = new List<BuybackBucket>();
        for (var date = start; date < end; date = period == "Year" ? date.AddMonths(1) : date.AddDays(1))
        {
            var next = period == "Year" ? date.AddMonths(1) : date.AddDays(1);
            var matches = rows.Where(r => r.Contract.Accepted!.Value.UtcDateTime >= date && r.Contract.Accepted.Value.UtcDateTime < next).ToArray();
            result.Add(new() { Date = date, Label = date.ToString(period == "Year" ? "MMM" : "dd"), Count = matches.Length, Value = matches.Sum(r => r.Contract.Price ?? 0) });
        }
        decimal max = result.Max(r => r.Value);
        foreach (var bucket in result) bucket.Height = max > 0 ? (double)(bucket.Value / max) * 190 : 0;
        return result;
    }
}
