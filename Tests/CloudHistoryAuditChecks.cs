using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static void CheckCloudHistoryAudit()
    {
        string root = Path.Combine(Path.GetTempPath(), "ecc-history-audit-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string mining = Path.Combine(root, "mining.jsonl");
            File.WriteAllText(mining,
                "{\"ObservedUtc\":\"2025-01-07T10:00:00Z\",\"M3\":123}\n" +
                "{\"ObservedUtc\":\"2026-10-11T10:00:00Z\",\"M3\":456}\n" +
                "{\"ExpiresUtc\":\"2026-10-18T10:00:00Z\",\"M3\":999}\n");
            string moon = Path.Combine(root, "moon-profiles.csv");
            File.WriteAllText(moon, "moon,ore\nSynthetic moon,Zeolites\n");
            string logs = Path.Combine(root, "EVE", "logs", "Gamelogs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "synthetic.log"), "outside the current automatic backup");

            var inventory = CloudHistoryAuditService.Build(new[]
            {
                new HistoryAuditSource("local/MiningData/mining.jsonl", mining),
                new HistoryAuditSource("local/PrivateMoonProfiles/moon-profile.csv", moon)
            }, Path.Combine(root, "EVE", "logs"));
            var miningRow = inventory.Categories.Single(c => c.Name == "Mining");
            Check(inventory.Files == 2 && inventory.ExcludedGameLogFiles == 1,
                "History audit separates selected application files from excluded EVE game logs");
            Check(miningRow.Earliest == "2025-01-07" && miningRow.Latest == "2026-10-11",
                "History audit obtains record dates from actual timestamp fields");
            Check(inventory.Categories.Single(c => c.Name == "Moon operations").Earliest == "Unknown",
                "History audit does not invent event dates for files without timestamp fields");
            string database = Path.Combine(root, "history.sqlite");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = database, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE observations (time TEXT NOT NULL); INSERT INTO observations VALUES ('2026-09-18');";
                command.ExecuteNonQuery();
            }
            byte[] sqlite = File.ReadAllBytes(database);
            var files = new (string Name, byte[] Bytes)[]
            {
                ("local/mining.jsonl", File.ReadAllBytes(mining)),
                ("local/history.sqlite", sqlite)
            };
            byte[] zipBytes;
            using (var buffer = new MemoryStream())
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = new CloudBackupManifest { Files = files.Select(f => new CloudBackupManifestFile {
                        LogicalPath = f.Name, Size = f.Bytes.Length,
                        Sha256 = Convert.ToHexString(SHA256.HashData(f.Bytes))
                    }).ToList() };
                    foreach (var item in files)
                    {
                        using Stream stream = zip.CreateEntry("data/" + item.Name).Open();
                        stream.Write(item.Bytes);
                    }
                    using Stream manifestStream = zip.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifestStream, manifest);
                }
                zipBytes = buffer.ToArray();
            }
            string encryptedFile = Path.Combine(root, "synthetic.ecc");
            File.WriteAllBytes(encryptedFile, CloudBackupPortableSecurity.Encrypt(zipBytes, "synthetic recovery password"));
            byte[] before = File.ReadAllBytes(database);
            var verification = CloudHistoryAuditService.VerifyEncryptedFileAsync(encryptedFile,
                "synthetic recovery password", CancellationToken.None).GetAwaiter().GetResult();
            Check(verification.Files == 2 && verification.CheckedSqliteDatabases == 1,
                "History audit verifies encrypted manifest and SQLite integrity in an isolated directory");
            Check(before.SequenceEqual(File.ReadAllBytes(database)),
                "Recovery verification never modifies the active source database");
            bool rejected = false;
            try
            {
                CloudHistoryAuditService.VerifyEncryptedFileAsync(encryptedFile,
                    "not the correct password").GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Recovery verifier rejects wrong password before extracting any files");
            File.WriteAllBytes(encryptedFile, CloudBackupPortableSecurity.Encrypt(
                Encoding.UTF8.GetBytes("not a ZIP file"), "synthetic recovery password"));
            bool invalid = false;
            try
            {
                CloudHistoryAuditService.VerifyEncryptedFileAsync(encryptedFile,
                    "synthetic recovery password").GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { invalid = true; }
            Check(invalid, "Recovery verification does not report success for an invalid archive");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
