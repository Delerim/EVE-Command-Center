using System;
using System.Collections.Generic;

namespace EveMultiPreview.Models;

public sealed class CloudBackupSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public string GoogleTokenEndpoint { get; set; } =
        "https://oauth2.googleapis.com/token";
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupMinutes { get; set; } = 15;
    public DateTimeOffset? LastBackupUtc { get; set; }
    public DateTimeOffset? LastRestoreUtc { get; set; }
    public string GoogleAccountHint { get; set; } = "";
}

public sealed class CloudBackupPreferences
{
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupMinutes { get; set; } = 15;
}

public sealed class CloudBackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CreatedUtc { get; set; }
    public string AppVersion { get; set; } = "";
    public string MachineName { get; set; } = "";
    public List<CloudBackupManifestFile> Files { get; set; } = new();
}

public sealed class CloudBackupManifestFile
{
    public string LogicalPath { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class CloudBackupStatus
{
    public bool IsConfigured { get; init; }
    public bool IsConnected { get; init; }
    public bool HasRemoteBackup { get; init; }
    public DateTimeOffset? RemoteModifiedUtc { get; init; }
    public long RemoteSize { get; init; }
    public DateTimeOffset? LastBackupUtc { get; init; }
    public DateTimeOffset? LastRestoreUtc { get; init; }
    public bool AutoBackupEnabled { get; init; }
}
