using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
namespace EveCommandCenter.Services;
public sealed partial class CloudBackupService
{
    // Called by the existing backup action. Tests inject HTTP and isolated settings, never real credentials.
    internal async Task UploadVerifiedBackupAsync(byte[] encrypted, string password, string token,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Checking the encrypted archive, file checksums, databases and EVE logins...");
        var report = await Task.Run(() => CloudHistoryAuditService.VerifyEncryptedBytesAsync(encrypted, password, ct), ct);
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        progress?.Report("Uploading the encrypted backup to Google Drive...");
        // Create a new object first; never overwrite the last good copy before verification.
        string name = PortablePrefix + "backup-" + now.UtcDateTime.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".ecc";
        await CreateRemoteBackupAsync(encrypted, token, ct, name);
        progress?.Report("Checking the uploaded Google Drive copy...");
        await VerifyUploadedSnapshotAsync(name, encrypted, token, ct);
        progress?.Report("Keeping the two most recent backups...");
        await KeepTwoBackupsAsync(name, token, ct);
        _settings = LoadSettings();
        _settings.LastBackupUtc = now;
        await SaveSettingsAsync();
        progress?.Report($"Backup complete: {report.Files:N0} files, {CloudHistoryAuditService.SizeText(report.Bytes)}. Google Drive copy verified. " +
            (report.MissingEveLogins > 0 ? $"{report.MissingEveLogins:N0} pilots had no saved login and will need reconnecting." : "Saved EVE logins verified."));
    }

    private async Task KeepTwoBackupsAsync(string verifiedName, string token, CancellationToken ct)
    {
        var files = await ListRemoteBackupsAsync(token, ct);
        var verified = files.SingleOrDefault(f => f.Name == verifiedName)
            ?? throw new InvalidDataException("The verified backup is missing from the restore-point list. Existing backups were retained.");
        var prior = files.Where(f => f.Id != verified.Id).OrderByDescending(f => f.ModifiedTime).ThenByDescending(f => f.Name).FirstOrDefault();
        foreach (var extra in files.Where(f => f.Id != verified.Id && f.Id != prior?.Id))
        {
            using var request = AuthorizedRequest(HttpMethod.Delete, DriveApi + "/files/" + Uri.EscapeDataString(extra.Id), token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Backup uploaded and verified, but older copies could not be removed (HTTP {(int)response.StatusCode}). More than two copies may remain.");
        }
    }

    private async Task VerifyUploadedSnapshotAsync(string name, byte[] encrypted, string token, CancellationToken ct)
    {
        DriveFile remote = await FindRemoteBackupAsync(ct, token, name)
            ?? throw new InvalidDataException("The uploaded backup could not be found on Google Drive. Backup completion was not recorded.");
        using var request = AuthorizedRequest(HttpMethod.Get, DriveApi + "/files/" + Uri.EscapeDataString(remote.Id) + "?alt=media", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Could not read back the uploaded backup (HTTP {(int)response.StatusCode}). Backup completion was not recorded.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920]; long count = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        {
            count += read;
            if (count > encrypted.LongLength) throw new InvalidDataException("Uploaded backup size differs from the local archive.");
            hash.AppendData(buffer, 0, read);
        }
        if (count != encrypted.LongLength || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), SHA256.HashData(encrypted)))
            throw new InvalidDataException("Uploaded backup did not match the local archive. Backup completion was not recorded; retry the backup.");
    }
}
