using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EveCommandCenter.Services;

// Webhook URLs are secrets. Store only user-scope DPAPI ciphertext on disk,
// never write a URL to diagnostics and never put it in the ordinary settings JSON.
public sealed class DiscordWebhookDestination
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "EVE alerts";
    // Display hint, not a channel selector: Discord binds the destination to the webhook.
    public string Channel { get; set; } = "";
    public string ProtectedUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> AlertTypes { get; set; } = new() { DiscordWebhookService.MoonNotSet };
}

public sealed class DiscordWebhookService
{
    public const string MoonNotSet = "moon_not_set";
    public const string LowFuel = "low_fuel";
    public const string NewContract = "new_contract";
    public const string AcceptedContract = "accepted_contract";
    public const string IndustryReady = "industry_ready";
    public const string OmegaRenewal = "omega_renewal";
    public const string GlisteningOre = "glistening_ore";
    public const string PiCollection = "pi_collection";
    public const string PiFactoryRefill = "pi_factory_refill";
    public const string PiExtractorIdle = "pi_extractor_idle";
    public const string MoonReportManual = "moon_report_manual";
    public const string MoonReportDaily = "moon_report_daily";
    public const string MoonReportWeekly = "moon_report_weekly";
    public const string MoonReportMonthly = "moon_report_monthly";

