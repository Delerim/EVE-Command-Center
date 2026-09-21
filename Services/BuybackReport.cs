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

    public static DateTime End(DateTime start, string period) =>
        period == "Week"
            ? start.AddDays(7)
            : period == "Year"
                ? start.AddYears(1)
                : start.AddMonths(1);

    public static bool IsQualifyingAcceptedBuyback(
        ContractRow row,
        long corporation)
    {
        return
            row.CorporationId == corporation &&
            row.Contract.AssigneeId == corporation &&
            row.Contract.Type == "item_exchange" &&
            row.JaniceUrl != null &&
            row.Contract.Price > 0 &&
            row.Contract.Status is
                "finished" or
                "finished_issuer" or
                "finished_contractor" &&
            row.Contract.Accepted.HasValue;
    }

    public static IReadOnlyList<ContractRow> Qualifying(
        IEnumerable<ContractRow> history,
        long corporation,
        DateTime start,
        DateTime end)
    {
        return history
            .Where(row =>
                IsQualifyingAcceptedBuyback(
                    row,
                    corporation))
            .Where(row =>
            {
                DateTime accepted =
                    row.Contract.Accepted!.Value.UtcDateTime;

                return accepted >= start &&
                       accepted < end;
            })
            .ToArray();
    }

    public static IReadOnlyList<BuybackBucket> Build(
        IEnumerable<ContractRow> history,
        long corporation,
        DateTime anchor,
        string period)
    {
        DateTime start =
            Start(anchor, period);

        DateTime end =
            End(start, period);

        IReadOnlyList<ContractRow> rows =
            Qualifying(
                history,
                corporation,
                start,
                end);

        var result =
            new List<BuybackBucket>();

        for (DateTime date = start;
             date < end;
             date =
                 period == "Year"
                     ? date.AddMonths(1)
                     : date.AddDays(1))
        {
            DateTime next =
                period == "Year"
                    ? date.AddMonths(1)
                    : date.AddDays(1);

            ContractRow[] matches =
                rows
                    .Where(row =>
                        row.Contract.Accepted!.Value.UtcDateTime >= date &&
                        row.Contract.Accepted.Value.UtcDateTime < next)
                    .ToArray();

            result.Add(
                new BuybackBucket
                {
                    Date = date,
                    Label =
                        date.ToString(
                            period == "Year"
                                ? "MMM"
                                : "dd"),
                    Count = matches.Length,
                    Value =
                        matches.Sum(
                            row =>
                                row.Contract.Price ??
                                0)
                });
        }

        decimal max =
            result.Count == 0
                ? 0
                : result.Max(row => row.Value);

        foreach (BuybackBucket bucket in result)
            bucket.Height =
                max > 0
                    ? (double)(bucket.Value / max) * 190
                    : 0;

        return result;
    }
}