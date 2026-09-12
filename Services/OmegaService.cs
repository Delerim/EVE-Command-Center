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
    public OmegaService(EveSsoService sso){_sso=sso;try{Pilots=JsonSerializer.Deserialize<List<OmegaPilot>>(File.ReadAllText(_file))??new();}catch{Pilots=new();}}
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
