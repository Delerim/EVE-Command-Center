using System.Text.Json;
using EveCommandCenter.Models;
using Microsoft.Data.Sqlite;
namespace EveCommandCenter.Services;
internal sealed partial class IndustryLedger
{
    private static ExpenseAllocationSnapshot ReadExpense(SqliteConnection c, long owner, long entry)
    {
        using var cmd = Command(c, "SELECT revision,json FROM expense_allocations WHERE owner_id=$p0 AND entry_id=$p1", owner, entry);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(owner, entry, r.GetInt64(0), JsonSerializer.Deserialize<List<ExpenseAllocation>>(r.GetString(1))!) : new(owner, entry, 0, new());
    }
    internal ExpenseAllocationSnapshot ReadExpense(long owner, long entry) { using var c = Open(); return ReadExpense(c, owner, entry); }
    internal List<ExpenseAllocationAudit> ExpenseHistory(long owner, long entry)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT time,before_json,after_json FROM expense_allocation_audit WHERE owner_id=$p0 AND entry_id=$p1 ORDER BY sequence DESC", owner, entry);
        using var r = cmd.ExecuteReader(); var result = new List<ExpenseAllocationAudit>();
        while (r.Read()) result.Add(new(DateTimeOffset.Parse(r.GetString(0)), JsonSerializer.Deserialize<List<ExpenseAllocation>>(r.GetString(1))!, JsonSerializer.Deserialize<List<ExpenseAllocation>>(r.GetString(2))!));
        return result;
    }
    internal ExpenseAllocationSnapshot SaveExpenses(long owner, long entry, long expectedRevision, IReadOnlyList<ExpenseAllocation> requested)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); var old = ReadExpense(c, owner, entry);
        if (old.Revision != expectedRevision) throw new InvalidOperationException("This charge changed elsewhere. Reload its assignments before editing.");
        using var source = Command(c, "SELECT json FROM wallet_journal WHERE owner_id=$p0 AND entry_id=$p1", owner, entry);
        var evidence = source.ExecuteScalar() is string json ? JsonSerializer.Deserialize<EveWalletJournalEntry>(json) : null;
        if (evidence == null) throw new InvalidOperationException("Choose an imported wallet charge first.");
        decimal charge = ExpenseEvidence.Charge(evidence);
        using var workspace = Command(c, "SELECT json FROM workspace WHERE id=1");
        var state = JsonSerializer.Deserialize<IndustryWorkspace>((string)workspace.ExecuteScalar()!)!;
        if (requested.Any(r => r.Id == Guid.Empty || r.ProjectId == Guid.Empty || r.Amount <= 0 || r.Note == null || r.Note.Length > 2000) || requested.Select(r => r.Id).Distinct().Count() != requested.Count)
            throw new InvalidOperationException("Each split needs a project, a positive ISK amount and a note within 2,000 characters.");
        decimal total = 0; var splits = new List<ExpenseAllocation>();
        foreach (var row in requested)
        {
            if (row.Amount > charge - total) throw new InvalidOperationException($"Assignments cannot exceed the confirmed charge of {charge:N2} ISK.");
            total += row.Amount;
            var project = state.Projects.FirstOrDefault(p => p.Id == row.ProjectId && !p.Archived);
            if (project == null)
            {
                if (!old.Splits.Contains(row)) throw new InvalidOperationException("Choose an active project. Historical assignments can be kept or removed.");
                splits.Add(row);
            }
            else splits.Add(row with { ProjectName = project.Name, Note = row.Note.Trim() });
        }
        splits = splits.OrderBy(r => r.Id).ToList();
        string before = JsonSerializer.Serialize(old.Splits.OrderBy(r => r.Id)), after = JsonSerializer.Serialize(splits);
        if (before == after) { tx.Commit(); return old; }
        long revision = checked(expectedRevision + 1);
        Run(c, "INSERT INTO expense_allocations VALUES($p0,$p1,$p2,$p3) ON CONFLICT(owner_id,entry_id) DO UPDATE SET revision=excluded.revision,json=excluded.json", owner, entry, revision, after);
        Run(c, "INSERT INTO expense_allocation_audit(owner_id,entry_id,time,before_json,after_json) VALUES($p0,$p1,$p2,$p3,$p4)", owner, entry, DateTimeOffset.UtcNow.ToString("O"), before, after);
        tx.Commit(); return new(owner, entry, revision, splits);
    }
    internal List<AttributedExpense> ProjectExpenses(Guid project)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        return ReadProjectExpenses(c, project);
    }
    private static List<AttributedExpense> ReadProjectExpenses(SqliteConnection c, Guid project)
    {
        using var cmd = Command(c, "SELECT a.owner_id,a.json,j.json FROM expense_allocations a JOIN wallet_journal j ON j.owner_id=a.owner_id AND j.entry_id=a.entry_id");
        using var r = cmd.ExecuteReader(); var result = new List<AttributedExpense>();
        while (r.Read())
        {
            var entry = JsonSerializer.Deserialize<EveWalletJournalEntry>(r.GetString(2))!;
            foreach (var split in JsonSerializer.Deserialize<List<ExpenseAllocation>>(r.GetString(1))!.Where(a => a.ProjectId == project)) result.Add(new(r.GetInt64(0), entry, split));
        }
        return result;
    }
}
