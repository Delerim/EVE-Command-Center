using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EveMultiPreview.Models;
using WpfApplication = System.Windows.Application;

namespace EveMultiPreview.Services;

public sealed class CloudBackupService : IDisposable
{
    public const string DriveScope =
        "https://www.googleapis.com/auth/drive.appdata";
    private const string BackupName = "eve-command-center-backup-v1.zip";
    private const string AuthorizeEndpoint =
        "https://accounts.google.com/o/oauth2/v2/auth";
    private const string DriveApi = "https://www.googleapis.com/drive/v3";
    private const string DriveUploadApi =
        "https://www.googleapis.com/upload/drive/v3";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsFile;
    private CloudBackupSettings _settings;
    private string _accessToken = "";
    private DateTimeOffset _accessTokenExpiresUtc;

    public CloudBackupService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EVE-Command-Center-Cloud-Backup/1.0");
        string root = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center", "CloudBackup");
        Directory.CreateDirectory(root);
        _settingsFile = Path.Combine(root, "cloud-backup.json");
        _settings = LoadSettings();
    }

    public CloudBackupSettings Settings => _settings;
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.GoogleClientId);
    public bool HasStoredGoogleLogin =>
        !string.IsNullOrWhiteSpace(CloudCredentialStore.Read());

    public async Task ConfigureFromGoogleJsonAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(fileName, cancellationToken));
        JsonElement root = document.RootElement;
        JsonElement client = root.TryGetProperty(
            "installed", out JsonElement installed)
            ? installed
            : throw new InvalidOperationException(
                "Choose a Google OAuth client created as Desktop app.");

        string clientId = client.GetProperty("client_id").GetString() ?? "";
        string clientSecret = client.TryGetProperty(
            "client_secret", out JsonElement secret)
            ? secret.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(
                "The Google OAuth JSON does not contain a client_id.");

        _settings.GoogleClientId = clientId;
        _settings.GoogleClientSecret = clientSecret;
        _settings.GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
        await SaveSettingsAsync();
        await AuthorizeGoogleAsync(cancellationToken);
    }

    public async Task AuthorizeGoogleAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "Choose a Google Desktop OAuth client JSON first.");

        int port = ReserveLoopbackPort();
        string redirectUri = $"http://127.0.0.1:{port}/";
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        string url = AuthorizeEndpoint +
            "?client_id=" + Uri.EscapeDataString(_settings.GoogleClientId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString(DriveScope) +
            "&access_type=offline&prompt=consent" +
            "&state=" + Uri.EscapeDataString(state) +
            "&code_challenge=" + Uri.EscapeDataString(challenge) +
            "&code_challenge_method=S256";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        HttpListenerContext context;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
        }

        string? error = context.Request.QueryString["error"];
        string? returnedState = context.Request.QueryString["state"];
        string? code = context.Request.QueryString["code"];
        bool accepted = string.IsNullOrWhiteSpace(error) &&
            string.Equals(state, returnedState, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(code);
        await WriteBrowserResponseAsync(context.Response, accepted);
        if (!accepted)
            throw new InvalidOperationException(
                "Google authorization was cancelled or the callback was invalid" +
                (string.IsNullOrWhiteSpace(error) ? "." : ": " + error));

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _settings.GoogleClientId,
            ["code"] = code!,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };
        if (!string.IsNullOrWhiteSpace(_settings.GoogleClientSecret))
            form["client_secret"] = _settings.GoogleClientSecret;

        GoogleTokenResponse token = await PostTokenAsync(form, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidOperationException(
                "Google did not return a refresh token. Remove this app from " +
                "your Google account permissions and connect again.");
        CloudCredentialStore.Write(token.RefreshToken);
        CacheAccessToken(token);
        _settings.GoogleAccountHint = "Google Drive connected";
        await SaveSettingsAsync();
    }

    public async Task DisconnectAsync()
    {
        CloudCredentialStore.Delete();
        _accessToken = "";
        _accessTokenExpiresUtc = default;
        _settings.GoogleAccountHint = "";
        await SaveSettingsAsync();
    }

    public async Task<CloudBackupStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        DriveFile? remote = null;
        bool connected = IsConfigured && HasStoredGoogleLogin;
        if (connected)
        {
            try { remote = await FindRemoteBackupAsync(cancellationToken); }
            catch { connected = false; }
        }
        return new CloudBackupStatus
        {
            IsConfigured = IsConfigured,
            IsConnected = connected,
            HasRemoteBackup = remote != null,
            RemoteModifiedUtc = remote?.ModifiedTime,
            RemoteSize = remote?.Size ?? 0,
            LastBackupUtc = _settings.LastBackupUtc,
            LastRestoreUtc = _settings.LastRestoreUtc,
            AutoBackupEnabled = _settings.AutoBackupEnabled
        };
    }

    public async Task SetAutoBackupAsync(bool enabled, int minutes)
    {
        _settings.AutoBackupEnabled = enabled;
        _settings.AutoBackupMinutes = Math.Clamp(minutes, 5, 1440);
        await SaveSettingsAsync();
    }

    public async Task BackupNowAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? archive = null;
        try
        {
            progress?.Report("Creating a safe snapshot of local data...");
            archive = await CreateArchiveAsync(cancellationToken);
            progress?.Report("Uploading backup to private Google Drive app data...");
            string token = await GetAccessTokenAsync(cancellationToken);
            DriveFile? remote = await FindRemoteBackupAsync(
                cancellationToken, token);
            byte[] bytes = await File.ReadAllBytesAsync(
                archive, cancellationToken);
            if (remote == null)
                await CreateRemoteBackupAsync(bytes, token, cancellationToken);
            else
                await UpdateRemoteBackupAsync(
                    remote.Id, bytes, token, cancellationToken);
            _settings.LastBackupUtc = DateTimeOffset.UtcNow;
            await SaveSettingsAsync();
            progress?.Report("Cloud backup completed.");
        }
        finally
        {
            _gate.Release();
            TryDelete(archive);
        }
    }

    public async Task RestoreNowAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? archive = null;
        try
        {
            string token = await GetAccessTokenAsync(cancellationToken);
            DriveFile remote = await FindRemoteBackupAsync(
                cancellationToken, token) ??
                throw new InvalidOperationException(
                    "No EVE Command Center backup exists in this Google account.");
            progress?.Report("Downloading cloud backup...");
            archive = TempPath("restore", ".zip");
            using (var request = AuthorizedRequest(
                       HttpMethod.Get,
                       DriveApi + "/files/" + remote.Id + "?alt=media",
                       token))
            using (HttpResponseMessage response = await _http.SendAsync(
                       request, HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                await EnsureSuccessAsync(response, "download backup");
                await using Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken);
                await using FileStream destination = File.Create(archive);
                await source.CopyToAsync(destination, cancellationToken);
            }

            progress?.Report("Validating and restoring files...");
            await RestoreArchiveAsync(archive, cancellationToken);
            _settings.LastRestoreUtc = DateTimeOffset.UtcNow;
            await SaveSettingsAsync();
            progress?.Report("Restore completed. Restarting is required.");
        }
        finally
        {
            _gate.Release();
            TryDelete(archive);
        }
    }

    public void RestartApplication()
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException(
                "Could not locate the running executable for restart.");
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? ""
        });
        WpfApplication.Current.Shutdown();
    }

    private async Task<string> CreateArchiveAsync(
        CancellationToken cancellationToken)
    {
        string archivePath = TempPath("backup", ".zip");
        var manifest = new CloudBackupManifest
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            AppVersion = typeof(CloudBackupService).Assembly
                .GetName().Version?.ToString() ?? "unknown",
            MachineName = Environment.MachineName
        };
        IReadOnlyList<BackupSource> sources = DiscoverSources();
        await using FileStream file = File.Create(archivePath);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach (BackupSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await ReadStableBytesAsync(
                source.SourcePath, cancellationToken);
            ZipArchiveEntry entry = zip.CreateEntry(
                "data/" + source.LogicalPath.Replace('\\', '/'),
                CompressionLevel.Optimal);
            await using (Stream stream = entry.Open())
                await stream.WriteAsync(bytes, cancellationToken);
            manifest.Files.Add(new CloudBackupManifestFile
            {
                LogicalPath = source.LogicalPath.Replace('\\', '/'),
                Size = bytes.LongLength,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
        }
        byte[] preferenceBytes = JsonSerializer.SerializeToUtf8Bytes(
            new CloudBackupPreferences
            {
                AutoBackupEnabled = _settings.AutoBackupEnabled,
                AutoBackupMinutes = _settings.AutoBackupMinutes
            }, _json);
        ZipArchiveEntry preferenceEntry = zip.CreateEntry(
            "data/cloud/preferences.json", CompressionLevel.Optimal);
        await using (Stream stream = preferenceEntry.Open())
            await stream.WriteAsync(preferenceBytes, cancellationToken);
        manifest.Files.Add(new CloudBackupManifestFile
        {
            LogicalPath = "cloud/preferences.json",
            Size = preferenceBytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(preferenceBytes))
        });
        ZipArchiveEntry manifestEntry = zip.CreateEntry(
            "manifest.json", CompressionLevel.Optimal);
        await using (Stream stream = manifestEntry.Open())
            await JsonSerializer.SerializeAsync(
                stream, manifest, _json, cancellationToken);
        return archivePath;
    }

    private IReadOnlyList<BackupSource> DiscoverSources()
    {
        string exeDir = AppContext.BaseDirectory;
        string localRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center");
        var result = new List<BackupSource>();
        AddFile(result, Path.Combine(exeDir, "EVE Command Center.json"),
            "config/EVE Command Center.json");
        AddFile(result, Path.Combine(exeDir, "EVE Command Center Mining.json"),
            "config/EVE Command Center Mining.json");
        if (Directory.Exists(localRoot))
        {
            foreach (string path in Directory.GetFiles(
                         localRoot, "*", SearchOption.AllDirectories)
                     .Where(path =>
                         path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            {
                string relative = Path.GetRelativePath(localRoot, path);
                string first = relative.Split(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)[0];
                if (first.Equals("CloudBackup", StringComparison.OrdinalIgnoreCase) ||
                    first.Equals("RestoreBackups", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddFile(result, path, "local/" + relative);
            }
        }

        string mining = Path.Combine(exeDir, "MiningData");
        if (Directory.Exists(mining))
        {
            foreach (string path in Directory.GetFiles(
                         mining, "*", SearchOption.AllDirectories)
                     .Where(path =>
                         path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            {
                string relative = Path.GetRelativePath(mining, path);
                AddFile(result, path, "mining/" + relative);
            }
        }
        return result;
    }

    private async Task RestoreArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry manifestEntry = zip.GetEntry("manifest.json") ??
            throw new InvalidDataException("Backup manifest is missing.");
        CloudBackupManifest manifest;
        await using (Stream stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<CloudBackupManifest>(
                stream, _json, cancellationToken) ??
                throw new InvalidDataException("Backup manifest is invalid.");
        }
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException(
                "This backup uses an unsupported schema version.");

        string safetyRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center", "RestoreBackups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        foreach (CloudBackupManifestFile item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string logical = NormalizeLogicalPath(item.LogicalPath);
            ZipArchiveEntry entry = zip.GetEntry("data/" + logical) ??
                throw new InvalidDataException(
                    "Backup entry is missing: " + logical);
            byte[] bytes;
            await using (Stream source = entry.Open())
            using (var memory = new MemoryStream())
            {
                await source.CopyToAsync(memory, cancellationToken);
                bytes = memory.ToArray();
            }
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (bytes.LongLength != item.Size || !string.Equals(
                    hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Backup checksum failed for " + logical);

            if (logical == "cloud/preferences.json")
            {
                CloudBackupPreferences preferences =
                    JsonSerializer.Deserialize<CloudBackupPreferences>(
                        bytes, _json) ?? new CloudBackupPreferences();
                _settings.AutoBackupEnabled = preferences.AutoBackupEnabled;
                _settings.AutoBackupMinutes = Math.Clamp(
                    preferences.AutoBackupMinutes, 5, 1440);
                continue;
            }

            string destination = ResolveDestination(logical);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                string safeCopy = Path.Combine(
                    safetyRoot, logical.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(safeCopy)!);
                File.Copy(destination, safeCopy, overwrite: true);
            }
            string temp = destination + ".cloud-restore.tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, destination, overwrite: true);
        }
    }

    private string ResolveDestination(string logical)
    {
        string exeDir = AppContext.BaseDirectory;
        string localRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center");
        if (logical.StartsWith("config/", StringComparison.Ordinal))
            return Path.Combine(exeDir, logical["config/".Length..]);
        if (logical.StartsWith("mining/", StringComparison.Ordinal))
            return Path.Combine(exeDir, "MiningData",
                logical["mining/".Length..].Replace('/', Path.DirectorySeparatorChar));
        if (logical.StartsWith("local/", StringComparison.Ordinal))
            return Path.Combine(localRoot,
                logical["local/".Length..].Replace(
                    '/', Path.DirectorySeparatorChar));
        throw new InvalidDataException(
            "Backup contains an unknown logical path: " + logical);
    }

    private async Task<DriveFile?> FindRemoteBackupAsync(
        CancellationToken cancellationToken,
        string? knownToken = null)
    {
        string token = knownToken ?? await GetAccessTokenAsync(cancellationToken);
        string query = "name='" + BackupName.Replace("'", "\\'") +
            "' and trashed=false";
        string url = DriveApi + "/files?spaces=appDataFolder&q=" +
            Uri.EscapeDataString(query) +
            "&fields=files(id,name,modifiedTime,size)&pageSize=10";
        using var request = AuthorizedRequest(HttpMethod.Get, url, token);
        using HttpResponseMessage response = await _http.SendAsync(
            request, cancellationToken);
        await EnsureSuccessAsync(response, "list backups");
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        DriveFileList list = JsonSerializer.Deserialize<DriveFileList>(
            body, _json) ?? new DriveFileList();
        return list.Files
            .OrderByDescending(file => file.ModifiedTime)
            .FirstOrDefault();
    }

    private async Task CreateRemoteBackupAsync(
        byte[] bytes,
        string token,
        CancellationToken cancellationToken)
    {
        using var multipart = new MultipartContent("related");
        var metadata = new StringContent(
            JsonSerializer.Serialize(new
            {
                name = BackupName,
                parents = new[] { "appDataFolder" }
            }), Encoding.UTF8, "application/json");
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(metadata);
        multipart.Add(content);
        using var request = AuthorizedRequest(
            HttpMethod.Post,
            DriveUploadApi + "/files?uploadType=multipart&fields=id,modifiedTime,size",
            token);
        request.Content = multipart;
        using HttpResponseMessage response = await _http.SendAsync(
            request, cancellationToken);
        await EnsureSuccessAsync(response, "create backup");
    }

    private async Task UpdateRemoteBackupAsync(
        string fileId,
        byte[] bytes,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Patch,
            DriveUploadApi + "/files/" + fileId +
            "?uploadType=media&fields=id,modifiedTime,size",
            token);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/zip");
        using HttpResponseMessage response = await _http.SendAsync(
            request, cancellationToken);
        await EnsureSuccessAsync(response, "update backup");
    }

    private async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) &&
            DateTimeOffset.UtcNow < _accessTokenExpiresUtc.AddMinutes(-2))
            return _accessToken;
        string refresh = CloudCredentialStore.Read() ??
            throw new InvalidOperationException(
                "Google Drive is not connected on this PC.");
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _settings.GoogleClientId,
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(_settings.GoogleClientSecret))
            form["client_secret"] = _settings.GoogleClientSecret;
        GoogleTokenResponse token = await PostTokenAsync(form, cancellationToken);
        CacheAccessToken(token);
        return _accessToken;
    }

    private async Task<GoogleTokenResponse> PostTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await _http.PostAsync(
            _settings.GoogleTokenEndpoint, content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "Google token request failed: " + Trim(body));
        return JsonSerializer.Deserialize<GoogleTokenResponse>(body, _json) ??
            throw new InvalidOperationException(
                "Google returned an empty token response.");
    }

    private void CacheAccessToken(GoogleTokenResponse token)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException(
                "Google did not return an access token.");
        _accessToken = token.AccessToken;
        _accessTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(
            Math.Max(60, token.ExpiresIn));
    }

    private CloudBackupSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFile))
                return JsonSerializer.Deserialize<CloudBackupSettings>(
                    File.ReadAllText(_settingsFile), _json) ?? new();
        }
        catch { }
        return new CloudBackupSettings();
    }

    public void ReloadSettings()
    {
        _settings = LoadSettings();
    }

    private async Task SaveSettingsAsync()
    {
        string temp = _settingsFile + ".tmp";
        await File.WriteAllTextAsync(
            temp, JsonSerializer.Serialize(_settings, _json));
        File.Move(temp, _settingsFile, overwrite: true);
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string url,
        string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"Google Drive could not {operation}: " + Trim(body));
    }

    private static async Task<byte[]> ReadStableBytesAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                return memory.ToArray();
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
        throw new IOException("Could not snapshot " + path);
    }

    private static async Task WriteBrowserResponseAsync(
        HttpListenerResponse response,
        bool accepted)
    {
        string message = accepted
            ? "Google Drive connected. You can close this tab and return to EVE Command Center."
            : "Google Drive connection failed. Return to EVE Command Center for details.";
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<!doctype html><html><body style='font-family:Segoe UI;background:#07181b;color:#eaffff;padding:40px'>" +
            "<h2>" + WebUtility.HtmlEncode(message) + "</h2></body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AddFile(
        ICollection<BackupSource> result,
        string path,
        string logical)
    {
        if (File.Exists(path))
            result.Add(new BackupSource(path, logical));
    }

    private static string NormalizeLogicalPath(string value)
    {
        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':') ||
            segments.Any(segment => segment == "..") ||
            string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("Unsafe path in backup manifest.");
        return normalized;
    }

    private static string TempPath(string label, string extension)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "EVECommandCenterCloud");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory,
            label + "-" + Guid.NewGuid().ToString("N") + extension);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    private static string Trim(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 300 ? value : value[..300];
    }
    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _http.Dispose();
    }

    private sealed record BackupSource(string SourcePath, string LogicalPath);
    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = "";
    }
    private sealed class DriveFileList
    {
        public List<DriveFile> Files { get; set; } = new();
    }
    private sealed class DriveFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset ModifiedTime { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Size { get; set; }
    }
}

