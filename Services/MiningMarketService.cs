using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>
/// Lightweight public-ESI price/volume lookup for resources seen in EVE mining logs.
/// No character authentication is required. Quotes are cached to avoid hammering ESI.
/// </summary>
public sealed class MiningMarketService
{
    public const long Jita44StationId = 60003760;
    public const int TheForgeRegionId = 10000002;
    public const long AmarrEmperorFamilyStationId = 60008494;
    public const int DomainRegionId = 10000043;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan QuoteTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ErrorRetryTtl = TimeSpan.FromSeconds(30);
    private const string EsiCompatibilityDate = "2026-08-18";

    private readonly ConcurrentDictionary<string, MiningMarketQuote> _quotes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<MiningMarketQuote?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EVE-MultiPreview-Mining", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Compatibility-Date", EsiCompatibilityDate);
        return client;
    }

    public bool TryGetQuote(string oreName, out MiningMarketQuote quote)
    {
        quote = default!;
        if (string.IsNullOrWhiteSpace(oreName)) return false;
        return _quotes.TryGetValue(oreName.Trim(), out quote!);
    }

    public Task<MiningMarketQuote?> EnsureQuoteAsync(string oreName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oreName))
            return Task.FromResult<MiningMarketQuote?>(null);

        oreName = oreName.Trim();
        if (_quotes.TryGetValue(oreName, out var existing))
        {
            var age = DateTime.UtcNow - existing.FetchedAtUtc;
            var ttl = string.IsNullOrEmpty(existing.Error) ? QuoteTtl : ErrorRetryTtl;
            if (age < ttl)
                return Task.FromResult<MiningMarketQuote?>(existing);
        }

        return _inFlight.GetOrAdd(oreName, key => FetchAndStoreAsync(key, cancellationToken));
    }

    private async Task<MiningMarketQuote?> FetchAndStoreAsync(string oreName, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveTypeAsync(oreName, cancellationToken).ConfigureAwait(false);
            if (resolved == null)
            {
                var missing = MiningMarketQuote.Unavailable(oreName, "ESI could not resolve this resource name.");
                _quotes[oreName] = missing;
                return missing;
            }

            var (typeId, canonicalName) = resolved.Value;
            var volumeTask = FetchTypeVolumeAsync(typeId, cancellationToken);
            var jitaTask = FetchStationPricesAsync(TheForgeRegionId, Jita44StationId, typeId, cancellationToken);
            var amarrTask = FetchStationPricesAsync(DomainRegionId, AmarrEmperorFamilyStationId, typeId, cancellationToken);

            await Task.WhenAll(volumeTask, jitaTask, amarrTask).ConfigureAwait(false);

            var volume = await volumeTask.ConfigureAwait(false);
            var jita = await jitaTask.ConfigureAwait(false);
            var amarr = await amarrTask.ConfigureAwait(false);

            var quote = new MiningMarketQuote
            {
                OreName = canonicalName,
                TypeId = typeId,
                UnitVolumeM3 = volume,
                JitaBestSell = jita.BestSell,
                JitaBestBuy = jita.BestBuy,
                AmarrBestSell = amarr.BestSell,
                AmarrBestBuy = amarr.BestBuy,
                FetchedAtUtc = DateTime.UtcNow,
                Error = null
            };
            _quotes[oreName] = quote;
            if (!oreName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase))
                _quotes[canonicalName] = quote;
            return quote;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MiningMarket] Quote error for '{oreName}': {ex.Message}");
            var failed = MiningMarketQuote.Unavailable(oreName, ex.Message);
            _quotes[oreName] = failed;
            return failed;
        }
        finally
        {
            _inFlight.TryRemove(oreName, out _);
        }
    }

    private static async Task<(int TypeId, string Name)?> ResolveTypeAsync(string name, CancellationToken cancellationToken)
    {
        string url = "https://esi.evetech.net/universe/ids/?datasource=tranquility&language=en";
        string jsonName = JsonSerializer.Serialize(new[] { name });
        using var content = new StringContent(jsonName, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("inventory_types", out var types) || types.GetArrayLength() == 0)
            return null;

        foreach (var t in types.EnumerateArray())
        {
            int id = t.GetProperty("id").GetInt32();
            string returnedName = t.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
            if (returnedName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (id, returnedName);
        }

        var first = types[0];
        return (first.GetProperty("id").GetInt32(), first.GetProperty("name").GetString() ?? name);
    }

    private static async Task<double> FetchTypeVolumeAsync(int typeId, CancellationToken cancellationToken)
    {
        string url = $"https://esi.evetech.net/universe/types/{typeId}/?datasource=tranquility&language=en";
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("volume", out var volume) && volume.TryGetDouble(out double result))
            return result;
        return 0;
    }

    private static async Task<(double? BestSell, double? BestBuy)> FetchStationPricesAsync(
        int regionId, long stationId, int typeId, CancellationToken cancellationToken)
    {
        double? bestSell = null;
        double? bestBuy = null;
        int pages = 1;

        for (int page = 1; page <= pages && page <= 25; page++)
        {
            string url = $"https://esi.evetech.net/markets/{regionId}/orders/?datasource=tranquility&order_type=all&page={page}&type_id={typeId}";
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (page == 1 && response.Headers.TryGetValues("X-Pages", out var headerValues))
            {
                var value = headerValues.FirstOrDefault();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    pages = Math.Max(1, parsed);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var order in doc.RootElement.EnumerateArray())
            {
                if (!order.TryGetProperty("location_id", out var loc) || loc.GetInt64() != stationId)
                    continue;

                bool isBuy = order.TryGetProperty("is_buy_order", out var buyEl) && buyEl.GetBoolean();
                if (!order.TryGetProperty("price", out var priceEl) || !priceEl.TryGetDouble(out double price))
                    continue;

                if (isBuy)
                {
                    if (bestBuy == null || price > bestBuy.Value) bestBuy = price;
                }
                else
                {
                    if (bestSell == null || price < bestSell.Value) bestSell = price;
                }
            }
        }

        return (bestSell, bestBuy);
    }
}

public sealed record MiningMarketQuote
{
    public string OreName { get; init; } = "";
    public int TypeId { get; init; }
    public double UnitVolumeM3 { get; init; }
    public double? JitaBestSell { get; init; }
    public double? JitaBestBuy { get; init; }
    public double? AmarrBestSell { get; init; }
    public double? AmarrBestBuy { get; init; }
    public DateTime FetchedAtUtc { get; init; }
    public string? Error { get; init; }

    public bool IsAvailable => TypeId > 0 && UnitVolumeM3 > 0 && string.IsNullOrEmpty(Error);

    public static MiningMarketQuote Unavailable(string oreName, string error) => new()
    {
        OreName = oreName,
        FetchedAtUtc = DateTime.UtcNow,
        Error = error
    };
}
