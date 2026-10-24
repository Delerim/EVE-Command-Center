using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Seller dashboard reads the existing authenticated ESI service and market-order cache.
// It never moves assets, modifies orders or performs market transactions.
public sealed class SellerDashboardService : IDisposable
{
    public const int Forge = 10000002;
    public const int Domain = 10000043;
    public const long Jita44 = 60003760;
    public const long AmarrEmperor = 60008494;
    public const string AssetsScope = "esi-assets.read_assets.v1";
    public const string OrdersScope = "esi-markets.read_character_orders.v1";
    private readonly EveSsoService _sso;
    private readonly MarketOrdersService _orders;
    private readonly HttpClient _http = EsiHttp.CreateClient();
    private readonly string _file;
    private readonly Dictionary<int,SellerTypeInfo> _types = new();
    private readonly ConcurrentDictionary<int,Task<(int CategoryId,string Name)>> _groups = new();
    private readonly ConcurrentDictionary<int,Task<string>> _categories = new();
    private readonly Dictionary<int,SellerHubQuote> _quotes = new();
    private readonly Dictionary<int,SellerHubQuote> _previous = new();
    private readonly SemaphoreSlim _busy = new(1,1);
    private bool _disposed;
    private sealed class Cache
    {
        public long CharacterId { get; set; }
        public Dictionary<int,SellerTypeInfo> Types { get; set; } = new();
        public Dictionary<int,SellerHubQuote> Quotes { get; set; } = new();
        public Dictionary<int,SellerHubQuote> Previous { get; set; } = new();
    }
    public long SellerCharacterId { get; private set; }
    public IReadOnlyList<SellerStockRow> Rows { get; private set; } = Array.Empty<SellerStockRow>();
    public DateTimeOffset? CheckedUtc { get; private set; }
    public string Status { get; private set; } = "Select a linked seller character.";
    public bool Busy { get; private set; }
    public event Action? Updated;

