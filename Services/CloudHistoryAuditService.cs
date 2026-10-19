using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveCommandCenter.Models;
using Microsoft.Data.Sqlite;

namespace EveCommandCenter.Services;

public sealed record HistoryAuditSource(string LogicalPath, string SourcePath);

public sealed class HistoryAuditCategory
{
    public string Name { get; init; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public int FilesWithRecordDates { get; set; }
    public DateTimeOffset? EarliestRecordedUtc { get; set; }
    public DateTimeOffset? LatestRecordedUtc { get; set; }
    public string Earliest => EarliestRecordedUtc?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";
    public string Latest => LatestRecordedUtc?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";
    public string Size => CloudHistoryAuditService.SizeText(Bytes);
}

public sealed class HistoryAuditReport
{
    public List<HistoryAuditCategory> Categories { get; init; } = new();
    public int Files => Categories.Sum(c => c.Files);
    public long Bytes => Categories.Sum(c => c.Bytes);
    public string Size => CloudHistoryAuditService.SizeText(Bytes);
    public long ExcludedGameLogBytes { get; init; }
    public int ExcludedGameLogFiles { get; init; }
    public int CheckedLiveSqliteDatabases { get; init; }
    public List<string> Warnings { get; init; } = new();
}

public sealed class CloudArchiveVerification
{
    public int Files { get; init; }
    public long Bytes { get; init; }
    public int CheckedSqliteDatabases { get; init; }
    public string Summary => $"Verified {Files:N0} files ({CloudHistoryAuditService.SizeText(Bytes)}), {CheckedSqliteDatabases:N0} SQLite database(s). No active application files were modified.";
}

/// <summary>
/// Read-only coverage inventory and isolated encrypted-archive verifier. This is
/// NOT a replacement for durable event collection or a live-application restore.
/// </summary>
public static class CloudHistoryAuditService
{
    private const long MaxRecordScanBytes = 64 * 1024 * 1024;
    private const long MaxLocalVerificationBytes = 512L * 1024 * 1024;
    private static readonly string[] Categories =
    {
        "Mining", "Moon operations", "Contracts / buyback", "Industry",
        "Planetary industry", "Pilots", "Other application data"
    };

