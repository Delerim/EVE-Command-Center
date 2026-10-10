using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Latest observations, including orders no longer returned. Not a fill/history or financial ledger.
public sealed class MarketOrdersService
{
    public const string Scope = "esi-markets.read_character_orders.v1";
    private readonly string _path;
    private readonly EveSsoService _sso;
    private readonly bool _resolveNames;
    private readonly HttpClient _http;
    private readonly Func<EvePilotProfile, CancellationToken, Task<string>> _token;
    public MarketOrdersState State { get; private set; } = new();
    public string Error { get; private set; } = "";
    public bool Busy { get; private set; }
    public MarketOrdersService(EveSsoService sso, string? directory = null, HttpClient? http = null,
        Func<EvePilotProfile, CancellationToken, Task<string>>? token = null)
    {
        _sso = sso; _resolveNames = http == null;
        _http = http ?? EsiHttp.CreateClient(); _token = token ?? sso.GetAccessTokenForAsync;
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "market-orders.json");
        try
        {
            if (File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                if (!doc.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() != 1) throw new InvalidDataException("Unsupported market cache schema");
                var loaded = doc.RootElement.Deserialize<MarketOrdersState>() ?? throw new InvalidDataException("Empty market cache");
                foreach (var c in loaded.Characters)
                {
                    Validate(c.Orders); Validate(c.Unobserved.Select(o => o.Order).ToList());
                    if (c.Unobserved.Any(o => o.LastSeen == default || o.MissingSince < o.LastSeen || o.Order.Corporation || c.Orders.Any(a => a.OrderId == o.Order.OrderId))) throw new InvalidDataException("Invalid order observation");
                }
                State = loaded;
            }
        }
        catch (Exception ex) { Error = "Market cache preserved; loading failed: " + ex.GetType().Name; }
    }
    private void Persist(MarketOrdersState next)
    {
        if (Error.Length > 0) throw new InvalidOperationException(Error);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, next); file.Flush(true); }
        if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak"); else File.Move(temp, _path);
        State = next;
    }
    private MarketOrdersState Copy() => JsonSerializer.Deserialize<MarketOrdersState>(JsonSerializer.Serialize(State))!;
    public void Select(IEnumerable<long> ids)
    {
        if (Busy) throw new InvalidOperationException("Wait for the current market refresh.");
        var next = Copy(); next.SelectedCharacters = ids.Where(id => id > 0).ToHashSet(); Persist(next);
    }
    internal static void Validate(List<MarketOrder> rows)
    {
        if (rows.Select(r => r.OrderId).Distinct().Count() != rows.Count || rows.Any(r => r.OrderId <= 0 || r.TypeId <= 0 || r.LocationId <= 0 ||
            r.Price < 0 || r.Total <= 0 || r.Remaining < 0 || r.Remaining > r.Total || r.Duration < 1 || r.Duration > 365 || r.Issued == default))
            throw new InvalidDataException("Invalid order snapshot");
        foreach (var row in rows) { _ = row.RemainingValue; _ = row.Expiry; }
    }
    public async Task RefreshAsync(IReadOnlyList<EvePilotProfile> linked, CancellationToken ct)
    {
        if (Busy || Error.Length > 0) return;
        Busy = true;
        try
        {
            foreach (long id in State.SelectedCharacters.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var next = Copy();
                var data = next.Characters.FirstOrDefault(c => c.CharacterId == id);
                if (data == null) { data = new() { CharacterId = id }; next.Characters.Add(data); }
                var pilot = linked.FirstOrDefault(p => p.CharacterId == id);
                if (pilot == null || !pilot.Scopes.Contains(Scope))
                { data.Error = pilot == null ? "Character no longer linked; cached orders retained" : "Missing market-order permission; reconnect this toon in Settings"; Persist(next); continue; }
                data.Name = pilot.CharacterName;
                if (DateTimeOffset.UtcNow < data.NextRefresh) continue;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/characters/{id}/orders");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(pilot, ct));
                    request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
                    using var response = await _http.SendAsync(request, ct);
                    response.EnsureSuccessStatusCode();
                    var rows = JsonSerializer.Deserialize<List<MarketOrder>>(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException("Empty response");
                    Validate(rows);
                    foreach (var row in rows)
                    {
                        row.ResolvedName = data.Orders.Concat(data.Unobserved.Select(o => o.Order)).FirstOrDefault(o => o.TypeId == row.TypeId)?.ResolvedName;
                        row.ResolvedLocation = data.Orders.Concat(data.Unobserved.Select(o => o.Order)).FirstOrDefault(o => o.LocationId == row.LocationId)?.ResolvedLocation;
                    }
                    if (_resolveNames)
                    {
                        try
                        {
                            var names = await _sso.ResolveIndustryTypeNamesAsync(rows.Select(r => r.TypeId), ct);
                            var locations = new Dictionary<long, string>();
                            foreach (long location in rows.Select(r => r.LocationId).Distinct())
                                if (await _sso.ResolveDisplayLocationAsync(location, pilot, ct) is {} name) locations[location] = name;
                            foreach (var row in rows)
                            {
                                row.ResolvedName = names.GetValueOrDefault(row.TypeId) is {} name && !name.StartsWith("Type ") && name != "Item name unavailable" ? name : data.Orders.Concat(data.Unobserved.Select(o => o.Order)).FirstOrDefault(o => o.TypeId == row.TypeId)?.ResolvedName;
                                row.ResolvedLocation = locations.GetValueOrDefault(row.LocationId) ?? data.Orders.Concat(data.Unobserved.Select(o => o.Order)).FirstOrDefault(o => o.LocationId == row.LocationId)?.ResolvedLocation;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { EsiDiagnostics.Write("Market names deferred: " + ex.GetType().Name); }
                    }
                    var observedAt = DateTimeOffset.UtcNow;
                    var returned = rows.Select(o => o.OrderId).ToHashSet();
                    data.Unobserved.RemoveAll(o => returned.Contains(o.Order.OrderId));
                    var retained = data.Unobserved.Select(o => o.Order.OrderId).ToHashSet();
                    foreach (var old in data.Orders.Where(o => !o.Corporation && !returned.Contains(o.OrderId)))
                        if (retained.Add(old.OrderId)) data.Unobserved.Add(new() { Order = old, LastSeen = data.Updated, MissingSince = observedAt });
                    data.Orders = rows; data.Updated = observedAt; data.Error = "";
                    var ttl = response.Headers.CacheControl?.MaxAge ?? TimeSpan.FromMinutes(20);
                    var age = response.Headers.Age ?? TimeSpan.Zero;
                    data.NextRefresh = data.Updated.Add(ttl > age ? ttl - age : TimeSpan.FromMinutes(1));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                { data.Error = "Refresh failed: " + ex.GetType().Name + "; cached orders retained"; data.NextRefresh = DateTimeOffset.UtcNow.AddMinutes(1); }
                Persist(next);
            }
        }
        finally { Busy = false; }
    }
}
