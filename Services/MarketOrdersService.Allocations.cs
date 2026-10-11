using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public sealed partial class MarketOrdersService
{
    public List<PurchaseAllocationSnapshot> PurchaseAllocations { get; private set; } = new();
    public void ReloadAllocations() => PurchaseAllocations = Ledger.Exists ? Ledger.Allocations() : new();
    public PurchaseAllocationSnapshot PurchaseAllocation(long owner, long transaction) => Ledger.ReadAllocation(owner, transaction);
    public List<PurchaseAllocationAudit> PurchaseHistory(long owner, long transaction) => Ledger.AllocationHistory(owner, transaction);
    public List<IndustryProject> AllocationProjects(int type) => Ledger.ReadWorkspace().Projects.Where(p => !p.Archived && p.Nodes.Any(n => n.TypeId == type)).ToList();
    public PurchaseAllocationSnapshot SavePurchaseAllocation(long owner, long transaction, long revision, IReadOnlyList<PurchaseAllocation> splits)
    {
        var saved = Ledger.SaveAllocations(owner, transaction, revision, splits); ReloadAllocations(); return saved;
    }
}
