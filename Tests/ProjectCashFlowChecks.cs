using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckProjectCashFlow()
    {
        var now = DateTimeOffset.UtcNow;
        var project = Guid.NewGuid();
        PurchaseAllocation Split(long quantity) => new(Guid.NewGuid(), "Project", project, "Build", quantity, "");
        MarketTransaction Trade(long id, bool buy, decimal price, DateTimeOffset date) => new() { Id = id, Buy = buy, UnitPrice = price, Date = date, Personal = true, TypeId = 34, Quantity = 100 };
        var purchase = Trade(1, true, 1.125m, now.AddDays(-2));
        var purchases = new[] { new AttributedPurchase(42, purchase, Split(2)), new AttributedPurchase(42, purchase, Split(3)), new AttributedPurchase(43, Trade(1, true, 2m, now), Split(4)) };
        var sales = new[] { new AttributedSale(43, Trade(2, false, 10.333m, now), Split(2)) };
        var all = ProjectCashFlow.Calculate(purchases, sales, DateTimeOffset.MinValue, now);
        Check(all.Paid == 13.625m && all.Received == 20.666m && all.Difference == 7.041m, "Project cash flow preserves decimal prices and separates spending, receipts and difference");
        Check(all.PurchaseCount == 2 && all.SaleCount == 1 && all.Entries.Count == 4, "Multiple splits retain their provenance without inflating owner-scoped trade counts");
        var buyer = ProjectCashFlow.Calculate(purchases, sales, DateTimeOffset.MinValue, now, 42);
        Check(buyer.Paid == 5.625m && buyer.Received == 0 && buyer.Difference == -5.625m, "Project finance pilot filter applies to both receipts and spending");
        var today = ProjectCashFlow.Calculate(purchases, sales, now, now);
        Check(today.Paid == 8 && today.Received == 20.666m && today.Entries.Count == 2, "Finance period bounds include exact boundary records and exclude earlier spending");
        var empty = ProjectCashFlow.Calculate(purchases, sales, now.AddSeconds(1), now.AddDays(1));
        Check(!empty.HasEvidence && empty.Entries.Count == 0, "No financial evidence is distinct from an observed zero-priced trade");
        var free = ProjectCashFlow.Calculate(new[] { new AttributedPurchase(42, Trade(9, true, 0, now), Split(1)) }, Array.Empty<AttributedSale>(), now, now);
        Check(free.HasEvidence && free.Paid == 0 && free.PurchaseCount == 1, "Confirmed free purchases remain evidence rather than unknown historical valuation");
        bool failed = false;
        try { ProjectCashFlow.Calculate(new[] { new AttributedPurchase(42, Trade(9, true, decimal.MaxValue, now), Split(2)) }, Array.Empty<AttributedSale>(), now, now); } catch (OverflowException) { failed = true; }
        Check(failed, "Financial overflow fails explicitly instead of wrapping a project total");
        failed = false; try { ProjectCashFlow.Calculate(purchases, sales, now, now.AddSeconds(-1)); } catch (ArgumentException) { failed = true; }
        Check(failed, "Invalid finance periods are rejected");
        var reversed = ProjectCashFlow.Calculate(purchases.Take(1), sales, DateTimeOffset.MinValue, now);
        Check(reversed.Paid == 2.25m && all.Paid == 13.625m, "Removing an allocation changes the next view without mutating the prior cash summary");
    }
}
