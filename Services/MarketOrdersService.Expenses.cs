using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed partial class MarketOrdersService
{
    public ExpenseAllocationSnapshot ExpenseAllocation(long owner, long entry) => Ledger.ReadExpense(owner, entry);
    public List<ExpenseAllocationAudit> ExpenseHistory(long owner, long entry) => Ledger.ExpenseHistory(owner, entry);
    public List<IndustryProject> ExpenseProjects() => Ledger.ReadWorkspace().Projects.Where(p => !p.Archived).ToList();
    public ExpenseAllocationSnapshot SaveExpenseAllocation(long owner, long entry, long revision, IReadOnlyList<ExpenseAllocation> splits)
    { var result = Ledger.SaveExpenses(owner, entry, revision, splits); DataVersion++; return result; }
}
