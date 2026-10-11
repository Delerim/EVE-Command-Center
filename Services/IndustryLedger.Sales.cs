using System.IO;
using Microsoft.Data.Sqlite;
namespace EveCommandCenter.Services;
internal sealed partial class IndustryLedger
{
    private void UpgradeSales(SqliteConnection c)
    {
        lock (SchemaGate)
        {
            string backup = PathName + ".v4.bak";
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
                CREATE TABLE IF NOT EXISTS sale_allocations(owner_id INTEGER NOT NULL,transaction_id INTEGER NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(owner_id,transaction_id),FOREIGN KEY(owner_id,transaction_id) REFERENCES wallet_transactions(owner_id,transaction_id));
                CREATE TABLE IF NOT EXISTS sale_allocation_audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT,owner_id INTEGER NOT NULL,transaction_id INTEGER NOT NULL,time TEXT NOT NULL,before_json TEXT NOT NULL,after_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS sale_audit_trade ON sale_allocation_audit(owner_id,transaction_id);
                PRAGMA user_version=5;
                """);
            tx.Commit();
        }
    }
}