    public SellerDashboardService(EveSsoService sso, MarketOrdersService orders, string? folder = null)
    {
        _sso=sso; _orders=orders;
        _file=Path.Combine(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EVE Command Center"),"seller-dashboard.json");
        try
        {
            if (File.Exists(_file))
            {
                var state=JsonSerializer.Deserialize<Cache>(File.ReadAllText(_file));
                if(state!=null)
                {
                    SellerCharacterId=state.CharacterId;
                    foreach(var v in state.Types) _types[v.Key]=v.Value;
                    foreach(var v in state.Quotes) _quotes[v.Key]=v.Value;
                    foreach(var v in state.Previous) _previous[v.Key]=v.Value;
                }
            }
        }
        catch (Exception e) { Status="Saved dashboard cache needs review: "+e.GetType().Name; }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EVECommandCenter-SellerDashboard/3.8.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Compatibility-Date","2026-08-25");
    }
    public void SelectSeller(long characterId)
    {
        if(characterId<=0) throw new ArgumentOutOfRangeException(nameof(characterId));
        SellerCharacterId=characterId; Rows=Array.Empty<SellerStockRow>(); CheckedUtc=null;
        Save(); Updated?.Invoke();
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var value=new Cache { CharacterId=SellerCharacterId,Types=_types,Quotes=_quotes,Previous=_previous };
        var tmp=_file+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(value));
        File.Move(tmp,_file,true);
    }
    private async Task<JsonDocument> JsonAsync(string uri,CancellationToken ct)
    {
        using var response=await _http.GetAsync(uri,ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
    private async Task<string> CategoryAsync(int id, CancellationToken ct)
    {
        if(id<=0)return "Unknown";
        using var doc=await JsonAsync($"https://esi.evetech.net/latest/universe/categories/{id}/",ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("name",out var value)?value.GetString()??"Unknown":"Unknown";
    }
    private async Task<(int CategoryId,string Name)> GroupAsync(int id,CancellationToken ct)
    {
        using var doc=await JsonAsync($"https://esi.evetech.net/latest/universe/groups/{id}/",ct).ConfigureAwait(false);
        var row=doc.RootElement;
        int cid=row.TryGetProperty("category_id",out var category)?category.GetInt32():0;
        string name=row.TryGetProperty("name",out var groupName)?groupName.GetString()??"Unknown":"Unknown";
        return(cid,name);
    }
    private async Task<SellerTypeInfo> TypeAsync(int id,CancellationToken ct)
    {
        using var doc=await JsonAsync($"https://esi.evetech.net/latest/universe/types/{id}/",ct).ConfigureAwait(false);
        var row=doc.RootElement;
        int group=row.TryGetProperty("group_id",out var groupId)?groupId.GetInt32():0;
        string name=row.TryGetProperty("name",out var itemName)?itemName.GetString()??$"Type {id}":$"Type {id}";
        var grp=await _groups.GetOrAdd(group,g=>GroupAsync(g,ct)).ConfigureAwait(false);
        var category=await _categories.GetOrAdd(grp.CategoryId,c=>CategoryAsync(c,ct)).ConfigureAwait(false);
        return new SellerTypeInfo(id,name,group,grp.CategoryId,category);
    }
    private async Task ResolveTypesAsync(IEnumerable<int> ids,CancellationToken ct)
    {
        var missing=ids.Where(id=>id>0&&!_types.ContainsKey(id)).Distinct().ToArray();
        using var gate=new SemaphoreSlim(4,4);
        var discovered=new ConcurrentDictionary<int,SellerTypeInfo>();
        await Task.WhenAll(missing.Select(async id=>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { discovered[id]=await TypeAsync(id,ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
            catch { /* Unknown types are not displayed or priced as verified stock. */ }
            finally {gate.Release();}
        })).ConfigureAwait(false);
        foreach(var p in discovered)_types[p.Key]=p.Value;
    }
    private async Task<decimal?> LowestAtStationAsync(int type,int region,long station,CancellationToken ct)
    {
        decimal? best=null;
        string endpoint=$"https://esi.evetech.net/latest/markets/{region}/orders/?order_type=sell&type_id={type}";
        int pages=1;
        for(int page=1;page<=pages;page++)
        {
            using var response=await _http.GetAsync(endpoint+"&page="+page,ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if(page==1 && response.Headers.TryGetValues("X-Pages",out var values))
            {
                if(!int.TryParse(values.FirstOrDefault(),out pages)||pages<1||pages>20)
                    return null; // Never label an incomplete region orderbook as the station best.
            }
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Market response not an array");
            foreach(var order in doc.RootElement.EnumerateArray())
            {
                if(!order.TryGetProperty("location_id",out var location)||location.GetInt64()!=station)continue;
                if(!order.TryGetProperty("price",out var price)||!price.TryGetDecimal(out var amount)||amount<=0)continue;
                if(!best.HasValue||amount<best.Value)best=amount;
            }
        }
        return best;
    }
    private async Task UpdateQuotesAsync(IEnumerable<int> types,CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        var due=types.Where(id=>id>0&&(!_quotes.TryGetValue(id,out var q)||q.CheckedUtc<now.AddMinutes(-20))).Distinct().ToArray();
        using var gate=new SemaphoreSlim(3,3);
        var received=new ConcurrentDictionary<int,SellerHubQuote>();
        await Task.WhenAll(due.Select(async id=>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Price failures never translate into an undercut recommendation.
                var jita=await LowestAtStationAsync(id,Forge,Jita44,ct).ConfigureAwait(false);
                var amarr=await LowestAtStationAsync(id,Domain,AmarrEmperor,ct).ConfigureAwait(false);
                received[id]=new SellerHubQuote(id,jita,amarr,DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch { /* Old quote, if any, is kept with its actual age. */ }
            finally {gate.Release();}
        })).ConfigureAwait(false);
        foreach(var p in received)
        {
            if(_quotes.TryGetValue(p.Key,out var old)&&old.CheckedUtc!=default)
                _previous[p.Key]=old;
            _quotes[p.Key]=p.Value;
        }
    }
    private async Task<Dictionary<long,string>> ReadContainerNamesAsync(EvePilotProfile pilot,
        IReadOnlyList<EveAssetItem> assets,CancellationToken ct)
    {
        var result=new Dictionary<long,string>();
        var parents=assets.Select(a=>a.LocationId).ToHashSet();
        var ids=assets.Where(a=>a.IsSingleton && parents.Contains(a.ItemId))
                      .Select(a=>a.ItemId).Distinct().ToArray();
        if(ids.Length==0)return result;
        var token=await _sso.GetAccessTokenForAsync(pilot,ct).ConfigureAwait(false);
        foreach(var batch in ids.Chunk(1000))
        {
            try
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,
                    $"https://esi.evetech.net/latest/characters/{pilot.CharacterId}/assets/names/");
                request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                request.Content=new StringContent(JsonSerializer.Serialize(batch),Encoding.UTF8,"application/json");
                using var response=await _http.SendAsync(request,ct).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode)continue;
                using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach(var row in doc.RootElement.EnumerateArray())
                {
                    long id=row.TryGetProperty("item_id",out var iid)?iid.GetInt64():0;
                    string name=row.TryGetProperty("name",out var label)?label.GetString()??"":"";
                    if(id>0 && !string.IsNullOrWhiteSpace(name))result[id]=name;
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch{/* Named container access can be partial; retain numeric item IDs. */}
        }
        return result;
    }
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if(_disposed || !await _busy.WaitAsync(0,ct))return;
        Busy=true;Status="Loading seller's assets...";Updated?.Invoke();
        try
        {
            var allPilots=await _sso.LoadPilotsAsync().ConfigureAwait(false);
            var pilot=allPilots.FirstOrDefault(p=>p.CharacterId==SellerCharacterId)
                ??throw new InvalidOperationException("Select a linked seller character first.");
            if(!pilot.Scopes.Contains(AssetsScope))throw new InvalidOperationException("Seller needs esi-assets.read_assets.v1. Reconnect the character in Setup.");
            if(!pilot.Scopes.Contains(OrdersScope))throw new InvalidOperationException("Seller needs esi-markets.read_character_orders.v1. Reconnect the character in Setup.");
            var assets=(await _sso.GetAssetsForPlanningAsync(pilot,ct).ConfigureAwait(false)).ToArray();
            Status=$"Resolving {assets.Select(x=>x.TypeId).Distinct().Count()} inventory types and categories...";Updated?.Invoke();
            await ResolveTypesAsync(assets.Select(x=>x.TypeId),ct).ConfigureAwait(false);
            var allById=assets.GroupBy(a=>a.ItemId).ToDictionary(g=>g.Key,g=>g.First());
            var stockIds=assets.Where(a=>a.TypeId>0 && _types.TryGetValue(a.TypeId,out var info)&&SellerStockEngine.IsSellable(info))
                .Select(a=>a.TypeId).Distinct().ToArray();
            Status="Checking active sell orders for the selected toon...";Updated?.Invoke();
            if(!_orders.Busy)await _orders.RefreshAsync(allPilots,ct,new[]{SellerCharacterId}).ConfigureAwait(false);
            var snapshot=_orders.State.Characters.FirstOrDefault(x=>x.CharacterId==SellerCharacterId);
            bool ordersFresh=snapshot!=null && snapshot.Updated>DateTimeOffset.UtcNow.AddHours(-1) && string.IsNullOrWhiteSpace(snapshot.Error);
            Status=$"Comparing Jita 4-4 and Amarr on {stockIds.Length} stock types...";Updated?.Invoke();
            await UpdateQuotesAsync(stockIds,ct).ConfigureAwait(false);
            var locations=new Dictionary<long,string>();
            foreach(var location in assets.Where(a=>(a.LocationType is "station" or "other") && a.LocationFlag=="Hangar")
                                          .Select(a=>a.LocationId).Distinct().Take(50))
            {
                var name=await _sso.ResolveDisplayLocationAsync(location,pilot,ct).ConfigureAwait(false);
                locations[location]=name??$"Location {location} (name unavailable)";
            }
            // This seller may have no Industry permissions. Resolve containers using the
            // selected character's own asset scope instead of depending on Industry sync.
            var containerNames=await ReadContainerNamesAsync(pilot,assets,ct).ConfigureAwait(false);
            var industry=BackgroundOperations.Current.Industry.State.Pilots.FirstOrDefault(x=>x.Id==SellerCharacterId);
            if(industry!=null)foreach(var name in industry.AssetNames)
                if(!containerNames.ContainsKey(name.Key))containerNames[name.Key]=name.Value;
            Rows=SellerStockEngine.Build(assets,snapshot?.Orders??new(),_types,locations,containerNames,
                _quotes,_previous,ordersFresh,DateTimeOffset.UtcNow);
            CheckedUtc=DateTimeOffset.UtcNow;
            Status=ordersFresh?$"{Rows.Count:N0} inventory positions; live order snapshot (ESI cached). Prices at Jita 4-4 and Amarr; refresh ~20 minutes."
                :"ORDERS STALE: stock visible, but do not act on unlisted/undercut flags until order refresh succeeds.";
            Save();
        }
        catch (OperationCanceledException) when(ct.IsCancellationRequested) {Status="Refresh cancelled";}
        catch(Exception ex){Status="REFRESH FAILED: "+ex.Message+". Previous stock not replaced.";}
        finally{Busy=false;_busy.Release();Updated?.Invoke();}
    }
    public void Dispose(){_disposed=true;_http.Dispose(); /* In-flight refresh may still own _busy. */}
}