public sealed class CloudBackupCoordinator : IDisposable
{
    private static CloudBackupCoordinator? _instance;
    private readonly CloudBackupService _service = new();
    private readonly DispatcherTimer _timer;
    private bool _running;

    private CloudBackupCoordinator()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Clamp(
                _service.Settings.AutoBackupMinutes, 5, 1440))
        };
        _timer.Tick += Timer_Tick;
        if (_service.Settings.AutoBackupEnabled &&
            _service.IsConfigured && _service.HasStoredGoogleLogin)
            _timer.Start();
        if (WpfApplication.Current != null)
            WpfApplication.Current.Exit += Application_Exit;
    }

    public static CloudBackupCoordinator Attach() =>
        _instance ??= new CloudBackupCoordinator();

    public static void RefreshSettings()
    {
        _instance?.ReloadSettings();
    }

    private void ReloadSettings()
    {
        _service.ReloadSettings();
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(
            _service.Settings.AutoBackupMinutes, 5, 1440));
        if (_service.Settings.AutoBackupEnabled &&
            _service.IsConfigured && _service.HasStoredGoogleLogin)
            _timer.Start();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_running) return;
        _running = true;
        try { await _service.BackupNowAsync(); }
        catch (Exception ex)
        {
            Debug.WriteLine("[CloudBackup] Automatic backup failed: " + ex.Message);
        }
        finally { _running = false; }
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (_running || !_service.Settings.AutoBackupEnabled ||
            !_service.IsConfigured || !_service.HasStoredGoogleLogin)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
            Task.Run(
                    () => _service.BackupNowAsync(
                        cancellationToken: timeout.Token),
                    timeout.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[CloudBackup] Exit backup failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (WpfApplication.Current != null)
            WpfApplication.Current.Exit -= Application_Exit;
        _service.Dispose();
    }
}

internal static class CloudCredentialStore
{
    private const string TargetName =
        "EVE Command Center:GoogleDriveCloudBackup";
    private const uint Generic = 1;
    private const uint LocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(
        string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string token)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(token);
        IntPtr blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = TargetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = LocalMachine,
                UserName = "Google Drive"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not save the Google Drive refresh token.");
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    public static string? Read()
    {
        if (!CredRead(TargetName, Generic, 0, out IntPtr pointer))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize == 0) return null;
            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public static void Delete()
    {
        if (!CredDelete(TargetName, Generic, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168)
                throw new Win32Exception(error);
        }
    }
}
