using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace EveCommandCenter.Services;

public sealed class AbyssalWatchSettings
{
    public bool Enabled { get; set; } = true;
    public bool WatchOreStripMiner { get; set; } = true;
    public bool WatchModulatedStripMiner { get; set; } = true;
    public double MinAskingPrice { get; set; } = 400_000_000;
    public double MinEstimatedValue { get; set; }
    public double MinMiningAmount { get; set; } = 140;
    public double MinEffectiveSpeed { get; set; } = 3.30;
    public double MaxCpuPenalty { get; set; } = 2;
    public double MaxPowergridPenalty { get; set; } = 2;
    public bool SingleItemContractsOnly { get; set; } = true;
    public int PollMinutes { get; set; } = 5;
}

public sealed record AbyssalWatchListing(
    long ItemId, long ContractId, string Source, string Slug,
    double AskingPrice, double? EstimatedValue, double MiningAmount,
    double EffectiveSpeed, double CpuPenalty, double PowergridPenalty,
    int AbyssalModules, int OtherItems)
{
    public string Key => ContractId.ToString(CultureInfo.InvariantCulture) + ":" + ItemId.ToString(CultureInfo.InvariantCulture);
    public string Url => "https://mutamarket.com/modules/" + Uri.EscapeDataString(Slug);
    public string PriceText => (AskingPrice / 1_000_000).ToString("N0", CultureInfo.InvariantCulture) + "M ISK";
    public string EstimateText => EstimatedValue.HasValue ? (EstimatedValue.Value / 1_000_000).ToString("N0", CultureInfo.InvariantCulture) + "M" : "Unknown";
    public string QualityText => $"{MiningAmount:0.##} m³ | {EffectiveSpeed:0.00} m³/s | CPU {CpuPenalty:+0.##;-0.##;0} | PG {PowergridPenalty:+0.##;-0.##;0}";
    public string MutaplasmidName { get; init; } = "Unknown mutaplasmid";
    public double CycleSeconds { get; init; }
    public double CriticalChancePercent { get; init; }
    public double CriticalBonusFactor { get; init; }
    public double? AverageRoll { get; init; }
    public string? ExpiryUtc { get; init; }
    public bool IsExpired => DateTimeOffset.TryParse(ExpiryUtc, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var expiry) && expiry <= DateTimeOffset.UtcNow;
    public string ExpiryText => DateTimeOffset.TryParse(ExpiryUtc, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var expiry)
        ? (expiry <= DateTimeOffset.UtcNow ? "EXPIRED" : expiry.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)) : "Unknown";
    public string MiningAmountText => MiningAmount.ToString("0.00", CultureInfo.InvariantCulture);
    public string CycleText => CycleSeconds > 0 ? CycleSeconds.ToString("0.00", CultureInfo.InvariantCulture) : "-";
    public string EffectiveSpeedText => EffectiveSpeed.ToString("0.00", CultureInfo.InvariantCulture);
    public string CpuDeltaText => CpuPenalty.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    public string PowergridDeltaText => PowergridPenalty.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    public string CritText => $"{CriticalChancePercent:0.##}% ×{CriticalBonusFactor:0.##}";
    public string PriceAdvantageText => EstimatedValue is > 0 && AskingPrice > 0
        ? ((EstimatedValue.Value - AskingPrice) / AskingPrice * 100).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + "%"
        : "n/a";
}

// Public MutaMarket JSON endpoints; no EVE scopes or private SSO credentials.
// Polling discovers items after MutaMarket publishes them, not at EVE contract creation time.
public sealed class AbyssalWatchService : IDisposable
{
    private const string Base = "https://mutamarket.com/api/modules/type/";
    private static readonly HttpClient Http = CreateClient();
    public static AbyssalWatchService Instance { get; } = new();
    private readonly DispatcherTimer _timer = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _seenOrder = new();
    private bool _initialized;
    private bool _busy;
    private bool _disposed;
    private CancellationTokenSource _life = new();
    public AbyssalWatchSettings Options { get; private set; } = new();
    public IReadOnlyList<AbyssalWatchListing> Latest { get; private set; } = Array.Empty<AbyssalWatchListing>();
    public IReadOnlyList<AbyssalWatchListing> AllListings { get; private set; } = Array.Empty<AbyssalWatchListing>();
    public IReadOnlyList<AbyssalWatchListing> RecentAlerts { get; private set; } = Array.Empty<AbyssalWatchListing>();
    private readonly Dictionary<string, double> _lastPrices = new(StringComparer.Ordinal);
    public DateTimeOffset? LastChecked { get; private set; }
    public string LastError { get; private set; } = "Not checked yet";
    public event Action? Updated;
    public event Action<IReadOnlyList<AbyssalWatchListing>>? NewDeals;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EVE Command Center", "abyssal-market-watch.json");

