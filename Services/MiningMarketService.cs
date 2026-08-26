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

    public const int GlobalPlexMarketRegionId = 19000001;
    public const int PlexTypeId = 44992;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan QuoteTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ErrorRetryTtl = TimeSpan.FromSeconds(30);
    private const string EsiCompatibilityDate = "2026-08-18";

    private readonly ConcurrentDictionary<string, MiningMarketQuote> _quotes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<MiningMarketQuote?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan HistoryTtl = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, MiningMarketHistory> _history =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<MiningMarketHistory?>> _historyInFlight =
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

    public bool TryGetHistory(string oreName, out MiningMarketHistory history)
    {
        history = default!;
        if (string.IsNullOrWhiteSpace(oreName)) return false;
        return _history.TryGetValue(oreName.Trim(), out history!);
    }

    public async Task<MiningMarketHistory?> EnsureHistoryAsync(
        string oreName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oreName))
            return null;

        oreName = oreName.Trim();

        if (_history.TryGetValue(oreName, out var existing))
        {
            var age = DateTime.UtcNow - existing.FetchedAtUtc;
            var ttl = string.IsNullOrEmpty(existing.Error)
                ? HistoryTtl
                : ErrorRetryTtl;

            if (age < ttl)
                return existing;
        }

        var quote = await EnsureQuoteAsync(
            oreName, cancellationToken).ConfigureAwait(false);

        if (quote == null || quote.TypeId <= 0)
        {
            var unavailable = MiningMarketHistory.Unavailable(
                oreName,
                quote?.TypeId ?? 0,
                quote?.Error ?? "Unable to resolve EVE type.");

            _history[oreName] = unavailable;
            return unavailable;
        }

        return await _historyInFlight.GetOrAdd(
            oreName,
            key => FetchHistoryAndStoreAsync(
                key, quote, cancellationToken)).ConfigureAwait(false);
    }

    public MiningMarketTimingSignal GetTimingSignal(
        string oreName,
        string market,
        string priceMode)
    {
        if (!TryGetQuote(oreName, out var quote) ||
            !quote.IsAvailable ||
            !TryGetHistory(oreName, out var history) ||
            !history.IsAvailable)
        {
            return MiningMarketTimingSignal.Unavailable(
                market,
                "Loading market history...");
        }

        bool amarr = market.Equals(
            "Amarr", StringComparison.OrdinalIgnoreCase);
        bool buy = priceMode.Equals(
            "buy", StringComparison.OrdinalIgnoreCase);

        double current = amarr
            ? ((buy ? quote.AmarrBestBuy : quote.AmarrBestSell) ?? 0)
            : ((buy ? quote.JitaBestBuy : quote.JitaBestSell) ?? 0);

        if (current <= 0)
            return MiningMarketTimingSignal.Unavailable(
                market,
                "No current station price.");

        var source = amarr ? history.Domain : history.TheForge;

        var valid = source
            .Where(d => d.Average > 0)
            .OrderBy(d => d.DateUtc)
            .ToList();

        if (valid.Count < 7)
            return MiningMarketTimingSignal.Unavailable(
                market,
                "Not enough ESI history yet.");

        DateTime today = DateTime.UtcNow.Date;

        var d7 = valid.Where(
            d => d.DateUtc >= today.AddDays(-7)).ToList();
        var d30 = valid.Where(
            d => d.DateUtc >= today.AddDays(-30)).ToList();
        var d90 = valid.Where(
            d => d.DateUtc >= today.AddDays(-90)).ToList();

        if (d30.Count == 0)
            return MiningMarketTimingSignal.Unavailable(
                market,
                "No recent ESI history.");

        double avg7 = WeightedAverage(d7);
        double avg30 = WeightedAverage(d30);
        double low90 = d90.Count > 0
            ? d90.Min(d => d.Average)
            : avg30;
        double high90 = d90.Count > 0
            ? d90.Max(d => d.Average)
            : avg30;

        double vs30Pct = avg30 > 0
            ? (current / avg30 - 1.0) * 100.0
            : 0;

        double trendPct = avg30 > 0 && avg7 > 0
            ? (avg7 / avg30 - 1.0) * 100.0
            : 0;

        double rangePosition = high90 > low90
            ? Math.Clamp(
                (current - low90) / (high90 - low90),
                0,
                1)
            : 0.5;

        string signal;

        // Conservative historical-position heuristic, not a price prediction.
        if (rangePosition >= 0.82 || vs30Pct >= 8.0)
            signal = "SELL";
        else if (rangePosition <= 0.22 || vs30Pct <= -8.0)
            signal = "HOLD";
        else if (trendPct >= 4.0 && vs30Pct < 2.0)
            signal = "HOLD";
        else if (trendPct <= -4.0 && vs30Pct > 0)
            signal = "SELL";
        else
            signal = "WAIT";

        string reason =
            $"{FormatSigned(vs30Pct)} vs 30d avg; " +
            $"{rangePosition * 100.0:F0}% of 90d range; " +
            $"7d trend {FormatSigned(trendPct)}";

        return new MiningMarketTimingSignal
        {
            IsAvailable = true,
            Signal = signal,
            Market = amarr ? "Amarr" : "Jita",
            CurrentPrice = current,
            Average7 = avg7,
            Average30 = avg30,
            Low90 = low90,
            High90 = high90,
            Vs30Percent = vs30Pct,
            TrendPercent = trendPct,
            RangePositionPercent = rangePosition * 100.0,
            Reason = reason
        };
    }

    private async Task<MiningMarketHistory?> FetchHistoryAndStoreAsync(
        string oreName,
        MiningMarketQuote quote,
        CancellationToken cancellationToken)
    {
        try
        {
            var forgeTask = FetchRegionHistoryAsync(
                TheForgeRegionId,
                quote.TypeId,
                cancellationToken);

            var domainTask = FetchRegionHistoryAsync(
                DomainRegionId,
                quote.TypeId,
                cancellationToken);

            await Task.WhenAll(
                forgeTask, domainTask).ConfigureAwait(false);

            var result = new MiningMarketHistory
            {
                OreName = quote.OreName,
                TypeId = quote.TypeId,
                TheForge = await forgeTask.ConfigureAwait(false),
                Domain = await domainTask.ConfigureAwait(false),
                FetchedAtUtc = DateTime.UtcNow,
                Error = null
            };

            _history[oreName] = result;

            if (!oreName.Equals(
                    quote.OreName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _history[quote.OreName] = result;
            }

            return result;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MiningMarket] History error for '{oreName}': {ex.Message}");

            var failed = MiningMarketHistory.Unavailable(
                oreName,
                quote.TypeId,
                ex.Message);

            _history[oreName] = failed;
            return failed;
        }
        finally
        {
            _historyInFlight.TryRemove(oreName, out _);
        }
    }

    private static double WeightedAverage(
        IReadOnlyCollection<MiningMarketHistoryDay> days)
    {
        if (days.Count == 0) return 0;

        double weighted = 0;
        double volume = 0;

        foreach (var day in days)
        {
            double weight = Math.Max(1, day.Volume);
            weighted += day.Average * weight;
            volume += weight;
        }

        return volume > 0
            ? weighted / volume
            : days.Average(d => d.Average);
    }

    private static string FormatSigned(double value) =>
        value.ToString(
            "+0.0;-0.0;0.0",
            CultureInfo.InvariantCulture) + "%";
    private async Task<MiningMarketQuote?> FetchAndStoreAsync(string oreName, CancellationToken cancellationToken)
    {
        try
        {
            bool isPlex =
                oreName.Equals(
                    "PLEX",
                    StringComparison.OrdinalIgnoreCase);

            var resolved =
                isPlex
                    ? (TypeId: PlexTypeId, Name: "PLEX")
                    : await ResolveTypeAsync(
                        oreName,
                        cancellationToken).ConfigureAwait(false);

            if (resolved == null)
            {
                var missing = MiningMarketQuote.Unavailable(
                    oreName,
                    "ESI could not resolve this resource name.");

                _quotes[oreName] = missing;
                return missing;
            }

            var (rawTypeId, canonicalName) = resolved.Value;

            // EVE's mining log records the UNCOMPRESSED ore name, but in normal
            // mining operations the ore is compressed before it is sold. Since the
            // 2022 compression changes, asteroid/moon ore compression preserves the
            // unit count 1:1. Therefore one mined unit maps to one compressed market
            // unit, while its mined-space volume must still use the RAW ore volume.
            //
            // Pricing raw moon ore is especially dangerous because those order books
            // can be extremely thin. A single odd raw sell order can make today's
            // "PROFIT" jump by several hundred million without another mining cycle.
            int marketTypeId = rawTypeId;
            string marketItemName = canonicalName;
            bool usesCompressedMarket = false;

            if (!isPlex &&
                !canonicalName.StartsWith(
                    "Compressed ",
                    StringComparison.OrdinalIgnoreCase))
            {
                var compressed = await ResolveTypeAsync(
                    "Compressed " + canonicalName,
                    cancellationToken).ConfigureAwait(false);

                if (compressed != null)
                {
                    marketTypeId = compressed.Value.TypeId;
                    marketItemName = compressed.Value.Name;
                    usesCompressedMarket = true;
                }
            }

            // Raw volume is intentional. It is used for mined m3 and ISK/m3.
            var rawVolumeTask = FetchTypeVolumeAsync(
                rawTypeId,
                cancellationToken);

            Task<(double? BestSell, double? BestBuy)> jitaTask;
            Task<(double? BestSell, double? BestBuy)> amarrTask;

            if (isPlex)
            {
                // PLEX uses the global market introduced by CCP.
                // stationId 0 means "do not filter by location".
                jitaTask =
                    FetchStationPricesAsync(
                        GlobalPlexMarketRegionId,
                        0,
                        PlexTypeId,
                        cancellationToken);

                amarrTask =
                    Task.FromResult(
                        (
                            BestSell: (double?)null,
                            BestBuy: (double?)null
                        ));
            }
            else
            {
                jitaTask =
                    FetchStationPricesAsync(
                        TheForgeRegionId,
                        Jita44StationId,
                        marketTypeId,
                        cancellationToken);

                amarrTask =
                    FetchStationPricesAsync(
                        DomainRegionId,
                        AmarrEmperorFamilyStationId,
                        marketTypeId,
                        cancellationToken);
            }

            await Task.WhenAll(
                rawVolumeTask,
                jitaTask,
                amarrTask).ConfigureAwait(false);

            double rawVolume =
                await rawVolumeTask.ConfigureAwait(false);

            var jita = await jitaTask.ConfigureAwait(false);
            var amarr = await amarrTask.ConfigureAwait(false);

            // If ESI resolves a compressed variant but it has no station orders at
            // either selected hub, fall back to the raw type instead of returning a
            // blank quote. This mainly protects unusual/new resource types.
            bool compressedHasAnyPrice =
                jita.BestSell.HasValue ||
                jita.BestBuy.HasValue ||
                amarr.BestSell.HasValue ||
                amarr.BestBuy.HasValue;

            if (!isPlex &&
                usesCompressedMarket &&
                !compressedHasAnyPrice)
            {
                marketTypeId = rawTypeId;
                marketItemName = canonicalName;
                usesCompressedMarket = false;

                var rawJitaTask = FetchStationPricesAsync(
                    TheForgeRegionId,
                    Jita44StationId,
                    rawTypeId,
                    cancellationToken);

                var rawAmarrTask = FetchStationPricesAsync(
                    DomainRegionId,
                    AmarrEmperorFamilyStationId,
                    rawTypeId,
                    cancellationToken);

                await Task.WhenAll(
                    rawJitaTask,
                    rawAmarrTask).ConfigureAwait(false);

                jita = await rawJitaTask.ConfigureAwait(false);
                amarr = await rawAmarrTask.ConfigureAwait(false);
            }

            var quote = new MiningMarketQuote
            {
                // Keep the resource name exactly as the mining logs know it.
                OreName = canonicalName,

                // TypeId is the TRADED type. Historical market data therefore
                // follows the same compressed market used by current valuation.
                TypeId = marketTypeId,

                // The physical volume is still the raw mined unit volume.
                UnitVolumeM3 = rawVolume,

                JitaBestSell = jita.BestSell,
                JitaBestBuy = jita.BestBuy,
                AmarrBestSell = amarr.BestSell,
                AmarrBestBuy = amarr.BestBuy,
                MarketItemName = marketItemName,
                UsesCompressedMarket = usesCompressedMarket,
                FetchedAtUtc = DateTime.UtcNow,
                Error = null
            };

            Debug.WriteLine(
                $"[MiningMarket] '{canonicalName}' valued as " +
                $"'{marketItemName}' type={marketTypeId}, " +
                $"rawVolume={rawVolume:N3}m3, compressed={usesCompressedMarket}");

            _quotes[oreName] = quote;

            if (!oreName.Equals(
                    canonicalName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _quotes[canonicalName] = quote;
            }

            return quote;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MiningMarket] Quote error for '{oreName}': {ex.Message}");

            var failed =
                MiningMarketQuote.Unavailable(oreName, ex.Message);

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
                if (stationId > 0 &&
                    (
                        !order.TryGetProperty(
                            "location_id",
                            out var loc) ||
                        loc.GetInt64() != stationId
                    ))
                {
                    continue;
                }

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

    private static async Task<IReadOnlyList<MiningMarketHistoryDay>>
        FetchRegionHistoryAsync(
            int regionId,
            int typeId,
            CancellationToken cancellationToken)
    {
        string url =
            $"https://esi.evetech.net/markets/{regionId}/history/" +
            $"?datasource=tranquility&type_id={typeId}";

        using var response = await Http.GetAsync(
            url, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var doc = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        DateTime cutoff = DateTime.UtcNow.Date.AddDays(-365);
        var result = new List<MiningMarketHistoryDay>();

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("date", out var dateEl))
                continue;

            string? dateText = dateEl.GetString();

            if (!DateTime.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                    out var dateUtc))
            {
                continue;
            }

            if (dateUtc < cutoff)
                continue;

            result.Add(new MiningMarketHistoryDay
            {
                DateUtc = dateUtc,
                Average = ReadDouble(row, "average"),
                Highest = ReadDouble(row, "highest"),
                Lowest = ReadDouble(row, "lowest"),
                OrderCount = ReadInt(row, "order_count"),
                Volume = ReadLong(row, "volume")
            });
        }

        return result
            .OrderBy(d => d.DateUtc)
            .ToList();
    }

    private static double ReadDouble(JsonElement row, string property) =>
        row.TryGetProperty(property, out var el) &&
        el.TryGetDouble(out double value)
            ? value
            : 0;

    private static int ReadInt(JsonElement row, string property) =>
        row.TryGetProperty(property, out var el) &&
        el.TryGetInt32(out int value)
            ? value
            : 0;

    private static long ReadLong(JsonElement row, string property) =>
        row.TryGetProperty(property, out var el) &&
        el.TryGetInt64(out long value)
            ? value
            : 0;
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

    // The EVE log names the raw resource, while market valuation normally follows
    // its compressed 1:1 trading equivalent.
    public string MarketItemName { get; init; } = "";
    public bool UsesCompressedMarket { get; init; }

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

