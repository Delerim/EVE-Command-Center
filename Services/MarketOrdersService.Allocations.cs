using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public sealed partial class MarketOrdersService
{
    public List<PurchaseAllocationSnapshot> PurchaseAllocations { get; private set; } = new();
    public List<PurchaseAllocationSnapshot> SaleAllocations { get; private set; } = new();
    public void ReloadAllocations()
    { PurchaseAllocations = Ledger.Exists ? Ledger.Allocations() : new(); SaleAllocations = Ledger.Exists ? Ledger.Allocations(true) : new(); }
    public PurchaseAllocationSnapshot SaleAllocation(long owner, long transaction) => Ledger.ReadAllocation(owner, transaction, true);
    public List<PurchaseAllocationAudit> SaleHistory(long owner, long transaction) => Ledger.AllocationHistory(owner, transaction, true);
    public List<IndustryProject> SaleProjects(int type) => Ledger.ReadWorkspace().Projects.Where(p => !p.Archived && p.Nodes.Any(n => n.ParentId == null && n.TypeId == type)).ToList();
    public PurchaseAllocationSnapshot SaveSaleAllocation(long owner, long transaction, long revision, IReadOnlyList<PurchaseAllocation> splits, bool confirmedProjectOrigin)
    {
        var saved = Ledger.SaveAllocations(owner, transaction, revision, splits, true, confirmedProjectOrigin); ReloadAllocations(); return saved;
    }
    public PurchaseAllocationSnapshot PurchaseAllocation(long owner, long transaction) => Ledger.ReadAllocation(owner, transaction);
    public List<PurchaseAllocationAudit> PurchaseHistory(long owner, long transaction) => Ledger.AllocationHistory(owner, transaction);
    public List<IndustryProject> AllocationProjects(int type) => Ledger.ReadWorkspace().Projects.Where(p => !p.Archived && p.Nodes.Any(n => n.TypeId == type)).ToList();
    public PurchaseAllocationSnapshot SavePurchaseAllocation(long owner, long transaction, long revision, IReadOnlyList<PurchaseAllocation> splits)
    {
        var saved = Ledger.SaveAllocations(owner, transaction, revision, splits); ReloadAllocations(); return saved;
    }
}
