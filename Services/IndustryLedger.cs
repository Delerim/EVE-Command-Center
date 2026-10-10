using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EveCommandCenter.Models;
using Microsoft.Data.Sqlite;

namespace EveCommandCenter.Services;

// One durable store for planning and imported history. The old JSON is an immutable migration backup.
internal sealed partial class IndustryLedger(string directory)
{
    internal string PathName => Path.Combine(directory, "industry-ledger.db");
    private string Legacy => Path.Combine(directory, "industry-workspace.json");
    internal bool Exists => File.Exists(PathName);
    private static string Fingerprint(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    private static SqliteConnection Connect(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        c.Open(); return c;
    }
    private static SqliteCommand Command(SqliteConnection c, string sql, params object[] values)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i]);
        return cmd;
    }
    private static void Run(SqliteConnection c, string sql, params object[] values) { using var cmd = Command(c, sql, values); cmd.ExecuteNonQuery(); }
    private SqliteConnection Open()
    {
        if (!Exists) throw new InvalidOperationException("Industry history has not been initialized.");
        var c = Connect(PathName);
        try
        {
            using var version = Command(c, "PRAGMA user_version");
            int schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema is not (1 or 2)) throw new InvalidDataException("Unsupported industry ledger version; data preserved.");
            using var hash = Command(c, "SELECT legacy_hash FROM workspace WHERE id=1");
            if ((string?)hash.ExecuteScalar() != Fingerprint(Legacy)) throw new InvalidDataException("The preserved planning file changed after migration. Reopen with a compatible version; data preserved.");
            if (schema == 1) UpgradeTransactions(c);
            return c;
        }
        catch { c.Dispose(); throw; }
    }
    internal void Ensure()
    {
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Legacy + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Exists) { using var existing = Open(); return; }
        var store = new IndustryWorkspaceStore(directory);
        if (store.Error.Length > 0) throw new InvalidDataException(store.Error);
        var state = store.Snapshot();
        if (File.Exists(Legacy))
        {
            var backup = Legacy + ".pre-ledger.bak";
            if (!File.Exists(backup)) File.Copy(Legacy, backup);
            else if (Fingerprint(backup) != Fingerprint(Legacy)) throw new InvalidDataException("A different migration backup already exists; data preserved.");
        }
        var temp = PathName + ".migration-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var c = Connect(temp))
            {
                using var tx = c.BeginTransaction();
                Run(c, """
                    CREATE TABLE workspace(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,json TEXT NOT NULL,legacy_hash TEXT NOT NULL);
                    CREATE TABLE components(id TEXT PRIMARY KEY,project_id TEXT NOT NULL,type_id INTEGER NOT NULL,json TEXT NOT NULL);
                    CREATE INDEX components_project ON components(project_id);
                    CREATE TABLE reservations(id TEXT PRIMARY KEY,project_id TEXT NOT NULL,owner_id INTEGER NOT NULL,item_id INTEGER NOT NULL,json TEXT NOT NULL);
                    CREATE INDEX reservations_source ON reservations(owner_id,item_id);
                    CREATE TABLE planning_events(id TEXT PRIMARY KEY,project_id TEXT,json TEXT NOT NULL);
                    CREATE TABLE order_history(owner_id INTEGER NOT NULL,order_id INTEGER NOT NULL,type_id INTEGER NOT NULL,state TEXT NOT NULL,observed TEXT NOT NULL,json TEXT NOT NULL,source TEXT NOT NULL,PRIMARY KEY(owner_id,order_id));
                    CREATE INDEX history_type ON order_history(type_id);
                    CREATE TABLE history_checkpoint(owner_id INTEGER PRIMARY KEY,observed TEXT NOT NULL,next_refresh TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """);
                Run(c, "INSERT INTO workspace VALUES(1,$p0,$p1,$p2)", state.Revision, JsonSerializer.Serialize(state), Fingerprint(Legacy));
                Index(c, state); tx.Commit();
            }
            File.Move(temp, PathName);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Index(SqliteConnection c, IndustryWorkspace state)
    {
        Run(c, "DELETE FROM components; DELETE FROM reservations; DELETE FROM planning_events;");
        foreach (var p in state.Projects) foreach (var n in p.Nodes)
            Run(c, "INSERT INTO components VALUES($p0,$p1,$p2,$p3)", n.Id.ToString(), p.Id.ToString(), n.TypeId, JsonSerializer.Serialize(n));
        foreach (var r in state.Reservations)
            Run(c, "INSERT INTO reservations VALUES($p0,$p1,$p2,$p3,$p4)", r.Id.ToString(), r.ProjectId.ToString(), r.OwnerId, r.ItemId, JsonSerializer.Serialize(r));
        foreach (var e in state.Events)
            Run(c, "INSERT INTO planning_events VALUES($p0,$p1,$p2)", e.Id.ToString(), (object?)e.ProjectId?.ToString() ?? DBNull.Value, JsonSerializer.Serialize(e));
    }
    internal IndustryWorkspace ReadWorkspace()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT json FROM workspace WHERE id=1");
        return JsonSerializer.Deserialize<IndustryWorkspace>((string)cmd.ExecuteScalar()!) ?? throw new InvalidDataException("Empty planning ledger.");
    }
    internal void SaveWorkspace(IndustryWorkspace state, long expectedRevision)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, "UPDATE workspace SET revision=$p0,json=$p1 WHERE id=1 AND revision=$p2", state.Revision, JsonSerializer.Serialize(state), expectedRevision);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Workspace changed in another instance. Reload before editing.");
        Index(c, state); tx.Commit();
    }
    internal DateTimeOffset NextHistory(long owner)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT next_refresh FROM history_checkpoint WHERE owner_id=$p0", owner);
        return cmd.ExecuteScalar() is string value ? DateTimeOffset.Parse(value) : default;
    }
    internal List<HistoricalOrderRecord> History()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT owner_id,observed,json FROM order_history ORDER BY observed DESC,order_id DESC");
        using var r = cmd.ExecuteReader(); var result = new List<HistoricalOrderRecord>();
        while (r.Read()) result.Add(new(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), JsonSerializer.Deserialize<HistoricalMarketOrder>(r.GetString(2))!));
        return result;
    }
    internal void ImportHistory(long owner, List<HistoricalMarketOrder> orders, DateTimeOffset observed, DateTimeOffset nextRefresh)
    {
        if (owner <= 0 || observed == default || nextRefresh < observed) throw new InvalidDataException("Invalid history checkpoint.");
        MarketOrdersService.Validate(orders.Cast<MarketOrder>().ToList());
        if (orders.Any(o => o.State is not ("cancelled" or "expired"))) throw new InvalidDataException("Unsupported order state.");
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var o in orders.Where(o => !o.Corporation))
            Run(c, "INSERT INTO order_history VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6) ON CONFLICT(owner_id,order_id) DO UPDATE SET type_id=excluded.type_id,state=excluded.state,observed=excluded.observed,json=excluded.json", owner, o.OrderId, o.TypeId, o.State, observed.ToString("O"), JsonSerializer.Serialize(o), $"ESI /characters/{owner}/orders/history; compatibility 2026-08-25");
        Run(c, "INSERT INTO history_checkpoint VALUES($p0,$p1,$p2) ON CONFLICT(owner_id) DO UPDATE SET observed=excluded.observed,next_refresh=excluded.next_refresh", owner, observed.ToString("O"), nextRefresh.ToString("O"));
        tx.Commit();
    }
}