public sealed record MiningMarketHistoryDay
{
    public DateTime DateUtc { get; init; }
    public double Average { get; init; }
    public double Highest { get; init; }
    public double Lowest { get; init; }
    public int OrderCount { get; init; }
    public long Volume { get; init; }
}

public sealed record MiningMarketHistory
{
    public string OreName { get; init; } = "";
    public int TypeId { get; init; }

    public IReadOnlyList<MiningMarketHistoryDay> TheForge { get; init; } =
        Array.Empty<MiningMarketHistoryDay>();

    public IReadOnlyList<MiningMarketHistoryDay> Domain { get; init; } =
        Array.Empty<MiningMarketHistoryDay>();

    public DateTime FetchedAtUtc { get; init; }
    public string? Error { get; init; }

    public bool IsAvailable =>
        string.IsNullOrEmpty(Error) &&
        (TheForge.Count > 0 || Domain.Count > 0);

    public static MiningMarketHistory Unavailable(
        string oreName,
        int typeId,
        string error) => new()
    {
        OreName = oreName,
        TypeId = typeId,
        FetchedAtUtc = DateTime.UtcNow,
        Error = error
    };
}

public sealed record MiningMarketTimingSignal
{
    public bool IsAvailable { get; init; }
    public string Signal { get; init; } = "LOADING";
    public string Market { get; init; } = "";
    public double CurrentPrice { get; init; }
    public double Average7 { get; init; }
    public double Average30 { get; init; }
    public double Low90 { get; init; }
    public double High90 { get; init; }
    public double Vs30Percent { get; init; }
    public double TrendPercent { get; init; }
    public double RangePositionPercent { get; init; }
    public string Reason { get; init; } = "";

    public static MiningMarketTimingSignal Unavailable(
        string market,
        string reason) => new()
    {
        IsAvailable = false,
        Signal = "LOADING",
        Market = market,
        Reason = reason
    };
}
