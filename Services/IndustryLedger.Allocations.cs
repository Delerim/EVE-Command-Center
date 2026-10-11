using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
using Microsoft.Data.Sqlite;
namespace EveCommandCenter.Services;

internal sealed partial class IndustryLedger
{
    private void UpgradeAllocations(SqliteConnection c)
    {
        lock (SchemaGate)
        {
            string backup = PathName + ".v3.bak";
            if (!File.Exists(backup))
            {
                string temp = backup + "." + Guid.NewGuid().ToString("N");
                try
                {
                    using (var destination = Connect(temp)) c.BackupDatabase(destination);
                    try { File.Move(temp, backup); } catch (IOException) when (File.Exists(backup)) { }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            using var tx = c.BeginTransaction();
            Run(c, """
                CREATE TABLE IF NOT EXISTS purchase_allocations(owner_id INTEGER NOT NULL,transaction_id INTEGER NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(owner_id,transaction_id),FOREIGN KEY(owner_id,transaction_id) REFERENCES wallet_transactions(owner_id,transaction_id));
                CREATE TABLE IF NOT EXISTS allocation_audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT,owner_id INTEGER NOT NULL,transaction_id INTEGER NOT NULL,time TEXT NOT NULL,before_json TEXT NOT NULL,after_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS allocation_audit_purchase ON allocation_audit(owner_id,transaction_id);
                PRAGMA user_version=4;
                """);
            tx.Commit();
        }
    }
    private static PurchaseAllocationSnapshot ReadAllocation(SqliteConnection c, long owner, long transaction)
    {
        using var cmd = Command(c, "SELECT revision,json FROM purchase_allocations WHERE owner_id=$p0 AND transaction_id=$p1", owner, transaction);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(owner, transaction, r.GetInt64(0), JsonSerializer.Deserialize<List<PurchaseAllocation>>(r.GetString(1))!) : new(owner, transaction, 0, new());
    }
    internal PurchaseAllocationSnapshot ReadAllocation(long owner, long transaction)
    { using var c = Open(); return ReadAllocation(c, owner, transaction); }
    internal List<PurchaseAllocationSnapshot> Allocations()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT owner_id,transaction_id,revision,json FROM purchase_allocations");
        using var r = cmd.ExecuteReader(); var result = new List<PurchaseAllocationSnapshot>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), JsonSerializer.Deserialize<List<PurchaseAllocation>>(r.GetString(3))!));
        return result;
    }
    internal List<PurchaseAllocationAudit> AllocationHistory(long owner, long transaction)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT time,before_json,after_json FROM allocation_audit WHERE owner_id=$p0 AND transaction_id=$p1 ORDER BY sequence DESC", owner, transaction);
        using var r = cmd.ExecuteReader(); var result = new List<PurchaseAllocationAudit>();
        while (r.Read()) result.Add(new(DateTimeOffset.Parse(r.GetString(0)), JsonSerializer.Deserialize<List<PurchaseAllocation>>(r.GetString(1))!, JsonSerializer.Deserialize<List<PurchaseAllocation>>(r.GetString(2))!));
        return result;
    }
    internal PurchaseAllocationSnapshot SaveAllocations(long owner, long transaction, long expectedRevision, IReadOnlyList<PurchaseAllocation> requested)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        var old = ReadAllocation(c, owner, transaction);
        if (old.Revision != expectedRevision) throw new InvalidOperationException("This purchase changed elsewhere. Reload its allocations before editing.");
        using var source = Command(c, "SELECT json FROM wallet_transactions WHERE owner_id=$p0 AND transaction_id=$p1", owner, transaction);
        var purchase = source.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MarketTransaction>(json) : null;
        if (purchase is not { Personal: true, Buy: true }) throw new InvalidOperationException("Choose an imported personal purchase first.");
        using var workspace = Command(c, "SELECT json FROM workspace WHERE id=1");
        var state = JsonSerializer.Deserialize<IndustryWorkspace>((string)workspace.ExecuteScalar()!)!;
        if (requested.Any(r => r.Id == Guid.Empty || r.Quantity <= 0 || r.Purpose is not ("Project" or "Stock" or "Unrelated")) || requested.Select(r => r.Id).Distinct().Count() != requested.Count)
            throw new InvalidOperationException("Every split needs a destination and a positive whole quantity.");
        long total = 0; var splits = new List<PurchaseAllocation>();
        foreach (var row in requested)
        {
            if (row.Quantity > purchase.Quantity - total) throw new InvalidOperationException($"Only {purchase.Quantity:N0} units were purchased. Splits cannot exceed that quantity.");
            total += row.Quantity; _ = checked(row.Quantity * purchase.UnitPrice);
            if (row.Note == null || row.Note.Length > 2000) throw new InvalidOperationException("Keep the note within 2,000 characters.");
            if (row.Purpose == "Project")
            {
                var project = state.Projects.FirstOrDefault(p => p.Id == row.ProjectId && !p.Archived && p.Nodes.Any(n => n.TypeId == purchase.TypeId));
                if (project == null)
                {
                    // Retain unchanged historical assignments when their project is archived, removed or replanned.
                    if (!old.Splits.Contains(row)) throw new InvalidOperationException("Choose an active project containing this item. Existing historical splits can be kept or removed.");
                    splits.Add(row);
                }
                else splits.Add(row with { ProjectName = project.Name, Note = row.Note.Trim() });
            }
            else
            {
                if (row.ProjectId != null) throw new InvalidOperationException("General stock and unrelated activity cannot reference a project.");
                splits.Add(row with { ProjectName = row.Purpose == "Stock" ? "General stock" : "Unrelated activity", Note = row.Note.Trim() });
            }
        }
        if (total > purchase.Quantity) throw new InvalidOperationException($"Only {purchase.Quantity:N0} units were purchased. Splits cannot exceed that quantity.");
        splits = splits.OrderBy(r => r.Id).ToList();
        string before = JsonSerializer.Serialize(old.Splits.OrderBy(r => r.Id)), after = JsonSerializer.Serialize(splits);
        if (before == after) { tx.Commit(); return old; }
        long revision = checked(expectedRevision + 1);
        Run(c, "INSERT INTO purchase_allocations VALUES($p0,$p1,$p2,$p3) ON CONFLICT(owner_id,transaction_id) DO UPDATE SET revision=excluded.revision,json=excluded.json", owner, transaction, revision, after);
        Run(c, "INSERT INTO allocation_audit(owner_id,transaction_id,time,before_json,after_json) VALUES($p0,$p1,$p2,$p3,$p4)", owner, transaction, DateTimeOffset.UtcNow.ToString("O"), before, after);
        tx.Commit(); return new(owner, transaction, revision, splits);
    }
    internal List<AttributedPurchase> ProjectPurchases(Guid project)
    {
        // Keep the source transactions and allocations in one SQLite read snapshot.
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        using var cmd = Command(c, "SELECT a.owner_id,a.json,t.json FROM purchase_allocations a JOIN wallet_transactions t ON t.owner_id=a.owner_id AND t.transaction_id=a.transaction_id");
        using var r = cmd.ExecuteReader(); var result = new List<AttributedPurchase>();
        while (r.Read())
        {
            var purchase = JsonSerializer.Deserialize<MarketTransaction>(r.GetString(2))!;
            foreach (var split in JsonSerializer.Deserialize<List<PurchaseAllocation>>(r.GetString(1))!.Where(a => a.Purpose == "Project" && a.ProjectId == project)) result.Add(new(r.GetInt64(0), purchase, split));
        }
        return result;
    }
}