    public static readonly (string Key, string Label)[] AlertChoices =
    {
        (MoonNotSet, "Moon drill: no extraction scheduled"),
        (LowFuel, "Structures: low fuel"),
        (NewContract, "New corporation contract"),
        (AcceptedContract, "Corporation contract accepted"),
        (IndustryReady, "Industry job ready"),
        (OmegaRenewal, "Omega renewal reminder"),
        (GlisteningOre, "Glistening ore detected"),
        (PiCollection, "Planetary: T1 collection at 40,500 / 45,000 m3 (per pilot)"),
        (PiFactoryRefill, "Planetary: factory-world inputs need refilling (grouped by pilot)"),
        (PiExtractorIdle, "Planetary: extraction inactive for over 5 hours (grouped by pilot)"),
        (MoonReportManual, "Manual moon operations report"),
        (MoonReportDaily, "Daily moon operations report (yesterday)"),
        (MoonReportWeekly, "Weekly moon operations report (previous week)"),
        (MoonReportMonthly, "Monthly moon operations report (previous month)")
    };

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Regex WebhookPath = new(
        @"^/api(?:/v\d+)?/webhooks/\d+/[A-Za-z0-9_\-]+/?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _settingsFile;
    private readonly string _deliveredFile;
    private readonly List<DiscordWebhookDestination> _destinations;
    private readonly Dictionary<string, DateTimeOffset> _delivered;

    public static DiscordWebhookService Current { get; } = new();

    public DiscordWebhookService(string? directory = null)
    {
        var folder = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center");
        _settingsFile = Path.Combine(folder, "discord-webhooks.json");
        _deliveredFile = Path.Combine(folder, "discord-delivered.json");
        try { _destinations = JsonSerializer.Deserialize<List<DiscordWebhookDestination>>(File.ReadAllText(_settingsFile)) ?? new(); }
        catch { _destinations = new(); }
        try { _delivered = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_deliveredFile)) ?? new(); }
        catch { _delivered = new(); }
    }

    public IReadOnlyList<DiscordWebhookDestination> Destinations()
    {
        lock (_lock)
            return _destinations.Select(x => new DiscordWebhookDestination
            {
                Id = x.Id, Name = x.Name, Channel = x.Channel,
                Enabled = x.Enabled, AlertTypes = (x.AlertTypes ?? new()).ToList(),
                ProtectedUrl = "" // Never expose encrypted or plaintext secrets to the view.
            }).ToList();
    }

    public bool HasRoutes(string type)
    {
        lock (_lock) return _destinations.Any(d => d.Enabled &&
            d.ProtectedUrl.Length > 0 && (d.AlertTypes?.Contains(type, StringComparer.Ordinal) ?? false));
    }

    public static bool IsValidWebhookUrl(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url)) return false;
        if (url.Scheme != Uri.UriSchemeHttps || url.Port != 443 ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) return false;
        if (url.Host != "discord.com" && url.Host != "discordapp.com" &&
            url.Host != "canary.discord.com" && url.Host != "ptb.discord.com") return false;
        return WebhookPath.IsMatch(url.AbsolutePath);
    }

    public void SaveDestination(string id, string name, string channel, string webhookUrl,
        bool enabled, IEnumerable<string> alertTypes)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a destination name.");
        var types = alertTypes.Intersect(AlertChoices.Select(x => x.Key), StringComparer.Ordinal).Distinct().ToList();
        lock (_lock)
        {
            var row = _destinations.FirstOrDefault(x => x.Id == id);
            if (row == null && string.IsNullOrWhiteSpace(webhookUrl))
                throw new ArgumentException("Paste a Discord webhook URL for a new destination.");
            string? cipher = null;
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                if (!IsValidWebhookUrl(webhookUrl))
                    throw new ArgumentException("Use a complete https://discord.com/api/webhooks/... URL.");
                cipher = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(webhookUrl.Trim()), null, DataProtectionScope.CurrentUser));
            }
            if (row == null)
            {
                row = new DiscordWebhookDestination { Id = Guid.NewGuid().ToString("N") };
                _destinations.Add(row);
            }
            row.Name = name.Trim();
            row.Channel = channel.Trim();
            row.Enabled = enabled;
            row.AlertTypes = types;
            if (cipher != null)
            {
                row.ProtectedUrl = cipher;
                ForgetDestinationNoLock(row.Id); // New URL should receive currently-active alerts.
            }
            SaveAtomic(_settingsFile, _destinations);
        }
    }

    public void RemoveDestination(string id)
    {
        lock (_lock)
        {
            _destinations.RemoveAll(x => x.Id == id);
            ForgetDestinationNoLock(id);
            SaveAtomic(_settingsFile, _destinations);
        }
    }

    public void ReconcileActive(string type, IEnumerable<string> activeEventKeys)
    {
        if (type != MoonNotSet && type != LowFuel && type != PiCollection &&
            type != PiFactoryRefill && type != PiExtractorIdle) return;
        var active = activeEventKeys.Select(Hash).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            bool changed = false;
            foreach (var destination in _destinations)
            {
                var prefix = destination.Id + "|" + type + "|";
                foreach (var key in _delivered.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                {
                    if (active.Contains(key.Substring(prefix.Length))) continue;
                    _delivered.Remove(key);
                    changed = true;
                }
            }
            if (changed) SaveAtomic(_deliveredFile, _delivered);
        }
    }

    /// <summary>Fire-and-forget, never blocks the UI or the ESI refresh cycle.</summary>
    public void Publish(string type, string title, string message, string eventKey)
    {
        if (!AlertChoices.Any(x => x.Key == type)) return;
        List<(string Id, string Cipher)> routes;
        lock (_lock)
        {
            routes = _destinations.Where(x => x.Enabled &&
                (x.AlertTypes?.Contains(type, StringComparer.Ordinal) ?? false) && x.ProtectedUrl.Length > 0)
                .Select(x => (x.Id, x.ProtectedUrl)).ToList();
        }
        foreach (var route in routes)
            _ = Task.Run(async () =>
            {
                try { await SendOnceAsync(route.Id, route.Cipher, type, title, message, eventKey); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Discord] Delivery failed: " + ex.GetType().Name); }
            });
    }

    public async Task<string> TestAsync(string id)
    {
        string cipher;
        lock (_lock)
        {
            cipher = _destinations.FirstOrDefault(x => x.Id == id)?.ProtectedUrl
                ?? throw new InvalidOperationException("Save the destination before testing it.");
        }
        return await PostAsync(cipher, "EVE Command Center test",
            "Discord notifications are connected. No live EVE alert was triggered.");
    }

    private async Task SendOnceAsync(string destinationId, string cipher,
        string type, string title, string message, string eventKey)
    {
        await _sendLock.WaitAsync();
        try
        {
            var key = destinationId + "|" + type + "|" + Hash(eventKey);
            lock (_lock)
            {
                if (_delivered.ContainsKey(key)) return;
                // Destination can be removed/disabled while queued.
                if (!_destinations.Any(x => x.Id == destinationId && x.Enabled &&
                    x.ProtectedUrl == cipher && (x.AlertTypes?.Contains(type) ?? false))) return;
            }
            await PostAsync(cipher, title, message);
            lock (_lock)
            {
                _delivered[key] = DateTimeOffset.UtcNow;
                foreach (var old in _delivered.Where(x => x.Value < DateTimeOffset.UtcNow.AddDays(-90)).Select(x => x.Key).ToArray())
                    _delivered.Remove(old);
                SaveAtomic(_deliveredFile, _delivered);
            }
        }
        finally { _sendLock.Release(); }
    }

    private static async Task<string> PostAsync(string cipher, string title, string message)
    {
        var webhookUrl = Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        if (!IsValidWebhookUrl(webhookUrl)) throw new InvalidOperationException("Invalid stored Discord webhook.");
        var body = JsonSerializer.Serialize(new
        {
            username = "EVE Command Center",
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[] { new
            {
                title = Limit(title, 256), description = Limit(message, 4096),
                color = 5617618, timestamp = DateTimeOffset.UtcNow.ToString("O")
            } }
        });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero :
                    delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Discord rejected the message (HTTP " + (int)response.StatusCode + ").");
            return "Discord accepted the message.";
        }
        throw new InvalidOperationException("Discord rate limit was not cleared.");
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value.Substring(0, max - 1) + "…";
    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private void ForgetDestinationNoLock(string id)
    {
        foreach (var key in _delivered.Keys.Where(x => x.StartsWith(id + "|", StringComparison.Ordinal)).ToArray())
            _delivered.Remove(key);
        SaveAtomic(_deliveredFile, _delivered);
    }
    private static void SaveAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
}
