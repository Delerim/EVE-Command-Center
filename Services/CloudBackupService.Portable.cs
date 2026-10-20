using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed partial class CloudBackupService
{
    private const string PortableLatestName = "eve-command-center-v2-latest.ecc";
    private const string PortablePrefix = "eve-command-center-v2-";

    public bool HasRecoveryPassword => CloudBackupPortableSecurity.IsConfigured;
    public void SetRecoveryPassword(string password) => CloudBackupPortableSecurity.RememberPassword(password);

    public async Task SelectPrivateMoonCsvAsync(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose an existing private moon CSV file.", path);
        _settings.PrivateMoonCsvPath = Path.GetFullPath(path);
        await SaveSettingsAsync();
    }

    // The cloud API lists objects in the hidden appDataFolder, not My Drive.
    private async Task<IReadOnlyList<DriveFile>> ListRemoteBackupsAsync(string token, CancellationToken ct)
    {
        string url = DriveApi + "/files?spaces=appDataFolder&q=" +
            Uri.EscapeDataString("trashed=false and name contains 'eve-command-center-'") +
            "&fields=nextPageToken,files(id,name,modifiedTime,size)&pageSize=100";
        var result = new List<DriveFile>();
        // Bounded pagination also handles accounts with extensive historic snapshots.
        for (int page = 0; page < 30; page++)
        {
            using var request = AuthorizedRequest(HttpMethod.Get, url, token);
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            await EnsureSuccessAsync(response, "list restore points");
            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("files", out JsonElement files))
                result.AddRange(JsonSerializer.Deserialize<List<DriveFile>>(files.GetRawText(), _json) ?? new());
            if (!document.RootElement.TryGetProperty("nextPageToken", out JsonElement next) ||
                string.IsNullOrWhiteSpace(next.GetString())) break;
            string tokenArg = Uri.EscapeDataString(next.GetString()!);
            string first = url.Split("&pageToken=", 2, StringSplitOptions.None)[0];
            url = first + "&pageToken=" + tokenArg;
        }
        return result.Where(item =>
            item.Name.StartsWith(PortablePrefix, StringComparison.Ordinal) ||
            string.Equals(item.Name, BackupName, StringComparison.Ordinal)).ToArray();
    }

    public async Task<IReadOnlyList<CloudRestorePoint>> GetRestorePointsAsync(CancellationToken ct = default)
    {
        string token = await GetAccessTokenAsync(ct);
        return (await ListRemoteBackupsAsync(token, ct))
            .OrderByDescending(f => f.ModifiedTime)
            .Select(f => new CloudRestorePoint
            {
                Id = f.Id,
                Name = f.Name,
                ModifiedUtc = f.ModifiedTime,
                Size = f.Size,
                IsLegacy = f.Name == BackupName
            }).ToArray();
    }

    public async Task RestoreLocalEncryptedFileAsync(string path, CancellationToken ct = default)
    {
        string password = CloudBackupPortableSecurity.ReadPassword();
        await _gate.WaitAsync(ct);
        string? archive = null;
        try
        {
            byte[] incoming = await File.ReadAllBytesAsync(path, ct);
            await Task.Run(() => CloudHistoryAuditService.VerifyEncryptedBytesAsync(incoming, password, ct), ct);
            byte[] plain = CloudBackupPortableSecurity.Decrypt(incoming, password);
            archive = TempPath("restore-local", ".zip");
            await File.WriteAllBytesAsync(archive, plain, ct);
            CryptographicOperations.ZeroMemory(plain);
            await RestoreArchiveAsync(archive, ct);
            _settings.LastRestoreUtc = DateTimeOffset.UtcNow;
            await SaveSettingsAsync();
        }
        finally { TryDelete(archive); _gate.Release(); }
    }

    public async Task ExportEncryptedSnapshotAsync(string destination, CancellationToken ct = default)
    {
        string password = CloudBackupPortableSecurity.ReadPassword();
        string? archive = null;
        await _gate.WaitAsync(ct);
        try
        {
            archive = await CreateArchiveAsync(password, ct);
            byte[] encrypted = CloudBackupPortableSecurity.Encrypt(await File.ReadAllBytesAsync(archive, ct), password);
            string temp = destination + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, encrypted, ct);
                File.Move(temp, destination, true);
            }
            finally { TryDelete(temp); }
        }
        finally { TryDelete(archive); _gate.Release(); }
    }
}
