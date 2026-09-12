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
    public string Status {get;private set;}="Industry snapshots; link industry permission to begin.";
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
                if(!Scopes.All(p.Scopes.Contains)){data.Error="Link industry permissions";continue;}
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
