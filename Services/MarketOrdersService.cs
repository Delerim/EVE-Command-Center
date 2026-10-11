using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Replaceable open-order observations plus explicit confirmed-history imports into the shared ledger.
public sealed partial class MarketOrdersService
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
    public string Progress { get; private set; } = "";
    private IndustryLedger Ledger => new(Path.GetDirectoryName(_path)!);
    public List<HistoricalOrderRecord> History { get; private set; } = new();
    public string HistoryStatus { get; private set; } = "History has not been refreshed.";
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
        try { if (Ledger.Exists) { History = Ledger.History(); HistoryStatus = "Saved confirmed history loaded."; Transactions = Ledger.Transactions(); TransactionStatus = "Saved personal transactions loaded."; Journal = Ledger.Journal(); JournalStatus = "Saved wallet activity loaded."; } }
        catch (Exception ex) { HistoryStatus = "History could not be loaded; data preserved: " + ex.GetType().Name; }
    }
    public async Task RefreshHistoryAsync(IReadOnlyList<EvePilotProfile> linked, CancellationToken ct)
    {
        if (Busy || Error.Length > 0) return;
        Busy = true; Progress = "Preparing history refresh..."; var messages = new List<string>();
        try
        {
            // Migration and complete-page commits run off the dispatcher. No partial-page checkpoint is committed.
            foreach (long id in State.SelectedCharacters.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var pilot = linked.FirstOrDefault(p => p.CharacterId == id);
                if (pilot == null || !pilot.Scopes.Contains(Scope))
                { messages.Add((pilot?.CharacterName ?? "Unlinked character") + ": market permission required; history retained."); continue; }
                try
                {
                    await Task.Run(() => Ledger.Ensure(), ct);
                    var due = Ledger.NextHistory(id);
                    if (DateTimeOffset.UtcNow < due) { messages.Add($"{pilot.CharacterName}: history cached until {due:HH:mm} UTC."); continue; }
                    var token = await _token(pilot, ct);
                    var orders = new List<HistoricalMarketOrder>();
                    int pages = 1; DateTimeOffset observed = DateTimeOffset.UtcNow;
                    DateTimeOffset next = observed;
                    for (int page = 1; page <= pages; page++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/characters/{id}/orders/history?page={page}");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
                        Progress = $"{pilot.CharacterName}: waiting for history page {page} through the EVE request queue...";
                        using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
                        if (!response.Headers.TryGetValues("X-Pages", out var values) || !int.TryParse(values.Single(), out int count) || count < 1 || count > 10000)
                            throw new InvalidDataException("Missing or invalid history page count.");
                        if (page > 1 && count != pages) throw new InvalidDataException("History changed during pagination; retry later.");
                        pages = count;
                        var rows = JsonSerializer.Deserialize<List<HistoricalMarketOrder>>(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException("Empty history response.");
                        orders.AddRange(rows);
                        var pageDue = RefreshDue(response, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
                        if (pageDue > next) next = pageDue;
                    }
                    Validate(orders.Cast<MarketOrder>().ToList());
                    if (orders.Any(o => o.State is not ("cancelled" or "expired"))) throw new InvalidDataException("Unsupported order state.");
                    var cached = State.Characters.SelectMany(c => c.Orders.Concat(c.Unobserved.Select(u => u.Order))).Concat(History.Select(h => (MarketOrder)h.Order)).ToArray();
                    foreach (var row in orders)
                    { row.ResolvedName = cached.FirstOrDefault(o => o.TypeId == row.TypeId)?.ResolvedName; row.ResolvedLocation = cached.FirstOrDefault(o => o.LocationId == row.LocationId)?.ResolvedLocation; }
                    if (_resolveNames)
                    {
                        Progress = "Data received. Looking up item and station names...";
                        await ResolveOptionalNames(async metadataCt =>
                        {
                            var names = await _sso.ResolveIndustryTypeNamesAsync(orders.Select(o => o.TypeId), metadataCt);
                            foreach (var row in orders)
                                if (names.GetValueOrDefault(row.TypeId) is {} name && !name.StartsWith("Type ") && name != "Item name unavailable") row.ResolvedName = name;
                            foreach (var location in orders.Select(o => o.LocationId).Distinct())
                                if (await _sso.ResolveDisplayLocationAsync(location, pilot, metadataCt) is {} name)
                                    foreach (var row in orders.Where(o => o.LocationId == location)) row.ResolvedLocation = name;
                        }, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    await Task.Run(() => Ledger.ImportHistory(id, orders, observed, next), ct);
                    messages.Add($"{pilot.CharacterName}: {orders.Count(o => !o.Corporation):N0} confirmed history records refreshed.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { messages.Add($"{pilot.CharacterName}: {Failure(ex)} History retained."); }
            }
            if (Ledger.Exists) History = await Task.Run(() => Ledger.History(), ct);
        }
        finally { HistoryStatus = string.Join("\n", messages); Busy = false; }
    }
    internal static async Task ResolveOptionalNames(Func<CancellationToken, Task> resolve, CancellationToken ct, TimeSpan? budget = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget ?? TimeSpan.FromSeconds(15));
        try { await resolve(timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { EsiDiagnostics.Write("Optional market names deferred: " + ex.GetType().Name); }
    }
    internal static string DisplayError(string error) => error.Contains("HttpRequestException", StringComparison.Ordinal)
        ? "Previous refresh failed. Refresh orders to try again." : error;
    internal static string Failure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "EVE rejected the session (401). Refresh again; reconnect the pilot if it persists.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "EVE denied access (403). Reconnect this pilot with the required permission, then refresh.",
        HttpRequestException { StatusCode: (System.Net.HttpStatusCode)420 or System.Net.HttpStatusCode.TooManyRequests } => "EVE is limiting requests. Wait for the retry window.",
        HttpRequestException h when h.StatusCode.HasValue => $"EVE request failed (HTTP {(int)h.StatusCode.Value}). Try again shortly.",
        HttpRequestException => "Could not reach EVE. Check the connection and try again.",
        OperationCanceledException => "The request timed out. Try again shortly.",
        InvalidDataException or JsonException => "EVE returned incomplete or unexpected data. Try again shortly.",
        _ => "Refresh could not finish. Saved data has been preserved."
    };
    internal static DateTimeOffset RefreshDue(HttpResponseMessage response, DateTimeOffset now, TimeSpan fallback)
    {
        if (response.Content.Headers.Expires is {} expires) return expires > now ? expires : now.AddMinutes(1);
        var ttl = (response.Headers.CacheControl?.MaxAge ?? fallback) - (response.Headers.Age ?? TimeSpan.Zero);
        return now.Add(ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(1));
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
            r.Price < 0 || r.Total <= 0 || r.Remaining < 0 || r.Remaining > r.Total || r.Duration < 0 || r.Duration > 365 || r.Issued == default))
            throw new InvalidDataException("Invalid order snapshot");
        foreach (var row in rows) { _ = row.RemainingValue; _ = row.Expiry; }
    }
    public async Task RefreshAsync(IReadOnlyList<EvePilotProfile> linked, CancellationToken ct)
    {
        if (Busy || Error.Length > 0) return;
        Busy = true; Progress = "Preparing open orders...";
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
                    Progress = $"{pilot.CharacterName}: waiting for open orders through the EVE request queue...";
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
                        Progress = "Data received. Looking up item and station names...";
                        await ResolveOptionalNames(async metadataCt =>
                        {
                            var names = await _sso.ResolveIndustryTypeNamesAsync(rows.Select(r => r.TypeId), metadataCt);
                            foreach (var row in rows)
                                if (names.GetValueOrDefault(row.TypeId) is {} name && !name.StartsWith("Type ") && name != "Item name unavailable") row.ResolvedName = name;
                            foreach (long location in rows.Select(r => r.LocationId).Distinct())
                                if (await _sso.ResolveDisplayLocationAsync(location, pilot, metadataCt) is {} name)
                                    foreach (var row in rows.Where(r => r.LocationId == location)) row.ResolvedLocation = name;
                        }, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    var observedAt = DateTimeOffset.UtcNow;
                    var returned = rows.Select(o => o.OrderId).ToHashSet();
                    data.Unobserved.RemoveAll(o => returned.Contains(o.Order.OrderId));
                    var retained = data.Unobserved.Select(o => o.Order.OrderId).ToHashSet();
                    foreach (var old in data.Orders.Where(o => !o.Corporation && !returned.Contains(o.OrderId)))
                        if (retained.Add(old.OrderId)) data.Unobserved.Add(new() { Order = old, LastSeen = data.Updated, MissingSince = observedAt });
                    data.Orders = rows; data.Updated = observedAt; data.Error = "";
                    data.NextRefresh = RefreshDue(response, data.Updated, TimeSpan.FromMinutes(20));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                { data.Error = "Refresh failed: " + Failure(ex) + "; cached orders retained"; data.NextRefresh = DateTimeOffset.UtcNow.AddMinutes(1); }
                Persist(next);
            }
        }
        finally { Busy = false; }
    }
}
