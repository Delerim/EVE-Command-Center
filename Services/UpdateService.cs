using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;

namespace EveCommandCenter.Services;

/// <summary>
/// Handles automatic update checking, downloading, and self-replacement via GitHub Releases.
/// Flow: CheckForUpdateAsync → DownloadUpdateAsync → ApplyUpdate (exits app, spawns updater, relaunches).
/// </summary>
public sealed class UpdateService
{
    // Fork builds must only install releases that contain our custom features.
    private const string GITHUB_RELEASES_URL = "https://api.github.com/repos/Delerim/EVE-Command-Center/releases";
    private const string EXE_ASSET_NAME = "EVE.Command.Center.exe";

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Current app version from assembly metadata.</summary>
    public string CurrentVersion { get; }

    /// <summary>Latest version tag from GitHub (set after CheckForUpdateAsync).</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>Release notes body from GitHub (set after CheckForUpdateAsync).</summary>
    public string? ReleaseNotes { get; private set; }

    /// <summary>Direct download URL for the new exe (set after CheckForUpdateAsync).</summary>
    public string? DownloadUrl { get; private set; }

    /// <summary>HTML URL for the release page (set after CheckForUpdateAsync).</summary>
    public string? ReleasePageUrl { get; private set; }

    /// <summary>Whether an update is available (set after CheckForUpdateAsync).</summary>
    public bool UpdateAvailable { get; private set; }

    public UpdateService()
    {
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Query the GitHub Releases API and determine if an update is available.
    /// Returns true if an update is available.
    /// </summary>
    public async Task<bool> CheckForUpdateAsync(bool allowPreRelease = false)
    {
        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EVE-Command-Center/" + CurrentVersion);

            string apiUrl = allowPreRelease
                ? GITHUB_RELEASES_URL
                : GITHUB_RELEASES_URL + "/latest";

            var json = await _httpClient.GetStringAsync(apiUrl);

            ReadRelease(json, allowPreRelease);

            Debug.WriteLine($"[Update] Current={CurrentVersion}, Latest={LatestVersion}, Available={UpdateAvailable}");
            return UpdateAvailable;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Update] Check failed: {ex.Message}");
            return false;
        }
    }

    private void ReadRelease(string json, bool allowPreRelease)
    {
        UpdateAvailable = false;
        LatestVersion = ReleaseNotes = DownloadUrl = ReleasePageUrl = null;
        using var document = JsonDocument.Parse(json);
        var releases = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : new[] { document.RootElement };
        foreach (var release in releases)
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!allowPreRelease && release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var version)) continue;
            if (LatestVersion != null && Version.Parse(LatestVersion) >= version) continue;
            string? download = null;
            if (release.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                    if (asset.GetProperty("name").GetString() == EXE_ASSET_NAME)
                        download = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(download, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "github.com" ||
                !uri.AbsolutePath.StartsWith("/Delerim/EVE-Command-Center/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
            LatestVersion = tag;
            DownloadUrl = download;
            ReleasePageUrl = release.GetProperty("html_url").GetString();
            ReleaseNotes = release.TryGetProperty("body", out var body) ? body.GetString() : null;
            UpdateAvailable = version > Version.Parse(CurrentVersion);
        }
    }

    /// <summary>
    /// Download the new exe to a temp directory. Reports progress 0.0–1.0.
    /// Returns the path to the downloaded file.
    /// </summary>
    public async Task<string> DownloadUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(DownloadUrl))
            throw new InvalidOperationException("No download URL available. Call CheckForUpdateAsync first.");

        var tempDir = Path.Combine(Path.GetTempPath(), "EVECommandCenter_update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var destPath = Path.Combine(tempDir, EXE_ASSET_NAME);

        // Delete any old download
        if (File.Exists(destPath)) File.Delete(destPath);

        using var response = await _httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        long downloadedBytes = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloadedBytes += bytesRead;
            if (totalBytes > 0)
                progress?.Report((double)downloadedBytes / totalBytes);
        }

        progress?.Report(1.0);
        Debug.WriteLine($"[Update] Downloaded {downloadedBytes:N0} bytes to {destPath}");
        return destPath;
    }

    /// <summary>
    /// Spawn the PowerShell updater script that waits for exit, backs up config,
    /// replaces the exe, and relaunches. Then shuts down the current app.
    /// </summary>
    public void ApplyUpdate(string downloadedExePath)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot find the running executable.");
        var appDir = Path.GetDirectoryName(executable)!;
        var scriptPath = Path.Combine(Path.GetDirectoryName(downloadedExePath)!, "update.ps1");
        var script = $@"
$ErrorActionPreference = 'Stop'
$appDir = '{EscapePs(appDir)}'
$newExe = '{EscapePs(downloadedExePath)}'
$oldExe = '{EscapePs(executable)}'
$backupExe = $oldExe + '.previous'
try {{
    $running = Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue
    if ($running) {{
        if (-not $running.WaitForExit(60000)) {{ throw 'Application did not exit in time.' }}
    }}
    $configFile = Join-Path $appDir 'EVE Command Center.json'
    if (Test-Path -LiteralPath $configFile) {{
        $backupDir = Join-Path $appDir 'Backups'
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        Copy-Item -LiteralPath $configFile -Destination (Join-Path $backupDir ('EVE Command Center_' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss') + '.json'))
    }}
    Copy-Item -LiteralPath $oldExe -Destination $backupExe -Force
    try {{
        Copy-Item -LiteralPath $newExe -Destination $oldExe -Force
        $restarted = Start-Process -FilePath $oldExe -WorkingDirectory $appDir -PassThru
        if ($restarted.WaitForExit(2000)) {{ throw 'Updated application exited during startup.' }}
        Remove-Item -LiteralPath $backupExe -Force -ErrorAction SilentlyContinue
    }} catch {{
        Copy-Item -LiteralPath $backupExe -Destination $oldExe -Force
        throw
    }}
    Remove-Item -LiteralPath $newExe -Force -ErrorAction SilentlyContinue
}} catch {{
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $appDir 'EVE Command Center Update Error.txt')
}}
";

        File.WriteAllText(scriptPath, script);

        // Launch PowerShell hidden
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Process.Start(psi) == null)
            throw new InvalidOperationException("Could not start the updater.");

        Debug.WriteLine("[Update] Updater script launched — shutting down app");
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
    }

    private static string EscapePs(string path) => path.Replace("'", "''");
}