    private sealed class Persisted
    {
        public AbyssalWatchSettings Options { get; set; } = new();
        public bool Initialized { get; set; }
        public List<string> Seen { get; set; } = new();
        public Dictionary<string, double> LastPrices { get; set; } = new();
        public List<AbyssalWatchListing> Alerts { get; set; } = new();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EVECommandCenter/3.6.100 (+https://github.com/Delerim/EVE-Command-Center)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public AbyssalWatchService()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var record = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(StorePath));
                if (record != null)
                {
                    Options = record.Options ?? new();
                    _initialized = record.Initialized;
                    foreach (var key in record.Seen ?? new()) Remember(key);
                    foreach (var pair in record.LastPrices ?? new()) _lastPrices[pair.Key] = pair.Value;
                    RecentAlerts = (record.Alerts ?? new()).TakeLast(100).ToArray();
                }
            }
        }
        catch (Exception ex) { LastError = "Settings could not be read: " + ex.Message; }
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(Options.PollMinutes, 2, 30));
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Save(AbyssalWatchSettings options)
    {
        Options = options;
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(options.PollMinutes, 2, 30));
        Persist();
        Updated?.Invoke();
    }

    private void Remember(string key)
    {
        if (!_seen.Add(key)) return;
        _seenOrder.Add(key);
        while (_seenOrder.Count > 4_000)
        {
            _seen.Remove(_seenOrder[0]);
            _seenOrder.RemoveAt(0);
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            string temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Persisted {
                Options = Options, Initialized = _initialized, Seen = _seenOrder.ToList(),
                LastPrices = _lastPrices, Alerts = RecentAlerts.TakeLast(100).ToList()
            }));
            File.Move(temp, StorePath, true);
        }
        catch (Exception ex) { LastError = "Could not save watch history: " + ex.Message; }
    }

    public async Task RefreshAsync(bool includeMorePages = false)
    {
        if (_disposed || _busy || !Options.Enabled) return;
        _busy = true;
        try
        {
            var listings = new List<AbyssalWatchListing>();
            if (Options.WatchOreStripMiner) listings.AddRange(await ReadAsync(90493, _life.Token, includeMorePages ? 3 : 1));
            if (Options.WatchModulatedStripMiner) listings.AddRange(await ReadAsync(90467, _life.Token, includeMorePages ? 3 : 1));
            if (_disposed) return;
            AllListings = listings.GroupBy(x => x.Key).Select(g => g.First()).ToArray();
            Latest = AllListings.Where(item => Matches(item, Options)).ToArray();
            // A changed asking price is a fresh opportunity even for an old listing.
            // Only notify on a meaningful (>=10%) drop, never a tiny market edit.
            var newDeals = _initialized && !includeMorePages ? Latest.Where(item => !_seen.Contains(item.Key) ||
                _lastPrices.TryGetValue(item.Key, out double previous) && previous > 0 &&
                item.AskingPrice > 0 && item.AskingPrice <= previous * 0.90).ToArray()
                : Array.Empty<AbyssalWatchListing>();
            // Remember ALL observed entries: changing filters cannot re-alert old listings.
            foreach (var item in AllListings) { Remember(item.Key); _lastPrices[item.Key] = item.AskingPrice; }
            while (_lastPrices.Count > 4_000) {
                var oldest = _lastPrices.Keys.First(); _lastPrices.Remove(oldest);
            }
            if (newDeals.Length > 0)
                RecentAlerts = RecentAlerts.Concat(newDeals).TakeLast(100).ToArray();
            _initialized = true; LastChecked = DateTimeOffset.UtcNow; LastError = "";
            Persist();
            Updated?.Invoke();
            if (newDeals.Length > 0) NewDeals?.Invoke(newDeals);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = "MutaMarket feed: " + ex.Message; Updated?.Invoke(); }
        finally { _busy = false; }
    }

    private static async Task<IReadOnlyList<AbyssalWatchListing>> ReadAsync(int type, CancellationToken token, int maxPages)
    {
        // Background checks use one newest-first page; manual browsing can fetch
        // three cursor pages. All URLs remain on the documented public endpoint.
        string baseUrl = Base + type.ToString(CultureInfo.InvariantCulture) + "/contracts-only/sort/date-added/desc";
        string? cursor = null;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var collected = new List<AbyssalWatchListing>();
        for (int page = 0; page < Math.Clamp(maxPages, 1, 3); page++)
        {
            string url = cursor == null ? baseUrl : baseUrl + "?cursor=" + Uri.EscapeDataString(cursor);
            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(token);
            collected.AddRange(ParsePage(json, type));
            cursor = ParseNextCursor(json);
            if (string.IsNullOrWhiteSpace(cursor) || !used.Add(cursor)) break;
        }
        return collected;
    }

    internal static string? ParseNextCursor(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("next_cursor", out var next) || next.ValueKind != JsonValueKind.String) return null;
        string? cursor = next.GetString();
        return string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 ? null : cursor;
    }

    internal static IReadOnlyList<AbyssalWatchListing> ParsePage(string json, int type)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MutaMarket returned no module data array.");
        var result = new List<AbyssalWatchListing>();
        foreach (var module in data.EnumerateArray())
        {
            if (!module.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.Object) continue;
            if (!module.TryGetProperty("source_type", out var source) || source.ValueKind != JsonValueKind.Object) continue;
            string model = Str(source, "name");
            if (type == 90493 && model != "ORE Strip Miner" || type == 90467 && model != "Modulated Strip Miner II") continue;
            double cid = Num(contract, "id");
            long contractId = double.IsFinite(cid) ? (long)cid : 0;
            double iid = Num(module, "id");
            long itemId = double.IsFinite(iid) ? (long)iid : 0;
            string slug = Str(module, "slug");
            if (contractId <= 0 || itemId <= 0 || slug.Length == 0 || !slug.All(c => char.IsLetterOrDigit(c) || c == '-')) continue;
            double amount = Attr(module, "miningAmount");
            double duration = Attr(module, "duration");
            if (duration > 1000) duration /= 1000;
            double chance = Attr(module, "miningCritChance");
            if (Math.Abs(chance) <= .1) chance *= 100;
            double bonus = Attr(module, "miningCritBonusYield");
            if (Math.Abs(bonus) > 10) bonus /= 100;
            double speed = duration > 0 ? amount / duration * (1 + (chance / 100) * bonus) : 0;
            var estimated = Num(module, "estimated_value");
            string mutaplasmidName = module.TryGetProperty("mutaplasmid", out var plasma) &&
                plasma.ValueKind == JsonValueKind.Object ? Str(plasma, "name") : "";
            result.Add(new AbyssalWatchListing(itemId, contractId, model, slug,
                Num(contract, "price"), estimated > 0 ? estimated : null, amount, speed,
                Attr(module, "cpu") - AttrBase(module, "cpu"),
                Attr(module, "power") - AttrBase(module, "power"),
                (int)Num(contract, "abyssal_modules_count"), (int)Num(contract, "non_abyssal_modules_count"))
            {
                MutaplasmidName = string.IsNullOrWhiteSpace(mutaplasmidName) ? "Unknown" : mutaplasmidName,
                CycleSeconds = duration, CriticalChancePercent = chance, CriticalBonusFactor = bonus,
                ExpiryUtc = Str(contract, "date_expired"),
                AverageRoll = double.IsFinite(Num(module, "average_fraction")) ? Num(module, "average_fraction") : null
            });
        }
        return result;
    }

    private static double Attr(JsonElement module, string name) => FindAttribute(module, name, "value");
    private static double AttrBase(JsonElement module, string name) => FindAttribute(module, name, "base_value");
    private static double FindAttribute(JsonElement module, string name, string key)
    {
        if (!module.TryGetProperty("mutated_attributes", out var a) || a.ValueKind != JsonValueKind.Array) return double.NaN;
        foreach (var attribute in a.EnumerateArray())
            if (Str(attribute, "name") == name) return Num(attribute, key);
        return double.NaN;
    }
    private static double Num(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var v)) return double.NaN;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double number) ? number : double.NaN;
    }
    private static string Str(JsonElement item, string field) =>
        item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool Matches(AbyssalWatchListing item, AbyssalWatchSettings options) =>
        !item.IsExpired && double.IsFinite(item.AskingPrice) && item.AskingPrice > 0 && item.AskingPrice >= options.MinAskingPrice &&
        (options.MinEstimatedValue <= 0 || item.EstimatedValue >= options.MinEstimatedValue) &&
        double.IsFinite(item.MiningAmount) && item.MiningAmount >= options.MinMiningAmount &&
        double.IsFinite(item.EffectiveSpeed) && item.EffectiveSpeed >= options.MinEffectiveSpeed &&
        double.IsFinite(item.CpuPenalty) && item.CpuPenalty <= options.MaxCpuPenalty &&
        double.IsFinite(item.PowergridPenalty) && item.PowergridPenalty <= options.MaxPowergridPenalty &&
        (!options.SingleItemContractsOnly || item.AbyssalModules == 1 && item.OtherItems == 0);

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _life.Cancel(); _life.Dispose();
        Persist();
    }
}
