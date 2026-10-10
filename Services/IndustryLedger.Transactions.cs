using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
using Microsoft.Data.Sqlite;
namespace EveCommandCenter.Services;

internal sealed partial class IndustryLedger
{
    private static readonly object SchemaGate = new();
    private void UpgradeTransactions(SqliteConnection c)
    {
        lock (SchemaGate)
        {
            string backup = PathName + ".v1.bak";
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
                CREATE TABLE IF NOT EXISTS wallet_transactions(owner_id INTEGER NOT NULL,transaction_id INTEGER NOT NULL,type_id INTEGER NOT NULL,date TEXT NOT NULL,observed TEXT NOT NULL,json TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(owner_id,transaction_id));
                CREATE INDEX IF NOT EXISTS transactions_date ON wallet_transactions(date);
                CREATE INDEX IF NOT EXISTS transactions_type ON wallet_transactions(type_id);
                CREATE TABLE IF NOT EXISTS transaction_checkpoint(owner_id INTEGER PRIMARY KEY,observed TEXT NOT NULL,next_refresh TEXT NOT NULL);
                PRAGMA user_version=2;
                """);
            tx.Commit();
        }
    }
    internal DateTimeOffset NextTransactions(long owner)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT next_refresh FROM transaction_checkpoint WHERE owner_id=$p0", owner);
        return cmd.ExecuteScalar() is string value ? DateTimeOffset.Parse(value) : default;
    }
    internal List<MarketTransactionRecord> Transactions()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT owner_id,observed,json FROM wallet_transactions ORDER BY date DESC,transaction_id DESC");
        using var r = cmd.ExecuteReader(); var result = new List<MarketTransactionRecord>();
        while (r.Read()) result.Add(new(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), JsonSerializer.Deserialize<MarketTransaction>(r.GetString(2))!));
        return result;
    }
    internal void ImportTransactions(long owner, List<MarketTransaction> rows, DateTimeOffset observed, DateTimeOffset next)
    {
        if (owner <= 0 || observed == default || next < observed) throw new InvalidDataException("Invalid transaction checkpoint.");
        MarketOrdersService.ValidateTransactions(rows);
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var row in rows.Where(r => r.Personal))
        {
            using var existing = Command(c, "SELECT json FROM wallet_transactions WHERE owner_id=$p0 AND transaction_id=$p1", owner, row.Id);
            if (existing.ExecuteScalar() is string json)
            {
                var old = JsonSerializer.Deserialize<MarketTransaction>(json)!;
                if (!MarketOrdersService.SameTransaction(old, row))
                    throw new InvalidDataException("Conflicting transaction evidence; prior record preserved.");
            }
            Run(c, "INSERT INTO wallet_transactions VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6) ON CONFLICT(owner_id,transaction_id) DO UPDATE SET observed=excluded.observed,json=excluded.json", owner, row.Id, row.TypeId, row.Date.ToUniversalTime().ToString("O"), observed.ToString("O"), JsonSerializer.Serialize(row), $"ESI /characters/{owner}/wallet/transactions; compatibility 2026-08-25");
        }
        Run(c, "INSERT INTO transaction_checkpoint VALUES($p0,$p1,$p2) ON CONFLICT(owner_id) DO UPDATE SET observed=excluded.observed,next_refresh=excluded.next_refresh", owner, observed.ToString("O"), next.ToString("O"));
        tx.Commit();
    }
}
