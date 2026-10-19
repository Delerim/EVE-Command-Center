using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EveCommandCenter.Services;

public sealed partial class CloudBackupService
{
    /// <summary>
    /// Inventory exactly the local files selected by today's backup writer.
    /// Does not authenticate with Google, take a backup, or change user files.
    /// </summary>
    public Task<HistoryAuditReport> AuditSavedHistoryAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var included = DiscoverSources()
                .Select(s => new HistoryAuditSource(s.LogicalPath, s.SourcePath));
            string eveLogs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs");
            return CloudHistoryAuditService.Build(included, eveLogs, cancellationToken);
        }, cancellationToken);

    /// <summary>Test an encrypted local copy by restoring only into a temporary directory.
    /// No live app files, pilots, EVE credentials or Google Drive data are modified.</summary>
    public Task<CloudArchiveVerification> VerifyLocalEncryptedCopyAsync(string path,
        CancellationToken cancellationToken = default) =>
        CloudHistoryAuditService.VerifyEncryptedFileAsync(path,
            CloudBackupPortableSecurity.ReadPassword(), cancellationToken);
}
