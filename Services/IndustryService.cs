using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed class IndustryService
{
    public static readonly string[] Scopes = {"esi-industry.read_character_jobs.v1","esi-characters.read_blueprints.v1","esi-assets.read_assets.v1","esi-skills.read_skills.v1"};
    private readonly EveSsoService _sso;
    private readonly HttpClient _http=EsiHttp.CreateClient();
    private readonly string _file;
    private DateTimeOffset _due;
    public bool Busy {get;private set;}
    public IndustryState State {get;private set;}
    public event Action? Changed;
    public event Action<string,string>? Alert;
    public string Status {get;private set;}="Industry snapshots; upgrade this toon once in Settings for all features.";
    public IndustryService(EveSsoService sso, string? directory=null) { _sso=sso; _file=Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EVE Command Center"),"industry.json"); try {State=JsonSerializer.Deserialize<IndustryState>(File.ReadAllText(_file))??new();}catch {State=new();} }
    public void Save() {Directory.CreateDirectory(Path.GetDirectoryName(_file)!);File.WriteAllText(_file+".tmp",JsonSerializer.Serialize(State));File.Move(_file+".tmp",_file,true);}
    public void Due() => _due=default;
    private async Task<List<JsonElement>> Read(string path,string token,CancellationToken ct)
    {
        var rows=new List<JsonElement>();int pages=1;
        for(int page=1;page<=pages;page++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://esi.evetech.net/latest"+path+(path.Contains("/blueprints/")?"?page="+page:""));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            request.Headers.TryAddWithoutValidation("X-Compatibility-Date","2026-08-25");request.Headers.UserAgent.ParseAdd("EVE-Command-Center/3.2.0");
            using var response=await _http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            if(response.Headers.TryGetValues("X-Pages",out var values)&&int.TryParse(values.FirstOrDefault(),out int n))pages=n;
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if(doc.RootElement.ValueKind==JsonValueKind.Array)rows.AddRange(doc.RootElement.EnumerateArray().Select(j=>j.Clone()));
            else if(doc.RootElement.TryGetProperty("skills",out var skills))rows.AddRange(skills.EnumerateArray().Select(j=>j.Clone()));
        }
        return rows;
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        CheckAlerts();
        if(Busy||DateTimeOffset.UtcNow<_due)return;Busy=true;_due=DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            var pilots=await _sso.LoadPilotsAsync();
            foreach(var p in pilots)
            {
                var data=State.Pilots.FirstOrDefault(x=>x.Id==p.CharacterId);
                if(data==null){data=new(){Id=p.CharacterId,Name=p.CharacterName};State.Pilots.Add(data);}
                if(!Scopes.All(p.Scopes.Contains)){data.Error="Upgrade this toon in Settings for all features";continue;}
                Status="Refreshing industry: "+p.CharacterName;Changed?.Invoke();
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    var token=await _sso.GetAccessTokenForAsync(p,timeout.Token);
                    data.Jobs=await Read($"/characters/{p.CharacterId}/industry/jobs/?include_completed=true",token,timeout.Token);
                    data.Blueprints=await Read($"/characters/{p.CharacterId}/blueprints/",token,timeout.Token);
                    data.Assets=(await _sso.GetAssetsForPlanningAsync(p,timeout.Token)).ToList();
                    data.Skills=(await Read($"/characters/{p.CharacterId}/skills/",token,timeout.Token)).ToDictionary(j=>(int)IndustryCatalog.Num(j,"skill_id"),j=>(int)IndustryCatalog.Num(j,"active_skill_level"));
                    data.Updated=DateTimeOffset.UtcNow;data.Error="";
                    try { await ResolveNamesAsync(data, p, token, timeout.Token); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { EsiDiagnostics.Write("Industry display names deferred: " + ex.GetType().Name); }
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception ex){data.Error="Refresh delayed: "+ex.GetType().Name;EsiDiagnostics.Write(data.Error);}
                Save();Changed?.Invoke();
            }
            State.Pilots.RemoveAll(p=>!pilots.Any(x=>x.CharacterId==p.Id));
            Status="Industry refreshed; ESI cache and shared queue govern freshness.";CheckAlerts();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Status="Industry refresh deferred: "+ex.GetType().Name;}
        finally{Busy=false;Save();Changed?.Invoke();}
    }
    private async Task ResolveNamesAsync(IndustryPilot data, EvePilotProfile pilot, string token, CancellationToken ct)
    {
        if (data.NamesUpdated > DateTimeOffset.UtcNow.AddHours(-1)) return;
        // Display metadata only; failures must not invalidate the assets/jobs snapshot.
        var typeIds = data.Assets.Select(a => a.TypeId).Concat(data.Blueprints.Select(b => (int)IndustryCatalog.Num(b, "type_id")))
            .Concat(data.Jobs.SelectMany(j => new[] { (int)IndustryCatalog.Num(j, "product_type_id"), (int)IndustryCatalog.Num(j, "blueprint_type_id") }))
            .Where(id => id > 0 && IndustryCatalog.Name(id).StartsWith("Type ") && !data.TypeNames.ContainsKey(id)).Distinct();
        foreach (var pair in await _sso.ResolveIndustryTypeNamesAsync(typeIds, ct))
            if (!pair.Value.StartsWith("Type ")) data.TypeNames[pair.Key] = pair.Value;
        var ids = data.Assets.Select(a => a.ItemId).ToHashSet();
        var parents = data.Assets.Select(a => a.LocationId).ToHashSet();
        var containers = data.Assets.Where(a => a.IsSingleton && parents.Contains(a.ItemId)).Select(a => a.ItemId).Distinct().ToArray();
        foreach (var batch in containers.Chunk(1000))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://esi.evetech.net/latest/characters/{pilot.CharacterId}/assets/names/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
            request.Content = new StringContent(JsonSerializer.Serialize(batch), System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) continue;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var row in json.RootElement.EnumerateArray())
            {
                string name = IndustryCatalog.Text(row, "name");
                if (!string.IsNullOrWhiteSpace(name) && name != "None") data.AssetNames[IndustryCatalog.Num(row, "item_id")] = name;
            }
        }
        var locations = data.Assets.Where(a => !ids.Contains(a.LocationId) && a.LocationFlag == "Hangar").Select(a => a.LocationId)
            .Concat(data.Jobs.Select(j => IndustryCatalog.Num(j, "facility_id"))).Where(id => id > 0).Distinct()
            .OrderBy(id => data.LocationNames.ContainsKey(id)).Take(64);
        foreach (long id in locations)
        {
            bool station = id >= 60000000 && id < 64000000;
            if (!station && !pilot.Scopes.Contains("esi-universe.read_structures.v1")) continue;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/latest/universe/{(station ? "stations" : "structures")}/{id}/");
            if (!station) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { if (!station) data.VerifiedStructures.Remove(id); continue; }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string name = IndustryCatalog.Text(json.RootElement, "name");
            if (!string.IsNullOrWhiteSpace(name)) { data.LocationNames[id] = name; if (!station) data.VerifiedStructures.Add(id); }
        }
        data.NamesUpdated = DateTimeOffset.UtcNow;
    }
    public void CheckAlerts()
    {
        int before=State.Notified.Count;
        var now=DateTimeOffset.UtcNow;
        foreach(var p in State.Pilots)
        {
            int count=0;
            foreach(var j in p.Jobs)
            {
                string key=p.Id+":"+IndustryCatalog.Num(j,"job_id");
                string status=IndustryCatalog.Text(j,"status");
                if(status is "delivered" or "cancelled" or "reverted") {State.Notified.Add(key);continue;}
                if(status is not ("active" or "ready"))continue;
                if(DateTimeOffset.TryParse(IndustryCatalog.Text(j,"end_date"),out var end)&&end<=now&&State.Notified.Add(key))count++;
            }
            if(count>0&&State.Alerts)Alert?.Invoke(p.Name,$"{count} industry jobs ready to deliver (estimated). Open Industry to review.");
        }
        if(State.Notified.Count!=before)Save();
    }
    public async Task QuoteAsync(IndustryRecipe recipe,CancellationToken ct)
    {
        foreach(int type in recipe.Materials.Keys.Concat(recipe.Products.Keys).Distinct())
        {
            if(State.Quotes.TryGetValue(type,out var quote)&&quote.Time>DateTimeOffset.UtcNow.AddHours(-1))continue;
            var q=await MiningMarketService.FetchStationPricesAsync(MiningMarketService.TheForgeRegionId,MiningMarketService.Jita44StationId,type,ct);
            State.Quotes[type]=new(){Buy=q.BestBuy,Sell=q.BestSell,Time=DateTimeOffset.UtcNow};
        }
        Save();
    }
}
