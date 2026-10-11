namespace EveCommandCenter.Models;
public sealed record ExpenseAllocation(Guid Id, Guid ProjectId, string ProjectName, decimal Amount, string Note);
public sealed record ExpenseAllocationSnapshot(long OwnerId, long EntryId, long Revision, List<ExpenseAllocation> Splits);
public sealed record ExpenseAllocationAudit(DateTimeOffset TimeUtc, List<ExpenseAllocation> Before, List<ExpenseAllocation> After);
public sealed record AttributedExpense(long OwnerId, EveWalletJournalEntry Entry, ExpenseAllocation Allocation);
