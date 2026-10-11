using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

// A view over saved purchase allocations, not an inventory valuation or a second ledger.
public static class ProjectAcquisitionCosts
{
    public sealed record ItemCost(int TypeId, string Name, string Icon, long Quantity, decimal Cost,
        int Purchases, decimal LowestPrice, decimal HighestPrice, DateTimeOffset LastBought)
    {
        public decimal AveragePrice => Cost / Quantity;
    }
    public static IReadOnlyList<ItemCost> Calculate(IEnumerable<AttributedPurchase> purchases) => purchases
        .GroupBy(p => p.Purchase.TypeId)
        .Select(group =>
        {
            var latest = group.OrderByDescending(p => p.Purchase.Date).First().Purchase;
            long quantity = group.Aggregate(0L, (sum, p) => checked(sum + p.Allocation.Quantity));
            if (quantity <= 0) throw new InvalidOperationException("Purchase quantities must be positive.");
            return new ItemCost(group.Key, latest.Name, latest.Icon, quantity, group.Sum(p => p.Cost),
                group.Select(p => (p.OwnerId, p.Purchase.Id)).Distinct().Count(),
                group.Min(p => p.Purchase.UnitPrice), group.Max(p => p.Purchase.UnitPrice), latest.Date);
        }).OrderByDescending(p => p.Cost).ThenBy(p => p.Name).ToArray();
}
