namespace EveCommandCenter.Models;

public sealed record PurchaseAllocation(Guid Id, string Purpose, Guid? ProjectId, string ProjectName, long Quantity, string Note);
public sealed record PurchaseAllocationSnapshot(long OwnerId, long TransactionId, long Revision, List<PurchaseAllocation> Splits);
public sealed record PurchaseAllocationAudit(DateTimeOffset TimeUtc, List<PurchaseAllocation> Before, List<PurchaseAllocation> After);
public sealed record AttributedPurchase(long OwnerId, MarketTransaction Purchase, PurchaseAllocation Allocation)
{
    public decimal Cost => checked(Purchase.UnitPrice * Allocation.Quantity);
}
