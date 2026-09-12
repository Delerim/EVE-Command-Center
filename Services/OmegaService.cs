using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed class OmegaService
{
    public const string Scope="esi-clones.read_clones.v1";
    private readonly EveSsoService _sso;
    private readonly HttpClient _http=EsiHttp.CreateClient();
    private readonly string _file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EVE Command Center","omega.json");
    private DateTimeOffset _due;public bool Busy {get;private set;}
    public List<OmegaPilot> Pilots {get;private set;}
    public event Action? Changed;
    public event Action<string,string>? Alert;
    public OmegaBudgetState Budget { get; private set; } = new();
    private string BudgetFile => _file + ".budget";
    private DateTimeOffset _marketDue;
    private bool _marketBusy;
    public string MarketStatus { get; private set; } = "Waiting for PLEX quote and offer announcements.";
    public void SaveBudget(){Directory.CreateDirectory(Path.GetDirectoryName(BudgetFile)!);File.WriteAllText(BudgetFile+".tmp",JsonSerializer.Serialize(Budget));File.Move(BudgetFile+".tmp",BudgetFile,true);}
    public void CheckAlerts(){var before=JsonSerializer.Serialize(Budget.Notified);var alerts=OmegaPlanning.Observe(Pilots,Budget,DateTimeOffset.UtcNow);if(before!=JsonSerializer.Serialize(Budget.Notified))SaveBudget();if(alerts.Count>0){Alert?.Invoke($"{alerts.Count} Omega accounts due",string.Join("\n",alerts.Take(5)));}}
    public async Task RefreshMarketAsync(CancellationToken ct)
    {
        if(_marketBusy||DateTimeOffset.UtcNow<_marketDue)return;
        _marketBusy=true;_marketDue=DateTimeOffset.UtcNow.AddMinutes(30);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var errors=new List<string>();
        try
        {
            if(Budget.PriceTime<DateTimeOffset.UtcNow.AddMinutes(-30))
                try{var q=await MiningMarketService.FetchStationPricesAsync(MiningMarketService.GlobalPlexMarketRegionId,0,MiningMarketService.PlexTypeId,timeout.Token);if(q.BestSell is >0){Budget.PlexSell=q.BestSell;Budget.PriceTime=DateTimeOffset.UtcNow;}else errors.Add("No global PLEX sell quote; retaining previous price");}catch(Exception ex){errors.Add("PLEX refresh deferred: "+ex.GetType().Name);}
            if(Budget.NewsTime<DateTimeOffset.UtcNow.AddHours(-6))
                try{using var news=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};var xml=await news.GetStringAsync("https://www.eveonline.com/rss",timeout.Token);Budget.News=OmegaPlanning.ParseNews(xml);Budget.NewsTime=DateTimeOffset.UtcNow;}catch(Exception ex){errors.Add("Offer news refresh deferred: "+ex.GetType().Name);}
            MarketStatus=errors.Count>0?string.Join(" | ",errors):"PLEX checks every 30 minutes; official news every 6 hours. Store availability must be verified.";
            SaveBudget();Changed?.Invoke();
        }
        catch(Exception ex){MarketStatus="Omega market refresh deferred: "+ex.GetType().Name;}
        finally{_marketBusy=false;}
    }
    public OmegaService(EveSsoService sso){_sso=sso;try{Budget=JsonSerializer.Deserialize<OmegaBudgetState>(File.ReadAllText(BudgetFile))??new();}catch{}try{Pilots=JsonSerializer.Deserialize<List<OmegaPilot>>(File.ReadAllText(_file))??new();}catch{Pilots=new();}}
    public void Save(){Directory.CreateDirectory(Path.GetDirectoryName(_file)!);File.WriteAllText(_file+".tmp",JsonSerializer.Serialize(Pilots));File.Move(_file+".tmp",_file,true);}
    public void Due()=>_due=default;
    public async Task RefreshAsync(CancellationToken ct)
    {
        if(Busy||DateTimeOffset.UtcNow<_due)return;Busy=true;_due=DateTimeOffset.UtcNow.AddMinutes(30);
        try
        {
            var linked=await _sso.LoadPilotsAsync();
            foreach(var p in linked)
            {
                var row=Pilots.FirstOrDefault(r=>r.Id==p.CharacterId);if(row==null){row=new(){Id=p.CharacterId,Name=p.CharacterName};Pilots.Add(row);}
                if(!p.Scopes.Contains(Scope)){row.Error="Link clone permission";continue;}
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    using var req=new HttpRequestMessage(HttpMethod.Get,$"https://esi.evetech.net/latest/characters/{p.CharacterId}/clones/");
                    req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await _sso.GetAccessTokenForAsync(p,timeout.Token));
                    req.Headers.TryAddWithoutValidation("X-Compatibility-Date","2026-08-25");req.Headers.UserAgent.ParseAdd("EVE-Command-Center/3.2.0");
                    using var response=await _http.SendAsync(req,timeout.Token);response.EnsureSuccessStatusCode();
                    using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));row.Clones=doc.RootElement.Clone();row.Updated=DateTimeOffset.UtcNow;row.Error="";
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception ex){row.Error="Clone refresh deferred: "+ex.GetType().Name;}
                Save();Changed?.Invoke();
            }
            Pilots.RemoveAll(p=>!linked.Any(l=>l.CharacterId==p.Id));
        }
        catch(OperationCanceledException){}catch(Exception ex){EsiDiagnostics.Write("Omega clone refresh: "+ex.GetType().Name);}
        finally{Busy=false;Save();Changed?.Invoke();}
    }
}
