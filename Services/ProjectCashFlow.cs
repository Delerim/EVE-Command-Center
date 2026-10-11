using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

// Read-only cash evidence. This deliberately has no profit or inventory valuation field.
public static class ProjectCashFlow
{
    public sealed record Entry(long OwnerId, MarketTransaction Trade, PurchaseAllocation Split)
    {
        public decimal Amount => checked(Trade.UnitPrice * Split.Quantity);
        public decimal Paid => Trade.Buy ? Amount : 0;
        public decimal Received => Trade.Buy ? 0 : Amount;
    }
    public sealed record Summary(IReadOnlyList<Entry> Entries, decimal Paid, decimal Received, int PurchaseCount, int SaleCount)
    {
        public decimal Difference => checked(Received - Paid);
        public bool HasEvidence => Entries.Count > 0;
    }
    public static Summary Calculate(IEnumerable<AttributedPurchase> purchases, IEnumerable<AttributedSale> sales,
        DateTimeOffset since, DateTimeOffset until, long? owner = null)
        => Calculate(purchases.Select(p => new Entry(p.OwnerId, p.Purchase, p.Allocation))
            .Concat(sales.Select(s => new Entry(s.OwnerId, s.Sale, s.Allocation))), since, until, owner);
    public static Summary Calculate(IEnumerable<Entry> entries, DateTimeOffset since, DateTimeOffset until, long? owner = null)
    {
        if (until < since) throw new ArgumentException("The end of the period must follow its start.");
        var rows = entries
            .Where(e => e.Trade.Date >= since && e.Trade.Date <= until && (!owner.HasValue || e.OwnerId == owner))
            .OrderByDescending(e => e.Trade.Date).ThenBy(e => e.OwnerId).ThenBy(e => e.Trade.Id).ThenBy(e => e.Split.Id).ToArray();
        if (rows.Any(e => e.Split.Quantity <= 0 || e.Trade.UnitPrice < 0)) throw new InvalidOperationException("Invalid allocated cash evidence.");
        return new(rows, rows.Sum(e => e.Paid), rows.Sum(e => e.Received),
            rows.Where(e => e.Trade.Buy).Select(e => (e.OwnerId, e.Trade.Id)).Distinct().Count(),
            rows.Where(e => !e.Trade.Buy).Select(e => (e.OwnerId, e.Trade.Id)).Distinct().Count());
    }
}
