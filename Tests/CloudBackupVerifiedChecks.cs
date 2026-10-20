using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckVerifiedCloudBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "ecc-backup-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string password = "synthetic recovery password";
        try
        {
            byte[] Archive(params (string Path, byte[] Bytes)[] files)
            {
                using var buffer = new MemoryStream();
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    foreach (var item in files) { using var entry = zip.CreateEntry("data/" + item.Path).Open(); entry.Write(item.Bytes); }
                    using var manifest = zip.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifest, new CloudBackupManifest { Files = files.Select(f => new CloudBackupManifestFile {
                        LogicalPath = f.Path, Size = f.Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(f.Bytes)) }).ToList() });
                }
                return CloudBackupPortableSecurity.Encrypt(buffer.ToArray(), password);
            }
            byte[] pilots = JsonSerializer.SerializeToUtf8Bytes(new[] { new { CharacterId = 123L } });
            byte[] logins = CloudBackupPortableSecurity.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new {
                RefreshTokens = new Dictionary<long, string> { [123] = "synthetic-not-a-real-login" }, MissingCharacterIds = Array.Empty<long>() }), password);
            byte[] archive = Archive(("local/PilotData/pilots.json", pilots), ("eve/refresh-tokens.json", logins), ("game-logs/Gamelogs/example.txt", "mining history"u8.ToArray()));
            var report = CloudHistoryAuditService.VerifyEncryptedBytesAsync(archive, password, default).GetAwaiter().GetResult();
            Check(report.Files == 3 && report.StoredEveLogins == 1, "Full archive verifies external logs and encrypted linked logins without touching credentials");
            bool rejected = false;
            try { CloudHistoryAuditService.VerifyEncryptedBytesAsync(Archive(("local/PilotData/pilots.json", pilots)), password, default).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Recovery refuses a pilot archive with missing login payload");
            foreach (string path in new[] { "local/../escape.json", "local/CloudBackup/cloud-backup.json", "unknown/history.json" })
            {
                rejected = false;
                try { CloudHistoryAuditService.VerifyEncryptedBytesAsync(Archive((path, "{}"u8.ToArray())), password, default).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Recovery rejects unsafe or unsupported destination " + path);
            }
            foreach (string failure in new[] { "", "upload", "download", "tamper", "cancel", "delete", "password" })
            {
                using var handler = new BackupDriveFixture { Failure = failure };
                string settings = Path.Combine(root, failure.Length == 0 ? "success.json" : failure + ".json");
                File.WriteAllText(settings, JsonSerializer.Serialize(new CloudBackupSettings { AutoBackupEnabled = true, AutoBackupMinutes = 137 }));
                byte[] before = File.ReadAllBytes(settings);
                using var service = new CloudBackupService(new HttpClient(handler), settings);
                bool failed = false;
                try { service.UploadVerifiedBackupAsync(archive, failure == "password" ? "incorrect password" : password, "synthetic-access", null, default).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException or OperationCanceledException) { failed = true; }
                if (failure.Length == 0)
                {
                    Check(!failed && handler.Files.Count == 2 && handler.Files.ContainsKey("old3"), "Verified upload retains new snapshot and newest previous copy only");
                    Check(service.Settings.LastBackupUtc.HasValue && service.Settings.AutoBackupMinutes == 137, "Only completed backup records success and preserves custom schedule");
                    service.UploadVerifiedBackupAsync(archive, password, "synthetic-access", null, default).GetAwaiter().GetResult();
                    Check(handler.Files.Count == 2 && !handler.Files.ContainsKey("old3"), "Repeated successful backup rotates exactly two full copies");
                }
                else
                {
                    Check(failed && before.SequenceEqual(File.ReadAllBytes(settings)), failure + " failure cannot record backup success");
                    Check(Enumerable.Range(0, 4).All(i => handler.Files.ContainsKey("old" + i)), failure + " failure preserves previous backups");
                    if (failure == "password") Check(handler.Requests == 0, "Invalid archive password fails before any cloud request");
                }
                Check(!handler.DeletedBeforeVerification, "Retention never deletes a copy before read-back verification");
            }
            var schedule = new CloudBackupSettings { AutoBackupEnabled = true, AutoBackupMinutes = 137, LastBackupUtc = DateTimeOffset.UtcNow };
            Check(!CloudBackupCoordinator.BackupIsDue(schedule, schedule.LastBackupUtc.Value.AddMinutes(136)) && CloudBackupCoordinator.BackupIsDue(schedule, schedule.LastBackupUtc.Value.AddMinutes(137)), "Schedule uses user-defined interval exactly");
            schedule.AutoBackupEnabled = false;
            Check(!CloudBackupCoordinator.BackupIsDue(schedule, DateTimeOffset.UtcNow.AddYears(1)) && !new CloudBackupSettings().AutoBackupEnabled, "Scheduling is opt-in and disabled schedules never become due");
            string app = Path.Combine(root, "app"), local = Path.Combine(root, "local"), logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(app); Directory.CreateDirectory(Path.Combine(local, "MarketData")); Directory.CreateDirectory(Path.Combine(local, "CloudBackup")); Directory.CreateDirectory(Path.Combine(logs, "Gamelogs"));
            File.WriteAllText(Path.Combine(local, "MarketData", "history.json"), "{}");
            File.WriteAllText(Path.Combine(local, "CloudBackup", "credentials.json"), "synthetic excluded");
            File.WriteAllText(Path.Combine(logs, "Gamelogs", "old.txt"), "historical mining");
            var sources = CloudBackupService.DiscoverSources(app, local, logs, null);
            Check(sources.Any(s => s.LogicalPath.Replace('\\', '/') == "game-logs/Gamelogs/old.txt") && sources.Any(s => s.LogicalPath.Contains("history.json")), "Backup discovery includes historical app data and external game logs");
            Check(!sources.Any(s => s.LogicalPath.Contains("credentials")), "Backup excludes Google credentials from application data");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BackupDriveFixture : HttpMessageHandler
    {
        internal string Failure = "";
        internal int Requests;
        internal bool DeletedBeforeVerification;
        private bool _readBack;
        internal Dictionary<string, (string Name, byte[] Bytes, DateTimeOffset Date)> Files = Enumerable.Range(0, 4).ToDictionary(i => "old" + i,
            i => ("eve-command-center-v2-backup-old" + i + ".ecc", new byte[] { 1 }, DateTimeOffset.UtcNow.AddDays(-4 + i)));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method == HttpMethod.Post)
            {
                if (Failure == "upload") return new(HttpStatusCode.ServiceUnavailable);
                _readBack = false;
                var parts = ((MultipartContent)request.Content!).ToArray();
                using var metadata = JsonDocument.Parse(await parts[0].ReadAsStringAsync(ct));
                string id = Guid.NewGuid().ToString("N");
                Files[id] = (metadata.RootElement.GetProperty("name").GetString()!, await parts[1].ReadAsByteArrayAsync(ct), DateTimeOffset.UtcNow);
                return Json(new { id });
            }
            string idPart = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.Method == HttpMethod.Delete)
            {
                DeletedBeforeVerification |= !_readBack;
                if (Failure == "delete") return new(HttpStatusCode.ServiceUnavailable);
                Files.Remove(idPart); return new(HttpStatusCode.NoContent);
            }
            if (request.RequestUri.Query.Contains("alt=media"))
            {
                if (Failure == "cancel") throw new OperationCanceledException();
                if (Failure == "download") return new(HttpStatusCode.ServiceUnavailable);
                _readBack = true;
                byte[] bytes = Files[idPart].Bytes.ToArray();
                if (Failure == "tamper") bytes[^1] ^= 1;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            string query = Uri.UnescapeDataString(request.RequestUri.Query);
            var files = Files.Where(f => !query.Contains("name='") || query.Contains("name='" + f.Value.Name + "'"));
            return Json(new { files = files.Select(f => new { id = f.Key, name = f.Value.Name, modifiedTime = f.Value.Date, size = f.Value.Bytes.Length }) });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
