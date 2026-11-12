using System.Net.Http;
using System.Text.Json;
using System.Text;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public interface IOreStashSource
{
    Task<IReadOnlyList<EvePilotProfile>> PilotsAsync();
    Task<OreAssetSnapshot> AssetsAsync(EvePilotProfile pilot, CancellationToken ct);
    Task<IReadOnlyDictionary<int, OreWatchType>> OreTypesAsync(IEnumerable<int> ownedTypes, CancellationToken ct);
    Task<OreOrderBook> BookAsync(int typeId, CancellationToken ct);
    Task<string?> LocationAsync(long locationId, EvePilotProfile pilot, CancellationToken ct);
}

public sealed class OreStashSource(EveSsoService sso, HttpClient? http = null) : IOreStashSource, IDisposable
{
    private readonly HttpClient _http = http ?? CreateClient();
    private HashSet<int>? _oreIds;
    private readonly Dictionary<int, OreWatchType> _types = new();
    private readonly Dictionary<int, OreOrderBook> _books = new();
    private static HttpClient CreateClient()
    {
        var client = EsiHttp.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EVECommandCenter-OreStash/3.8.12");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
        return client;
    }
    public Task<IReadOnlyList<EvePilotProfile>> PilotsAsync() => sso.LoadPilotsAsync();
    public Task<OreAssetSnapshot> AssetsAsync(EvePilotProfile pilot, CancellationToken ct) => sso.GetOreWatchAssetsAsync(pilot, ct);
    public Task<string?> LocationAsync(long id, EvePilotProfile pilot, CancellationToken ct) => sso.ResolveDisplayLocationAsync(id, pilot, ct);
    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync("https://esi.evetech.net/latest" + path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
    public async Task<IReadOnlyDictionary<int, OreWatchType>> OreTypesAsync(IEnumerable<int> ownedTypes, CancellationToken ct)
    {
        if (_oreIds == null)
        {
            // Resolve the authoritative Asteroid category once, rather than requesting
            // metadata for every module, blueprint and ship in all personal hangars.
            using var category = await GetAsync("/universe/categories/25/", ct).ConfigureAwait(false);
            var ids = new HashSet<int>();
            foreach (var group in category.RootElement.GetProperty("groups").EnumerateArray())
            {
                using var detail = await GetAsync($"/universe/groups/{group.GetInt32()}/", ct).ConfigureAwait(false);
                if (detail.RootElement.GetProperty("category_id").GetInt32() != 25)
                    throw new InvalidOperationException("Ore catalogue category changed; inventory cannot be classified completely.");
                foreach (var type in detail.RootElement.GetProperty("types").EnumerateArray()) ids.Add(type.GetInt32());
            }
            _oreIds = ids;
        }
        foreach (int typeId in ownedTypes.Distinct().Where(_oreIds.Contains))
        {
            if (!_types.TryGetValue(typeId, out var info))
            {
                using var type = await GetAsync($"/universe/types/{typeId}/", ct).ConfigureAwait(false);
                string name = type.RootElement.GetProperty("name").GetString() ?? throw new InvalidOperationException("Ore type name unavailable.");
                info = new(typeId, name);
            }
            if (info.Name.StartsWith("Compressed ", StringComparison.OrdinalIgnoreCase)) info = info with { MarketTypeId = typeId, MarketName = info.Name };
            else if (info.MarketTypeId == 0 && !info.Name.StartsWith("Batch Compressed ", StringComparison.OrdinalIgnoreCase))
            {
                // Modern ore/ice compression preserves unit count (CCP 20.03).
                // Match only an exact modern compressed type in the ore category;
                // legacy batch compression is never treated as one-to-one.
                using var content = new StringContent(JsonSerializer.Serialize(new[] { "Compressed " + info.Name }), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync("https://esi.evetech.net/latest/universe/ids/?language=en", content, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { _types[typeId] = info; continue; }
                response.EnsureSuccessStatusCode();
                using var names = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (names.RootElement.TryGetProperty("inventory_types", out var matches))
                    foreach (var match in matches.EnumerateArray())
                    {
                        int id = match.GetProperty("id").GetInt32();
                        string name = match.GetProperty("name").GetString() ?? "";
                        if (_oreIds.Contains(id) && name.Equals("Compressed " + info.Name, StringComparison.OrdinalIgnoreCase))
                            info = info with { MarketTypeId = id, MarketName = name };
                    }
            }
            _types[typeId] = info;
        }
        return _types;
    }
    public async Task<OreOrderBook> BookAsync(int typeId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_books.TryGetValue(typeId, out var cached) && cached.ExpiresUtc > now && cached.ObservedUtc >= now.AddMinutes(-20)) return cached;
        var bids = new List<OreBookOrder>(); var asks = new List<OreBookOrder>();
        DateTimeOffset observed = now, expires = now.AddMinutes(5);
        int pages = 1;
        for (int page = 1; page <= pages; page++)
        {
            using var response = await _http.GetAsync($"https://esi.evetech.net/latest/markets/10000002/orders/?order_type=all&type_id={typeId}&page={page}", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            int count = 1;
            if (response.Headers.TryGetValues("X-Pages", out var values) && (!int.TryParse(values.FirstOrDefault(), out count) || count < 1 || count > 25))
                throw new InvalidOperationException("Market order book is incomplete.");
            if (page == 1) pages = count;
            else if (count != pages) throw new InvalidOperationException("Market pages changed during the scan.");
            var received = DateTimeOffset.UtcNow;
            var dated = response.Content.Headers.LastModified ?? (response.Headers.Date ?? received - (response.Headers.Age ?? TimeSpan.Zero));
            observed = dated < observed ? dated : observed;
            var until = response.Content.Headers.Expires ?? (response.Headers.Date ?? received).Add(response.Headers.CacheControl?.MaxAge ?? TimeSpan.FromMinutes(5));
            expires = until < expires ? until : expires;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("type_id", out var id) && id.GetInt32() != typeId)
                    throw new InvalidOperationException("Market response belongs to a different item type.");
                if (item.GetProperty("location_id").GetInt64() != MiningMarketService.Jita44StationId) continue;
                decimal price = item.GetProperty("price").GetDecimal();
                long volume = item.GetProperty("volume_remain").GetInt64();
                long minimum = item.TryGetProperty("min_volume", out var min) ? min.GetInt64() : 1;
                if (price <= 0 || volume <= 0 || minimum < 1) throw new InvalidOperationException("Invalid market order data.");
                (item.GetProperty("is_buy_order").GetBoolean() ? bids : asks).Add(new(price, volume, minimum));
            }
        }
        var book = new OreOrderBook(typeId, bids, asks, observed, expires);
        _books[typeId] = book;
        return book;
    }
    public void Dispose() => _http.Dispose();
}
