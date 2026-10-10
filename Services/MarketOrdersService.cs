using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Latest observed orders only. Historical transactions/attribution will use the future shared ledger.
public sealed class MarketOrdersService
{
    public const string Scope = "esi-markets.read_character_orders.v1";
    private readonly string _path;
    private readonly HttpClient _http;
    private readonly Func<EvePilotProfile, CancellationToken, Task<string>> _token;
    public MarketOrdersState State { get; private set; } = new();
    public string Error { get; private set; } = "";
    public bool Busy { get; private set; }
    public MarketOrdersService(EveSsoService sso, string? directory = null, HttpClient? http = null,
        Func<EvePilotProfile, CancellationToken, Task<string>>? token = null)
    {
        _http = http ?? EsiHttp.CreateClient(); _token = token ?? sso.GetAccessTokenForAsync;
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "market-orders.json");
        try
        {
            if (File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                if (!doc.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() != 1) throw new InvalidDataException("Unsupported market cache schema");
                var loaded = doc.RootElement.Deserialize<MarketOrdersState>() ?? throw new InvalidDataException("Empty market cache");
                foreach (var c in loaded.Characters) Validate(c.Orders);
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
                    data.Orders = rows; data.Updated = DateTimeOffset.UtcNow; data.Error = "";
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
