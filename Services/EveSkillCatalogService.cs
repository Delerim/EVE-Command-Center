using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Builds a complete published EVE skill catalogue from public ESI.
///
/// Category 16 is the EVE "Skill" category. Each group's types are resolved
/// through /universe/types/{type_id}; dogma attribute 275 is skillTimeConstant,
/// i.e. the training rank multiplier.
///
/// This is intentionally separate from character SSO data. The catalogue is
/// shared by every pilot and cached under PilotData so the expensive discovery
/// only happens on the first run (or after the cache ages out).
/// </summary>
public sealed class EveSkillCatalogService
{
    private const string EsiBase = "https://esi.evetech.net/latest";
    private const int SkillCategoryId = 16;
    private const int SkillTimeConstantAttributeId = 275;
    private const int PrimaryAttributeDogmaId = 180;
    private const int SecondaryAttributeDogmaId = 181;
    private const int CacheSchemaVersion = 2;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private IReadOnlyList<EveSkillCatalogEntry>? _memoryCache;

    public EveSkillCatalogService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EVE-Command-Center/0.2 (+https://github.com/Delerim/EVE-MultiPreview)");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Compatibility-Date", "2026-08-25");

        _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center",
            "PilotData");

        Directory.CreateDirectory(root);
        _cacheFile = Path.Combine(root, "skill-catalog.json");
    }

    public async Task<IReadOnlyList<EveSkillCatalogEntry>> GetCatalogAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_memoryCache != null)
            return _memoryCache;

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache != null)
                return _memoryCache;

            EveSkillCatalogCache? diskCache =
                await TryLoadCacheAsync(cancellationToken);

            if (diskCache != null &&
                diskCache.SchemaVersion == CacheSchemaVersion &&
                diskCache.Entries.Count > 100 &&
                diskCache.Entries.All(
                    e => e.PrimaryAttributeId > 0 &&
                         e.SecondaryAttributeId > 0) &&
                DateTime.UtcNow - diskCache.GeneratedUtc < CacheLifetime)
            {
                _memoryCache = diskCache.Entries
                    .OrderBy(e => e.GroupName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                progress?.Report(
                    $"Skill catalogue loaded • {_memoryCache.Count:N0} skills");
                return _memoryCache;
            }

            progress?.Report("Discovering EVE skill groups...");

            EveUniverseCategory category =
                await GetPublicAsync<EveUniverseCategory>(
                    $"/universe/categories/{SkillCategoryId}/",
                    cancellationToken);

            var groups = new List<EveUniverseGroup>();

            foreach (int groupId in category.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EveUniverseGroup group =
                    await GetPublicAsync<EveUniverseGroup>(
                        $"/universe/groups/{groupId}/",
                        cancellationToken);

                if (group.Published && group.Types.Count > 0)
                    groups.Add(group);
            }

            int totalTypes = groups.Sum(g => g.Types.Count);
            int completed = 0;

            progress?.Report(
                $"Building skill catalogue • 0 / {totalTypes:N0}");

            var entries = new List<EveSkillCatalogEntry>();
            using var concurrency = new SemaphoreSlim(8);

            var jobs = groups
                .SelectMany(group =>
                    group.Types.Select(typeId => (group, typeId)))
                .Select(async item =>
                {
                    await concurrency.WaitAsync(cancellationToken);
                    try
                    {
                        EveUniverseType? type = null;

                        try
                        {
                            type = await GetPublicAsync<EveUniverseType>(
                                $"/universe/types/{item.typeId}/",
                                cancellationToken);
                        }
                        catch (Exception ex)
                            when (ex is not OperationCanceledException)
                        {
                            Debug.WriteLine(
                                $"[SkillCatalog] Type {item.typeId}: {ex.Message}");
                        }

                        if (type == null || !type.Published)
                            return (EveSkillCatalogEntry?)null;

                        EveDogmaAttributeValue? rankAttribute =
                            type.DogmaAttributes.FirstOrDefault(
                                a => a.AttributeId ==
                                     SkillTimeConstantAttributeId);

                        int rank = rankAttribute == null
                            ? 1
                            : Math.Max(
                                1,
                                (int)Math.Round(
                                    rankAttribute.Value,
                                    MidpointRounding.AwayFromZero));

                        int primaryAttributeId =
                            (int)Math.Round(
                                type.DogmaAttributes
                                    .FirstOrDefault(
                                        a => a.AttributeId ==
                                             PrimaryAttributeDogmaId)
                                    ?.Value ?? 0);

                        int secondaryAttributeId =
                            (int)Math.Round(
                                type.DogmaAttributes
                                    .FirstOrDefault(
                                        a => a.AttributeId ==
                                             SecondaryAttributeDogmaId)
                                    ?.Value ?? 0);

                        return new EveSkillCatalogEntry
                        {
                            SkillId = item.typeId,
                            Name = string.IsNullOrWhiteSpace(type.Name)
                                ? $"Skill {item.typeId}"
                                : type.Name,
                            GroupId = item.group.GroupId,
                            GroupName = item.group.Name,
                            Rank = rank,
                            MaxSp = 256000L * rank,
                            PrimaryAttributeId = primaryAttributeId,
                            SecondaryAttributeId = secondaryAttributeId
                        };
                    }
                    finally
                    {
                        int done = Interlocked.Increment(ref completed);

                        if (done == totalTypes || done % 25 == 0)
                        {
                            progress?.Report(
                                $"Building skill catalogue • " +
                                $"{done:N0} / {totalTypes:N0}");
                        }

                        concurrency.Release();
                    }
                })
                .ToArray();

            EveSkillCatalogEntry?[] resolved =
                await Task.WhenAll(jobs);

            entries.AddRange(
                resolved
                    .Where(e => e != null)
                    .Select(e => e!)
                    .OrderBy(
                        e => e.GroupName,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(
                        e => e.Name,
                        StringComparer.OrdinalIgnoreCase));

            if (entries.Count < 100)
            {
                throw new InvalidOperationException(
                    "The EVE skill catalogue was unexpectedly incomplete. " +
                    $"Only {entries.Count:N0} published skills were resolved.");
            }

            var cache = new EveSkillCatalogCache
            {
                SchemaVersion = CacheSchemaVersion,
                GeneratedUtc = DateTime.UtcNow,
                Entries = entries
            };

            try
            {
                string json =
                    JsonSerializer.Serialize(cache, _json);
                await File.WriteAllTextAsync(
                    _cacheFile,
                    json,
                    cancellationToken);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                Debug.WriteLine(
                    $"[SkillCatalog] Cache write failed: {ex.Message}");
            }

            _memoryCache = entries.ToArray();

            progress?.Report(
                $"Skill catalogue ready • {_memoryCache.Count:N0} skills");

            return _memoryCache;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<EveSkillCatalogCache?> TryLoadCacheAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_cacheFile))
            return null;

        try
        {
            string json =
                await File.ReadAllTextAsync(
                    _cacheFile,
                    cancellationToken);

            return JsonSerializer.Deserialize<EveSkillCatalogCache>(
                json,
                _json);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            Debug.WriteLine(
                $"[SkillCatalog] Cache read failed: {ex.Message}");
            return null;
        }
    }

    private async Task<T> GetPublicAsync<T>(
        string relativePath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        EsiBase + relativePath);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue(
                        "application/json"));

                using HttpResponseMessage response =
                    await _http.SendAsync(
                        request,
                        cancellationToken);

                string json =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return JsonSerializer.Deserialize<T>(
                               json,
                               _json)
                           ?? throw new InvalidOperationException(
                               $"ESI returned no data for {relativePath}.");
                }

                int status = (int)response.StatusCode;

                if (status is 420 or 429 or 502 or 503 or 504)
                {
                    lastError = new InvalidOperationException(
                        $"ESI {relativePath} returned HTTP {status}.");

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            750 * (attempt + 1)),
                        cancellationToken);

                    continue;
                }

                string detail = string.IsNullOrWhiteSpace(json)
                    ? ""
                    : " - " + (json.Length > 400
                        ? json[..400] + "..."
                        : json);

                throw new InvalidOperationException(
                    $"ESI {relativePath} failed: " +
                    $"{status} {response.ReasonPhrase}{detail}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                if (attempt >= 3)
                    break;

                await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        500 * (attempt + 1)),
                    cancellationToken);
            }
        }

        throw lastError ??
              new InvalidOperationException(
                  $"ESI {relativePath} failed.");
    }
}
