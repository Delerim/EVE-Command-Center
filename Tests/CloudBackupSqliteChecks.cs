using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using EveCommandCenter.Services;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static void CheckCloudBackupLiveSqlite()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "ecc-backup-sqlite-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string livePath = Path.Combine(folder, "running.sqlite");
            using (var live = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = livePath,
                       Mode = SqliteOpenMode.ReadWriteCreate,
                       Pooling = false
                   }.ToString()))
            {
                live.Open();
                using (var setup = live.CreateCommand())
                {
                    setup.CommandText = "PRAGMA journal_mode=WAL;";
                    string mode = Convert.ToString(setup.ExecuteScalar()) ?? "";
                    Check(mode.Equals("wal", StringComparison.OrdinalIgnoreCase),
                        "SQLite fixture uses live WAL mode");
                }
                using (var create = live.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE events (id INTEGER PRIMARY KEY, name TEXT NOT NULL);";
                    create.ExecuteNonQuery();
                }
                using (var write = live.CreateCommand())
                {
                    write.CommandText = "INSERT INTO events (id, name) VALUES (1, 'first');";
                    write.ExecuteNonQuery();
                }
                Check(File.Exists(livePath + "-wal"),
                    "SQLite source retains an open WAL sidecar during snapshot");

                byte[] first = CloudBackupService.ReadBackupFileAsync(
                    livePath, CancellationToken.None).GetAwaiter().GetResult();
                CheckRestoredSqliteSnapshot(folder, "first", first, 1);

                // App is still running and writing after the first snapshot.
                using (var write = live.CreateCommand())
                {
                    write.CommandText = "INSERT INTO events (id, name) VALUES (2, 'second');";
                    write.ExecuteNonQuery();
                }
                byte[] second = CloudBackupService.ReadBackupFileAsync(
                    livePath, CancellationToken.None).GetAwaiter().GetResult();
                CheckRestoredSqliteSnapshot(folder, "second", second, 2);

                // Simulate the actual upload path: ZIP -> encrypted portable
                // backup -> decrypt -> extract -> SQLite reopen and integrity check.
                using var archiveStream = new MemoryStream();
                using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using Stream entry = zip.CreateEntry("data/local/running.sqlite").Open();
                    entry.Write(second, 0, second.Length);
                }
                byte[] encrypted = CloudBackupPortableSecurity.Encrypt(
                    archiveStream.ToArray(), "synthetic recovery password 1234");
                byte[] decrypted = CloudBackupPortableSecurity.Decrypt(
                    encrypted, "synthetic recovery password 1234");
                using var restoredZip = new ZipArchive(new MemoryStream(decrypted), ZipArchiveMode.Read);
                using (Stream entry = restoredZip.GetEntry("data/local/running.sqlite")!.Open())
                using (var restoredBytes = new MemoryStream())
                {
                    entry.CopyTo(restoredBytes);
                    CheckRestoredSqliteSnapshot(folder, "encrypted", restoredBytes.ToArray(), 2);
                }
            }

            // Not every .db extension is a SQLite database. Preserve these
            // normally instead of attempting a provider operation.
            string otherPath = Path.Combine(folder, "ordinary.db");
            byte[] ordinary = Encoding.UTF8.GetBytes("not a sqlite database: regular application data");
            File.WriteAllBytes(otherPath, ordinary);
            byte[] copied = CloudBackupService.ReadBackupFileAsync(
                otherPath, CancellationToken.None).GetAwaiter().GetResult();
            Check(copied.SequenceEqual(ordinary),
                "Non-SQLite .db files are included without SQLite conversion");

            // SQLite-looking but broken data must NEVER fall back to a raw
            // live copy and be declared a successful recovery snapshot.
            string invalidPath = Path.Combine(folder, "damaged.sqlite");
            File.WriteAllBytes(invalidPath,
                Encoding.ASCII.GetBytes("SQLite format 3\0invalid-content"));
            bool invalidRejected = false;
            try
            {
                _ = CloudBackupService.ReadBackupFileAsync(
                    invalidPath, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (SqliteException) { invalidRejected = true; }
            Check(invalidRejected,
                "Unrecoverable SQLite files fail backup instead of unsafe raw copy");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CheckRestoredSqliteSnapshot(
        string directory, string label, byte[] snapshot, long expectedRows)
    {
        string restoredPath = Path.Combine(directory, "restored-" + label + ".sqlite");
        File.WriteAllBytes(restoredPath, snapshot);
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = restoredPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        restored.Open();
        using var count = restored.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM events;";
        Check(Convert.ToInt64(count.ExecuteScalar()) == expectedRows,
            "Online SQLite snapshot " + label + " retains committed history");
        using var verify = restored.CreateCommand();
        verify.CommandText = "PRAGMA integrity_check;";
        Check(string.Equals(Convert.ToString(verify.ExecuteScalar()), "ok",
                StringComparison.OrdinalIgnoreCase),
            "Online SQLite snapshot " + label + " passes integrity_check");
    }
}
