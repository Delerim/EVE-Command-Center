using System.IO;
using System.Text.Json;
namespace EveCommandCenter.Services;

public sealed class TrackedRock
{
    public string Ore { get; set; } = "";
    public double StartM3 { get; set; }
    public double LeftM3 { get; set; }
    public double Share { get; set; } = 1;
    public bool Active { get; set; }
    public DateTime StartedUtc { get; set; }
    public string Note { get; set; } = "Not set";
    public double Percent => StartM3>0?Math.Clamp(LeftM3/StartM3*100,0,100):0;
    public string Text => StartM3<=0?"Set rock volume":$"{LeftM3:N0} m3 est. | "+(Active?LeftM3<=0?"Rescan":"tracking":"paused");
}
public sealed class RockTrackingState
{
    public bool Enabled { get; set; }
    public Dictionary<string,TrackedRock[]> Pilots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class RockTrackingService
{
    private readonly object _gate=new();
    private readonly string _file;
    private RockTrackingState _state=new();
    private DateTime _saved;
    public RockTrackingService(string? directory=null)
    {
        _file=Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EVE Command Center"),"rock-tracking.json");
        try{_state=JsonSerializer.Deserialize<RockTrackingState>(File.ReadAllText(_file))??new();_state.Pilots=new(_state.Pilots,StringComparer.OrdinalIgnoreCase);foreach(var lanes in _state.Pilots.Values)foreach(var rock in lanes){rock.Active=false;rock.Note="Paused after restart; rescan and set current volume.";}}catch{_state=new();}
    }
    public bool Enabled { get {lock(_gate)return _state.Enabled;} }
    public void Enable(bool enabled){lock(_gate){_state.Enabled=enabled;if(!enabled)foreach(var lanes in _state.Pilots.Values)foreach(var rock in lanes){rock.Active=false;rock.Note="Tracking disabled; rescan before restarting.";}Save();}}
    public TrackedRock[] Get(string pilot)
    {
        lock(_gate){var rows=_state.Pilots.GetValueOrDefault(pilot)??new[]{new TrackedRock(),new TrackedRock()};return rows.Select(r=>new TrackedRock {Ore=r.Ore,StartM3=r.StartM3,LeftM3=r.LeftM3,Share=r.Share,Active=r.Active,StartedUtc=r.StartedUtc,Note=r.Note}).ToArray();}
    }
    public void Set(string pilot,int lane,string ore,double volume,double share,DateTime now)
    {
        if(lane is <0 or >1||!double.IsFinite(volume)||volume<=0||!double.IsFinite(share)||share<=0||string.IsNullOrWhiteSpace(ore))throw new ArgumentException("Enter an ore name, positive volume and positive yield share.");
        lock(_gate){if(!_state.Pilots.TryGetValue(pilot,out var lanes))_state.Pilots[pilot]=lanes=new[]{new TrackedRock(),new TrackedRock()};lanes[lane]=new(){Ore=ore.Trim(),StartM3=volume,LeftM3=volume,Share=share,Active=_state.Enabled,StartedUtc=now,Note="Estimate: normal logged yield only; excludes unknown residue and other miners."};Save();}
    }
    public void Pause(string pilot,int lane){lock(_gate){if(_state.Pilots.TryGetValue(pilot,out var lanes)){lanes[lane].Active=false;lanes[lane].Note="Paused. Rescan and set the current volume to restart.";Save();}}}
    public void Record(string pilot,string ore,double? volume,bool critical,DateTime at)
    {
        lock(_gate)
        {
            if(!_state.Enabled||critical||!_state.Pilots.TryGetValue(pilot,out var lanes))return;
            var matching=lanes.Where(r=>r.Active&&at>r.StartedUtc&&r.Ore.Equals(ore.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matching.Length==0)return;
            if(volume is not >0||!double.IsFinite(volume.Value)){foreach(var r in matching){r.Active=false;r.Note="Ore volume unavailable; rescan before restarting.";}Save();return;}
            double total=lanes.Where(r=>r.StartM3>0&&r.Ore.Equals(ore.Trim(),StringComparison.OrdinalIgnoreCase)).Sum(r=>r.Share);
            foreach(var r in matching)r.LeftM3=Math.Max(0,r.LeftM3-volume.Value*r.Share/total);
            if(DateTime.UtcNow-_saved>TimeSpan.FromSeconds(10))Save();
        }
    }
    private void Save(){try{Directory.CreateDirectory(Path.GetDirectoryName(_file)!);File.WriteAllText(_file+".tmp",JsonSerializer.Serialize(_state));File.Move(_file+".tmp",_file,true);_saved=DateTime.UtcNow;}catch(Exception ex){EsiDiagnostics.Write("Rock tracking save: "+ex.GetType().Name);}}
}