    public static string SizeText(long bytes) => bytes >= 1024 * 1024 * 1024
        ? (bytes / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB"
        : bytes >= 1024 * 1024
            ? (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB"
            : (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";

    public static string Categorize(string logicalPath)
    {
        string p = logicalPath.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("moon") || p.Contains("fracture") || p.Contains("extraction") || p.Contains("drill")) return "Moon operations";
        if (p.Contains("contract") || p.Contains("buyback") || p.Contains("appraisal")) return "Contracts / buyback";
        if (p.Contains("planet") || p.Contains("/pi/") || p.Contains("/pihistory/") || p.Contains("planetary")) return "Planetary industry";
        if (p.Contains("industry") || p.Contains("manufactur") || p.Contains("blueprint") || p.Contains("production")) return "Industry";
        if (p.Contains("mining") || p.Contains("miner") || p.Contains("ore_") || p.Contains("session")) return "Mining";
        if (p.Contains("pilot") || p.Contains("character") || p.Contains("account")) return "Pilots";
        return "Other application data";
    }

    public static HistoryAuditReport Build(IEnumerable<HistoryAuditSource> included,
        string? externalGameLogFolder = null, CancellationToken cancellationToken = default)
    {
        var categories = Categories.ToDictionary(c => c, c => new HistoryAuditCategory { Name = c });
        var warnings = new List<string>();
        int checkedDatabases = 0;
        foreach (HistoryAuditSource source in included.GroupBy(x => x.LogicalPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = categories[Categorize(source.LogicalPath)];
            FileInfo file;
            try { file = new FileInfo(source.SourcePath); if (!file.Exists) { warnings.Add("Missing selected source: " + source.LogicalPath); continue; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add("Cannot inspect source: " + source.LogicalPath); continue; }
            category.Files++;
            category.Bytes += file.Length;
            if (new[] { ".sqlite", ".sqlite3", ".db" }.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (IsSqliteFile(file.FullName))
                    {
                        using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
                        {
                            DataSource = file.FullName, Mode = SqliteOpenMode.ReadOnly,
                            Pooling = false, DefaultTimeout = 4
                        }.ToString());
                        sqlite.Open();
                        using var command = sqlite.CreateCommand();
                        command.CommandText = "PRAGMA quick_check(1);";
                        if (!string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                            warnings.Add("SQLite integrity check FAILED: " + source.LogicalPath);
                        else checkedDatabases++;
                    }
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                { warnings.Add("SQLite integrity NOT verified: " + source.LogicalPath); }
            }
            // Only report dates extracted from actual record fields, never file timestamps.
            if (file.Length > MaxRecordScanBytes ||
                !new[] { ".json", ".jsonl" }.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                (DateTimeOffset? earliest, DateTimeOffset? latest) = ReadRecordDates(file.FullName, cancellationToken);
                if (earliest.HasValue && latest.HasValue)
                {
                    category.FilesWithRecordDates++;
                    if (!category.EarliestRecordedUtc.HasValue || earliest < category.EarliestRecordedUtc) category.EarliestRecordedUtc = earliest;
                    if (!category.LatestRecordedUtc.HasValue || latest > category.LatestRecordedUtc) category.LatestRecordedUtc = latest;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { warnings.Add("Record-date scan unavailable: " + source.LogicalPath); }
        }
        int externalFiles = 0;
        long externalBytes = 0;
        if (!string.IsNullOrWhiteSpace(externalGameLogFolder) && Directory.Exists(externalGameLogFolder))
        {
            try
            {
                foreach (string name in Directory.EnumerateFiles(externalGameLogFolder, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { externalFiles++; externalBytes += new FileInfo(name).Length; }
                    catch (IOException) { warnings.Add("An external EVE log could not be counted."); }
                }
            }
            catch (UnauthorizedAccessException) { warnings.Add("Not all external EVE game logs could be enumerated."); }
        }
        if (externalFiles > 0) warnings.Add($"External EVE game logs: {externalFiles:N0} files, {SizeText(externalBytes)} not selected for backup.");
        warnings.Add("Record date ranges are partial: timestamp-bearing JSON/JSONL up to 64 MiB is inspected; future schedule/expiry dates are excluded. 'Unknown' is not zero history.");
        warnings.Add("This audits saved files, not gaps while ESI or live telemetry were unavailable. It does not create missing historical activity.");
        warnings.Add("EVE SSO refresh tokens are archived separately from these source files and cannot be confirmed by a file inventory alone.");
        return new HistoryAuditReport
        {
            Categories = Categories.Select(k => categories[k]).ToList(),
            ExcludedGameLogFiles = externalFiles,
            ExcludedGameLogBytes = externalBytes,
            CheckedLiveSqliteDatabases = checkedDatabases,
            Warnings = warnings
        };
    }

    private static bool IsSqliteFile(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return stream.Read(header) == 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    private static (DateTimeOffset?, DateTimeOffset?) ReadRecordDates(string path, CancellationToken cancellationToken)
    {
        DateTimeOffset? min = null, max = null;
        void Observe(JsonElement element, string propertyName = "", int depth = 0)
        {
            if (depth > 12) return;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject()) Observe(property.Value, property.Name, depth + 1);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Observe(item, propertyName, depth + 1);
            }
            else if (element.ValueKind == JsonValueKind.String && IsDateProperty(propertyName))
            {
                string? value = element.GetString();
                if (value?.Length >= 10 && value.Length <= 40 && value[4] == '-' && value[7] == '-' &&
                    DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) &&
                    date.Year >= 2003 && date <= DateTimeOffset.UtcNow.AddDays(1))
                {
                    if (!min.HasValue || date < min) min = date;
                    if (!max.HasValue || date > max) max = date;
                }
            }
        }
        if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string line in File.ReadLines(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { using JsonDocument parsed = JsonDocument.Parse(line); Observe(parsed.RootElement); }
                catch (JsonException) { /* a malformed line cannot become a false date */ }
            }
        }
        else
        {
            using JsonDocument parsed = JsonDocument.Parse(File.ReadAllBytes(path));
            Observe(parsed.RootElement);
        }
        return (min, max);
    }

    private static bool IsDateProperty(string key)
    {
        if (key.StartsWith("next", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("expir", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("scheduled", StringComparison.OrdinalIgnoreCase)) return false;
        return key.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("DayKey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("observed", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("recorded", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("fetched", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("issued", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("accepted", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("fractured", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("created", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("lastupdate", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("extractionstart", StringComparison.OrdinalIgnoreCase);
    }


    public static async Task<CloudArchiveVerification> VerifyEncryptedFileAsync(string path, string password,
        CancellationToken cancellationToken = default)
    {
        FileInfo source = new(path);
        if (!source.Exists) throw new FileNotFoundException("Encrypted backup not found", path);
        if (source.Length > MaxLocalVerificationBytes)
            throw new InvalidOperationException("This encrypted backup exceeds the 512 MiB audit-verifier limit. It has NOT been verified; a streaming verifier is required.");
        byte[] encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] plaintext = CloudBackupPortableSecurity.Decrypt(encrypted, password);
        string temporary = Path.Combine(Path.GetTempPath(), "ecc-history-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            using var buffer = new MemoryStream(plaintext, writable: false);
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            ZipArchiveEntry manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup manifest missing.");
            CloudBackupManifest manifest;
            await using (Stream input = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<CloudBackupManifest>(input, cancellationToken: cancellationToken)
                    ?? throw new InvalidDataException("Backup manifest invalid.");
            if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported backup manifest version.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            int databases = 0;
            foreach (CloudBackupManifestFile item in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string logical = item.LogicalPath.Replace('\\', '/');
                string[] segments = logical.Split('/');
                if (logical.StartsWith('/') || logical.Contains(':') ||
                    segments.Any(s => s.Length == 0 || s == "." || s == "..") || !seen.Add(logical))
                    throw new InvalidDataException("Backup contains unsafe or repeated paths.");
                ZipArchiveEntry entry = zip.GetEntry("data/" + logical) ?? throw new InvalidDataException("Missing archived file: " + logical);
                string destination = Path.GetFullPath(Path.Combine(temporary, logical.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid restore destination.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (Stream input = entry.Open())
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, cancellationToken);
                var info = new FileInfo(destination);
                if (info.Length != item.Size) throw new InvalidDataException("Archived size mismatch: " + logical);
                byte[] hash = await File.ReadAllBytesAsync(destination, cancellationToken);
                if (!Convert.ToHexString(SHA256.HashData(hash)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archived checksum mismatch: " + logical);
                if (logical.Equals("eve/refresh-tokens.json", StringComparison.Ordinal) &&
                    !CloudBackupPortableSecurity.IsEncrypted(hash))
                    throw new InvalidDataException("EVE credentials are not encrypted within the archive.");
                bytes += info.Length;
                if (new[] { ".sqlite", ".sqlite3", ".db" }.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    // Non-SQLite .db files are permitted, matching the backup writer.
                    byte[] header = hash.AsSpan(0, Math.Min(16, hash.Length)).ToArray();
                    if (header.Length == 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0")
                    {
                        using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource = destination, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                        sqlite.Open();
                        using var check = sqlite.CreateCommand();
                        check.CommandText = "PRAGMA integrity_check;";
                        if (!string.Equals(Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("SQLite integrity verification failed: " + logical);
                        databases++;
                    }
                }
            }
            var actualEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry item in zip.Entries)
            {
                if (!item.FullName.StartsWith("data/", StringComparison.Ordinal)) continue;
                string logical = item.FullName["data/".Length..];
                if (!seen.Contains(logical) || !actualEntries.Add(logical))
                    throw new InvalidDataException("Archive has unlisted or duplicate data entries.");
            }
            return new CloudArchiveVerification { Files = seen.Count, Bytes = bytes, CheckedSqliteDatabases = databases };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (Directory.Exists(temporary))
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Isolated verification cleanup failed. Sensitive temporary files may remain at: " + temporary, ex);
                }
            }
        }
    }
}
