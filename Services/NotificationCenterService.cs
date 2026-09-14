using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed class CenterNotification
{
    public string Id {get;set;}="";
    public string Source {get;set;}="";
    public string Title {get;set;}="";
    public string Detail {get;set;}="";
    public string Color {get;set;}="#FFD166";
    public bool Active {get;set;}
    public bool Read {get;set;}
    public DateTimeOffset Updated {get;set;}
    public string State=>Active?Read?"ACTIVE / READ":"ACTIVE / NEW":Read?"HISTORY":"UNREAD";
    public string Time=>Updated.ToLocalTime().ToString("dd MMM HH:mm");
}
public sealed class NotificationCenterService
{
    public static NotificationCenterService Current {get;}=new();
    private readonly string _file;
    public List<CenterNotification> Items {get;private set;}=new();
    public event Action? Changed;
    public NotificationCenterService(string? directory=null)
    {
        _file=Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EVE Command Center"),"notifications.json");
        try{Items=JsonSerializer.Deserialize<List<CenterNotification>>(File.ReadAllText(_file))??new();}catch{}
    }
    public void SyncPi(PiState state,DateTimeOffset now)
    {
        var analysis=PlanetaryAnalysis.Build(state,now);
        var active=analysis.Colonies.Where(r=>r.Color=="#FFD166"||r.Status.Contains("COLLECT")||PlanetaryAlerts.Warning(r).Length>0)
            .GroupBy(r=>r.Colony!.CharacterId).Select(g=>new CenterNotification{Id="PI:"+g.Key,Source="PI",Title=g.First().Colony!.Character+" | "+g.Count()+" colonies",Detail=string.Join("\n",g.Select(r=>r.Name+": "+(PlanetaryAlerts.Warning(r).Length>0?PlanetaryAlerts.Warning(r)+" (estimated restart/refill)":r.Status)+" | "+r.Next)),Color="#FFD166",Active=true,Updated=now}).ToList();
        active.AddRange(analysis.Hauls.Where(h=>h.Stage>0).Select(h=>new CenterNotification{Id="PI:haul:"+h.CharacterId,Source="PI",Title=h.Character+" | T1 collection",Detail=h.Summary+" across extracting planets. Estimate from saved colony contents and nominal production; verify before hauling.",Color=h.Color,Active=true,Updated=now}));
        bool changed=false;var ids=active.Select(x=>x.Id).ToHashSet();
        foreach(var row in Items.Where(x=>x.Source=="PI"&&x.Active&&!ids.Contains(x.Id))){row.Active=false;row.Read=true;row.Updated=now;changed=true;}
        foreach(var row in active)
        {
            var old=Items.FirstOrDefault(x=>x.Id==row.Id);
            if(old==null){Items.Add(row);changed=true;}
            else if(!old.Active||old.Detail!=row.Detail){old.Active=true;old.Read=false;old.Detail=row.Detail;old.Color=row.Color;old.Updated=now;changed=true;}
        }
        if(changed)Save();
    }
    public void Record(string title,string detail,string category)
    {
        Items.Add(new(){Id=Guid.NewGuid().ToString(),Source=category.Contains("MINING")?"MINING":category.Contains("PLANETARY")?"PI":category.Contains("OMEGA")?"OMEGA":category.Contains("INDUSTRY")?"INDUSTRY":category.Contains("CONTRACT")?"CONTRACTS":"MOONS",Title=title,Detail=detail,Updated=DateTimeOffset.UtcNow});Save();
    }
    public void MarkRead(){foreach(var item in Items)item.Read=true;Save();}
    private void Save()
    {
        // Keep current issues; bound historical popups to 300 entries and 30 days.
        Items=Items.Where(x=>x.Active).Concat(Items.Where(x=>!x.Active&&x.Updated>DateTimeOffset.UtcNow.AddDays(-30)).OrderByDescending(x=>x.Updated).Take(300)).ToList();
        try{Directory.CreateDirectory(Path.GetDirectoryName(_file)!);File.WriteAllText(_file+".tmp",JsonSerializer.Serialize(Items));File.Move(_file+".tmp",_file,true);}catch(Exception ex){EsiDiagnostics.Write("Notification history save: "+ex.GetType().Name);}
        Changed?.Invoke();
    }
}
