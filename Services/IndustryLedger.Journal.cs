using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
using Microsoft.Data.Sqlite;
namespace EveCommandCenter.Services;

internal sealed partial class IndustryLedger
{
    private void UpgradeJournal(SqliteConnection c)
    {
        lock (SchemaGate)
        {
            string backup = PathName + ".v2.bak";
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
                CREATE TABLE IF NOT EXISTS wallet_journal(owner_id INTEGER NOT NULL,entry_id INTEGER NOT NULL,date TEXT NOT NULL,observed TEXT NOT NULL,json TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(owner_id,entry_id));
                CREATE INDEX IF NOT EXISTS journal_date ON wallet_journal(date);
                CREATE TABLE IF NOT EXISTS journal_checkpoint(owner_id INTEGER PRIMARY KEY,observed TEXT NOT NULL,next_refresh TEXT NOT NULL);
                PRAGMA user_version=3;
                """);
            tx.Commit();
        }
    }
    internal DateTimeOffset NextJournal(long owner)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT next_refresh FROM journal_checkpoint WHERE owner_id=$p0", owner);
        return cmd.ExecuteScalar() is string value ? DateTimeOffset.Parse(value) : default;
    }
    internal List<MarketJournalRecord> Journal()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT owner_id,observed,json FROM wallet_journal ORDER BY date DESC,entry_id DESC");
        using var reader = cmd.ExecuteReader(); var result = new List<MarketJournalRecord>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), JsonSerializer.Deserialize<EveWalletJournalEntry>(reader.GetString(2))!));
        return result;
    }
    internal void ImportJournal(long owner, List<EveWalletJournalEntry> rows, DateTimeOffset observed, DateTimeOffset next)
    {
        if (owner <= 0 || observed == default || next < observed) throw new InvalidDataException("Invalid wallet checkpoint.");
        MarketOrdersService.ValidateJournal(rows);
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var row in rows)
        {
            string json = JsonSerializer.Serialize(row);
            using var existing = Command(c, "SELECT json FROM wallet_journal WHERE owner_id=$p0 AND entry_id=$p1", owner, row.Id);
            if (existing.ExecuteScalar() is string prior && !MarketOrdersService.SameJournal(JsonSerializer.Deserialize<EveWalletJournalEntry>(prior)!, row))
                throw new InvalidDataException("Conflicting wallet evidence; previous entries preserved.");
            Run(c, "INSERT INTO wallet_journal VALUES($p0,$p1,$p2,$p3,$p4,$p5) ON CONFLICT(owner_id,entry_id) DO UPDATE SET observed=excluded.observed", owner, row.Id, row.Date.ToUniversalTime().ToString("O"), observed.ToString("O"), json, $"ESI /characters/{owner}/wallet/journal; compatibility 2026-08-25");
        }
        Run(c, "INSERT INTO journal_checkpoint VALUES($p0,$p1,$p2) ON CONFLICT(owner_id) DO UPDATE SET observed=excluded.observed,next_refresh=excluded.next_refresh", owner, observed.ToString("O"), next.ToString("O"));
        tx.Commit();
    }
}
