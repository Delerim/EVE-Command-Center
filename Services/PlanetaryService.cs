using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class PlanetaryService
{
    public const string Scope = "esi-planets.manage_planets.v1";
    private readonly EveSsoService _sso;
    private readonly HttpClient _http = EsiHttp.CreateClient();
    private readonly string _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "planetary.json");
    public PiState State { get; private set; }
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Saved snapshots; refresh checks every 30 minutes.";
    public event Action? Changed;
    public PlanetaryService(EveSsoService sso)
    {
        _sso = sso;
        try { State = JsonSerializer.Deserialize<PiState>(File.ReadAllText(_file)) ?? new(); }
        catch { State = new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(State));
        File.Move(_file + ".tmp", _file, true);
    }
    private void Update(string status) { Status = status; Changed?.Invoke(); }
    private async Task<JsonElement> Read(string path, string? token, CancellationToken ct, HttpMethod? method = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, "https://esi.evetech.net/latest" + path);
        request.Headers.UserAgent.ParseAdd("EVE-Command-Center/3.0.0");
        request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.Clone();
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Busy || DateTimeOffset.UtcNow < State.NextRefresh) return;
        Busy = true;
        State.NextRefresh = DateTimeOffset.UtcNow.AddMinutes(30);
        try
        {
            var pilots = await _sso.LoadPilotsAsync();
            var stockPilot = pilots.FirstOrDefault(p => p.CharacterId == State.StockCharacterId);
            if (stockPilot != null)
            {
                try { Update("Reading stockpile for " + stockPilot.CharacterName); await LoadStock(stockPilot, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { State.StockError = "Stockpile refresh delayed: " + ex.Message; }
            }
            foreach (var pilot in pilots)
            {
                ct.ThrowIfCancellationRequested();
                if (!pilot.Scopes.Contains(Scope)) { State.PilotStatus[pilot.CharacterId] = "Link PI permission to read colonies"; continue; }
                try
                {
                    Update("Loading colonies: " + pilot.CharacterName);
                    string token = await _sso.GetAccessTokenForAsync(pilot, ct);
                    var colonies = await Read($"/characters/{pilot.CharacterId}/planets/", token, ct);
                    var ids = colonies.EnumerateArray().Select(c => c.GetProperty("planet_id").GetInt64()).ToHashSet();
                    State.Colonies.RemoveAll(c => c.CharacterId == pilot.CharacterId && !ids.Contains(c.PlanetId));
                    State.PilotStatus[pilot.CharacterId] = $"{ids.Count} colonies linked";
                    foreach (var colony in colonies.EnumerateArray())
                    {
                        long id = colony.GetProperty("planet_id").GetInt64();
                        var previous = State.Colonies.FirstOrDefault(c => c.CharacterId == pilot.CharacterId && c.PlanetId == id);
                        try
                        {
                            var layout = await Read($"/characters/{pilot.CharacterId}/planets/{id}/", token, ct);
                            string name = previous?.Planet ?? "Planet " + id;
                            if (previous == null)
                                try { name = (await Read($"/universe/planets/{id}/", null, ct)).GetProperty("name").GetString() ?? name; }
                                catch (HttpRequestException) { }
                            var next = new PiColony { CharacterId = pilot.CharacterId, Character = pilot.CharacterName, PlanetId = id, Planet = name,
                                PlanetType = colony.GetProperty("planet_type").GetString() ?? "", LastUpdate = colony.GetProperty("last_update").GetDateTimeOffset(), Fetched = DateTimeOffset.UtcNow, Layout = layout };
                            if (previous != null) State.Colonies.Remove(previous);
                            State.Colonies.Add(next);
                            Save(); Changed?.Invoke();
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { if (previous != null) previous.Error = "Refresh delayed: " + ex.Message; State.PilotStatus[pilot.CharacterId] = "Some colony data unavailable: " + ex.Message; }
                    }
                    if (!State.PilotStatus.GetValueOrDefault(pilot.CharacterId, "").StartsWith("Some colony")) State.PilotStatus[pilot.CharacterId] = $"{ids.Count} colonies linked";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { State.PilotStatus[pilot.CharacterId] = "PI refresh delayed: " + ex.Message; }
                Save(); Changed?.Invoke();
            }
            State.Colonies.RemoveAll(c => !pilots.Any(p => p.CharacterId == c.CharacterId));
            Update("PI refresh finished. Next check " + State.NextRefresh.ToLocalTime().ToString("HH:mm") + ". Timers update locally; colony amounts may need an in-game visit.");
        }
        catch (OperationCanceledException) { Update("PI refresh cancelled; last snapshots retained."); }
        catch (Exception ex) { Update("PI refresh delayed: " + ex.Message); }
        finally { Busy = false; Save(); Changed?.Invoke(); }
    }
    private async Task LoadStock(EvePilotProfile pilot, CancellationToken ct)
    {
        string token = await _sso.GetAccessTokenForAsync(pilot, ct);
        var assets = await _sso.GetAssetsForPlanningAsync(pilot, ct);
        var parents = assets.Select(a => a.LocationId).ToHashSet();
        var candidates = assets.Where(a => a.IsSingleton && parents.Contains(a.ItemId) && a.LocationType == "station").ToArray();
        var containers = new List<PiContainer>();
        foreach (var batch in candidates.Chunk(500))
        {
            var names = await Read($"/characters/{pilot.CharacterId}/assets/names/", token, ct, HttpMethod.Post, batch.Select(a => a.ItemId).ToArray());
            foreach (var name in names.EnumerateArray())
            {
                long id = name.GetProperty("item_id").GetInt64();
                var item = batch.First(a => a.ItemId == id);
                containers.Add(new() { Id = id, Station = item.LocationId, Name = name.GetProperty("name").GetString() ?? "Container " + id });
            }
        }
        State.StockError = ""; State.Assets = assets.ToList(); State.Containers = containers; State.StockFetched = DateTimeOffset.UtcNow;
        Save(); Changed?.Invoke();
    }
    public void SelectStock(long character, long container)
    {
        if (State.StockCharacterId != character) { State.Assets.Clear(); State.Containers.Clear(); State.StockFetched = default; State.ContainerId = 0; State.NextRefresh = default; }
        State.StockCharacterId = character; State.ContainerId = container;
        Save(); Changed?.Invoke();
    }
}
