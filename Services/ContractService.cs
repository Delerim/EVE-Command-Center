using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class ContractService : IDisposable
{
    private sealed class ThrottledException : Exception
    {
        public ThrottledException() : base("The data provider is rate limiting requests. Monitoring will retry later.") { }
    }
    public const string ReadScope = "esi-contracts.read_corporation_contracts.v1";
    public const string WindowScope = "esi-ui.open_window.v1";
    public const string StructureScope = "esi-universe.read_structures.v1";
    public static readonly string[] Scopes = { ReadScope, WindowScope, StructureScope };
    public static readonly string[] AllowedLocations = { "Raren - Ducks Migration", "Mazitah - Eagle One", "Joppaya IX - Moon 9 - Ardishapur Family Bureau" };
    public const long JoppayaStationId = 60008740;
    private readonly EveSsoService _sso;
    private readonly HttpClient _http;
    private readonly Func<EvePilotProfile, CancellationToken, Task<string>> _getAccessToken;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private readonly Dictionary<string, (DateTimeOffset Time, JsonElement Value)> _publicCache = new();
    private readonly Dictionary<(long Corp, long Id), IReadOnlyList<ContractItem>> _items = new();
    public ContractState State { get; private set; }
    public event Action? Changed;
    public event Action<IReadOnlyList<ContractRow>>? NewContracts;
    public event Action<IReadOnlyList<ContractRow>>? AcceptedContracts;
    public DateTimeOffset NextCheckUtc { get => State.NextRefreshUtc; private set => State.NextRefreshUtc = value; }
    public string? LastError { get; private set; }
    public bool IsRefreshing { get; private set; }

    public ContractService(EveSsoService sso, HttpClient? http = null, string? stateDirectory = null,
        Func<EvePilotProfile, CancellationToken, Task<string>>? getAccessToken = null)
    {
        _sso = sso;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _getAccessToken = getAccessToken ?? sso.GetAccessTokenForAsync;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EVE-Command-Center/2.5.0");
        _http.DefaultRequestHeaders.Add("X-Compatibility-Date", "2026-08-25");
        var dir = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "Contracts");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "contracts.json");
        try { State = JsonSerializer.Deserialize<ContractState>(File.ReadAllText(_file)) ?? new(); }
        catch { State = new(); }
    }

    public static bool CanRead(EvePilotProfile pilot) => pilot.Scopes.Contains(ReadScope);
    public void Save()
    {
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(State));
        File.Move(temp, _file, true);
    }

    public async Task RefreshAsync(EvePilotProfile pilot, CancellationToken token, bool respectCooldown = true)
    {
        if (!CanRead(pilot)) throw new InvalidOperationException("Reconnect this character to grant corporation contract access.");
        await _gate.WaitAsync(token);
        if (respectCooldown && DateTimeOffset.UtcNow < NextCheckUtc) { _gate.Release(); Changed?.Invoke(); return; }
        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            var access = await _getAccessToken(pilot, token);
            var character = await GetAsync($"/characters/{pilot.CharacterId}/", null, token);
            long corp = character.GetProperty("corporation_id").GetInt64();
            var corporation = await GetAsync($"/corporations/{corp}/", null, token);
            NextCheckUtc = DateTimeOffset.UtcNow.AddMinutes(30);
            var contracts = await PagesAsync<CorporationContract>($"/corporations/{corp}/contracts/", access, token);
            await ResolveEntitiesAsync(contracts.SelectMany(c => new[] { c.IssuerId, c.AcceptorId }), token);
            var rows = new List<ContractRow>();
            foreach (var contract in contracts.Where(c => c.Status == "outstanding" && c.AssigneeId == corp && c.Expires > DateTimeOffset.UtcNow).OrderBy(c => c.Expires))
            {
                token.ThrowIfCancellationRequested();
                var row = new ContractRow { Contract = contract, CorporationId = corp, ReaderCharacterId = pilot.CharacterId, JaniceUrl = ExtractJaniceUrl(contract.Title) };
                row.Issuer = EntityName(contract.IssuerId);
                string locationPath = contract.LocationId >= 1_000_000_000_000 ? $"/universe/structures/{contract.LocationId}/" : $"/universe/stations/{contract.LocationId}/";
                row.Location = await ResolveNameAsync(locationPath, contract.LocationId, access, token);
                JsonElement? appraisal = null;
                string? appraisalError = null;
                if (row.JaniceUrl != null)
                {
                    try { appraisal = await GetAppraisalAsync(row.JaniceUrl, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException and not ThrottledException) { appraisalError = "Janice unavailable: " + ex.Message; }
                }
                Evaluate(row, appraisal, State.BuyPercent, State.TolerancePercent, appraisalError);
                rows.Add(row);
            }
            var history = new List<ContractRow>();
            foreach (var contract in contracts)
            {
                var row = rows.FirstOrDefault(r => r.Contract.Id == contract.Id) ?? new ContractRow
                {
                    Contract = contract, CorporationId = corp, ReaderCharacterId = pilot.CharacterId,
                    Issuer = EntityName(contract.IssuerId),
                    JaniceUrl = ExtractJaniceUrl(contract.Title),
                    Location = State.History.FirstOrDefault(r => r.CorporationId == corp && r.Contract.Id == contract.Id)?.Location ?? contract.LocationId.ToString()
                };
                if (contract.AcceptorId > 0) row.Acceptor = EntityName(contract.AcceptorId);
                history.Add(row);
            }
            var accepted = RecordHistory(State, corp, history);
            var fresh = FindNew(State.SeenByCorporation, corp, rows);
            State.CharacterId = pilot.CharacterId;
            State.CorporationId = corp;
            State.CorporationName = corporation.GetProperty("name").GetString() ?? corp.ToString();
            State.Rows = rows;
            State.LastRefreshUtc = DateTimeOffset.UtcNow;
            LastError = null;
            Save();
            if (State.NotificationsEnabled && fresh.Count > 0) NewContracts?.Invoke(fresh);
            if (State.NotificationsEnabled && accepted.Count > 0) AcceptedContracts?.Invoke(accepted);
        }
        catch (Exception ex) { LastError = ex.Message; NextCheckUtc = NextCheckUtc > DateTimeOffset.UtcNow.AddMinutes(30) ? NextCheckUtc : DateTimeOffset.UtcNow.AddMinutes(30); Save(); throw; }
        finally { IsRefreshing = false; _gate.Release(); Changed?.Invoke(); }
    }

    public static List<ContractRow> FindNew(Dictionary<long, HashSet<long>> seen, long corp, List<ContractRow> rows)
    {
        bool baseline = !seen.TryGetValue(corp, out var ids);
        ids ??= new();
        var fresh = baseline ? new List<ContractRow>() : rows.Where(r => !ids.Contains(r.Contract.Id)).ToList();
        ids.UnionWith(rows.Select(r => r.Contract.Id));
        seen[corp] = ids;
        return fresh;
    }

    private string EntityName(long id) => State.EntityNames.TryGetValue(id, out var name) ? name : $"Unresolved ({id})";
    private async Task ResolveEntitiesAsync(IEnumerable<long> ids, CancellationToken token)
    {
        foreach (var chunk in ids.Where(id => id > 0 && !State.EntityNames.ContainsKey(id)).Distinct().Chunk(1000))
        {
            using var request = Request("/universe/names/", null, HttpMethod.Post);
            request.Content = new StringContent(JsonSerializer.Serialize(chunk), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, token);
            await EnsureAsync(response, token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            foreach (var entity in document.RootElement.EnumerateArray())
                State.EntityNames[entity.GetProperty("id").GetInt64()] = entity.GetProperty("name").GetString() ?? "Unknown";
        }
    }

    public static List<ContractRow> RecordHistory(ContractState state, long corp, IReadOnlyList<ContractRow> incoming)
    {
        var previous = state.History.Concat(state.Rows).Where(r => r.CorporationId == corp)
            .GroupBy(r => r.Contract.Id).ToDictionary(g => g.Key, g => g.First());
        bool baseline = state.HistoryBaselines.Add(corp);
        if (!state.AcceptedNotified.TryGetValue(corp, out var notified)) state.AcceptedNotified[corp] = notified = new();
        var accepted = new List<ContractRow>();
        foreach (var row in incoming)
        {
            previous.TryGetValue(row.Contract.Id, out var old);
            if (row.Contract.WasAccepted && notified.Add(row.Contract.Id) &&
                (old != null && !old.Contract.WasAccepted || !baseline && old == null && row.Contract.Accepted > state.LastRefreshUtc))
                accepted.Add(row);
            previous[row.Contract.Id] = row;
        }
        state.History = state.History.Where(r => r.CorporationId != corp).Concat(previous.Values).ToList();
        return accepted;
    }

    public static string? ExtractJaniceUrl(string title)
    {
        var match = Regex.Match(WebUtility.HtmlDecode(title), @"https?://janice\.e-351\.com/a/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        return match.Success ? "https://janice.e-351.com/a/" + match.Groups[1].Value : null;
    }

    public static string NormalizeLocation(string name) => Regex.Replace(name.Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u00a0', ' ').Trim(), @"\s+", " ");

    public static void Evaluate(ContractRow row, JsonElement? appraisal, decimal buyPercent, decimal tolerancePercent, string? error = null)
    {
        var issues = new List<string>();
        row.HasMismatch = false;
        row.JaniceBuy = row.ExpectedPrice = null;
        row.PriceCheck = "Unverified";
        if (row.Contract.Type != "item_exchange") { issues.Add("Not an item-exchange contract"); row.HasMismatch = true; }
        if (!row.Contract.Price.HasValue) issues.Add("No contract price");
        if (row.JaniceUrl == null) issues.Add("No Janice link in the title");
        if (row.Location.StartsWith("Unresolved (") || string.IsNullOrWhiteSpace(row.Location))
        { row.LocationCheck = "Unverified"; issues.Add("ESI could not verify the destination; check structure access"); }
        else
        {
            bool allowed = row.Contract.LocationId == JoppayaStationId ||
                (row.Contract.LocationId >= 1_000_000_000_000 && AllowedLocations.Take(2).Any(l => NormalizeLocation(l).Equals(NormalizeLocation(row.Location), StringComparison.OrdinalIgnoreCase)));
            row.LocationCheck = allowed ? "APPROVED" : "WRONG DESTINATION";
            if (!allowed) { row.HasMismatch = true; issues.Add("Destination is not one of the three approved locations"); }
        }
        if (error != null) issues.Add(error);
        if (appraisal is { } a)
        {
            var market = a.TryGetProperty("pricerMarket", out var m) && m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!market.Contains("Jita", StringComparison.OrdinalIgnoreCase)) { issues.Add("Appraisal market is not Jita"); row.HasMismatch = true; }
            if (a.TryGetProperty("immediatePrices", out var prices) && prices.TryGetProperty("totalBuyPrice", out var buy) && buy.TryGetDecimal(out var total) && total >= 0)
            {
                row.JaniceBuy = total;
                row.ExpectedPrice = decimal.Round(total * buyPercent / 100, 2);
                decimal tolerance = Math.Max(1, row.ExpectedPrice.Value * tolerancePercent / 100);
                if (row.Contract.Price is { } price)
                {
                    bool matches = Math.Abs(price - row.ExpectedPrice.Value) <= tolerance;
                    row.PriceCheck = matches ? "MATCHES TARGET" : "PRICE MISMATCH";
                    if (!matches) { row.HasMismatch = true; issues.Add($"Price is {row.ActualPercentText} of Jita buy; expected {buyPercent:0.##}%. Difference: {(price - row.ExpectedPrice.Value):N2} ISK"); }
                }
            }
            else issues.Add("Janice did not return a usable buy total");
        }
        else if (row.JaniceUrl != null && error == null) issues.Add("Appraisal has not been verified");
        row.Result = issues.Count == 0 ? "CHECKS PASSED" : "CHECK NEEDED";
        row.Reason = issues.Count == 0 ? "Price, market and configured location checks passed. Inspect contents before accepting in EVE." : string.Join("; ", issues);
    }

    private async Task<JsonElement> GetAppraisalAsync(string url, CancellationToken token)
    {
        if (_publicCache.TryGetValue(url, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromMinutes(30)) return cached.Value;
        var code = new Uri(url).Segments.Last();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new { id = 1, method = "Appraisal.get", @params = new { code } }), Encoding.UTF8), "~request~");
        using var response = await _http.PostAsync("https://janice.e-351.com/api/rpc/v1?m=Appraisal.get", form, token);
        await EnsureAsync(response, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Appraisal not found");
        var value = result.Clone();
        _publicCache[url] = (DateTimeOffset.UtcNow, value);
        return value;
    }

    public async Task<IReadOnlyList<ContractItem>> ItemsAsync(ContractRow row, EvePilotProfile pilot, CancellationToken token)
    {
        if (_items.TryGetValue((row.CorporationId, row.Contract.Id), out var cached)) return cached;
        var access = await _getAccessToken(pilot, token);
        var items = await PagesAsync<ContractItem>($"/corporations/{row.CorporationId}/contracts/{row.Contract.Id}/items/", access, token);
        foreach (var item in items)
        {
            var type = await GetAsync($"/universe/types/{item.TypeId}/", null, token);
            item.Name = type.GetProperty("name").GetString() ?? item.TypeId.ToString();
            item.UnitVolume = type.TryGetProperty("packaged_volume", out var volume) || type.TryGetProperty("volume", out volume) ? volume.GetDouble() : 0;
        }
        _items[(row.CorporationId, row.Contract.Id)] = items;
        return items;
    }

    public async Task OpenInGameAsync(long id, EvePilotProfile pilot, CancellationToken token)
    {
        if (!pilot.Scopes.Contains(WindowScope)) throw new InvalidOperationException("Reconnect this character to allow opening in-game windows.");
        var access = await _getAccessToken(pilot, token);
        using var request = Request($"/ui/openwindow/contract/?contract_id={id}", access, HttpMethod.Post);
        using var response = await _http.SendAsync(request, token);
        await EnsureAsync(response, token);
    }

    private HttpRequestMessage Request(string path, string? access, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, "https://esi.evetech.net/latest" + path);
        if (access != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return request;
    }
    private Task EnsureAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("ESI denied access. Check this character's corporation roles and reconnect to grant the required scopes.");
        if ((int)response.StatusCode == 420 || (int)response.StatusCode == 429)
        {
            var retry = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30));
            NextCheckUtc = retry > NextCheckUtc ? retry : NextCheckUtc;
            Save();
            throw new ThrottledException();
        }
        throw new HttpRequestException($"ESI returned {(int)response.StatusCode}. Try again later.");
    }
    private async Task<JsonElement> GetAsync(string path, string? access, CancellationToken token)
    {
        if (access == null && _publicCache.TryGetValue(path, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromHours(12)) return cached.Value;
        using var request = Request(path, access);
        using var response = await _http.SendAsync(request, token);
        await EnsureAsync(response, token);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var value = doc.RootElement.Clone();
        if (access == null) _publicCache[path] = (DateTimeOffset.UtcNow, value);
        return value;
    }
    private async Task<string> ResolveNameAsync(string path, long id, string? access, CancellationToken token)
    {
        try { return (await GetAsync(path, access, token)).GetProperty("name").GetString() ?? id.ToString(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not ThrottledException) { return $"Unresolved ({id})"; }
    }
    private async Task<List<T>> PagesAsync<T>(string path, string access, CancellationToken token)
    {
        var result = new List<T>();
        for (int page = 1, pages = 1; page <= pages; page++)
        {
            using var request = Request(path + "?page=" + page, access);
            using var response = await _http.SendAsync(request, token);
            if (path.EndsWith("/contracts/"))
            {
                var expiry = response.Content.Headers.Expires ?? DateTimeOffset.UtcNow.Add(
                    response.Headers.CacheControl?.MaxAge - (response.Headers.Age ?? TimeSpan.Zero) ?? TimeSpan.FromMinutes(1));
                if (expiry > NextCheckUtc) NextCheckUtc = expiry;
                if (response.Headers.RetryAfter?.Delta is { } retry) NextCheckUtc = DateTimeOffset.UtcNow.Add(retry);
                if (response.Headers.RetryAfter?.Date is { } retryDate) NextCheckUtc = retryDate;
            }
            await EnsureAsync(response, token);
            if (response.Headers.TryGetValues("X-Pages", out var values) && int.TryParse(values.First(), out var count)) pages = count;
            result.AddRange(JsonSerializer.Deserialize<List<T>>(await response.Content.ReadAsStringAsync(token)) ?? new());
        }
        return result;
    }
    public void Dispose() => _http.Dispose();
}
