using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EveCommandCenter.Services;

/// <summary>Observed combat events, not fitted DPS or a live module/debuff state.</summary>
public sealed class CombatTelemetry
{
    private readonly ConcurrentDictionary<string, State> _pilots=new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _clock;
    private static readonly Regex Tags=new("<[^>]*>",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex Damage=new(@"<color=(0x[0-9a-fA-F]+)><b>(\d+)</b>",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex Timestamp=new(@"^\[\s*(\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2})\s*\]",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    private static readonly Regex Weapon=new(@"\s-\s(.+?)\s-\s",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    public CombatTelemetry(Func<DateTime>? clock=null)=>_clock=clock??(()=>DateTime.UtcNow);
    public void Remove(string pilot)=>_pilots.TryRemove(pilot,out _);
    public void Observe(string pilot,string line)
    {
        if(!line.Contains("(combat)")&&!line.Contains("(notify)"))return;
        var now=_clock();var match=Timestamp.Match(line);
        var time=match.Success&&DateTime.TryParseExact(match.Groups[1].Value,"yyyy.MM.dd HH:mm:ss",CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var parsed)?parsed:now;
        // Backfilled/stale logs must not become current warnings or rate spikes.
        if(time<now.AddMinutes(-2)||time>now.AddSeconds(5))return;
        var plain=Tags.Replace(line,"");
        var damage=Damage.Match(line);
        if(line.Contains("(combat)")&&damage.Success&&int.TryParse(damage.Groups[2].Value,out var amount)) {
            var color=damage.Groups[1].Value.ToLowerInvariant();
            if(color is not ("0xff00ffff" or "0xffcc0000"))return;
            bool incoming=color=="0xffcc0000";
            var state=_pilots.GetOrAdd(pilot,_=>new());
            lock(state) {
                state.Events.Enqueue(new(time,amount,incoming?1:0));Trim(state,now);
                if(incoming && (state.LastIncomingTime==null || time>=state.LastIncomingTime)) {state.LastIncomingTime=time;state.LastIncoming=amount;}
                if(state.LastDamage==null||time>=state.LastDamage) {
                    state.LastDamage=time;
                    if(!incoming) {var weapon=Weapon.Match(plain);if(weapon.Success)state.LastWeapon=weapon.Groups[1].Value.Trim();}
                }
            }
        }
        bool scramble=line.Contains("(combat)")&&AlertPatterns.Matches(plain.ToLowerInvariant(),"warp_scramble_combat")
            &&Regex.IsMatch(plain,@"to\s+you\b",RegexOptions.IgnoreCase)&&!Regex.IsMatch(plain,@"from\s+you\b",RegexOptions.IgnoreCase);
        bool blocked=line.Contains("(notify)")&&AlertPatterns.Matches(line,"warp_scramble");
        if(scramble||blocked) {
            var state=_pilots.GetOrAdd(pilot,_=>new());lock(state) {
                if(state.ThreatTime==null||time>=state.ThreatTime) {state.ThreatTime=time;state.Threat=scramble?"Scramble attempt logged":"Warp blocked in log";}
            }
        }
    }
    public void Repair(string pilot,int amount,bool incoming)
    {
        if(amount<=0)return;var now=_clock();var state=_pilots.GetOrAdd(pilot,_=>new());
        lock(state){state.Events.Enqueue(new(now,amount,incoming?2:3));Trim(state,now);}
    }
    public CombatSnapshot Snapshot(string pilot)
    {
        if(!_pilots.TryGetValue(pilot,out var state))return new();var now=_clock();
        lock(state) {
            Trim(state,now);var events=state.Events.Where(e=>e.Time>now.AddSeconds(-30)&&e.Time<=now).ToArray();
            double Rate(int kind)=>events.Where(e=>e.Kind==kind).Sum(e=>(double)e.Amount)/30;
            double? age=state.LastDamage.HasValue?Math.Max(0,(now-state.LastDamage.Value).TotalSeconds):null;
            double? threatAge=state.ThreatTime.HasValue?Math.Max(0,(now-state.ThreatTime.Value).TotalSeconds):null;
            return new() {OutDps=Rate(0),InDps=Rate(1),RepIn=Rate(2),RepOut=Rate(3),
                DamageIn30Seconds=events.Where(e=>e.Kind==1).Sum(e=>(double)e.Amount),
                LastIncoming=state.LastIncoming, IncomingAgeSeconds=state.LastIncomingTime.HasValue?Math.Max(0,(now-state.LastIncomingTime.Value).TotalSeconds):null,
                PeakIn=events.Where(e=>e.Kind==1).Select(e=>e.Amount).DefaultIfEmpty().Max(),
                LastWeapon=state.LastWeapon,AgeSeconds=age,Threat=state.Threat,ThreatAgeSeconds=threatAge};
        }
    }
    private static void Trim(State state,DateTime now) {
        // Keep a hard bound as well as an age bound; reads filter timestamps even after delayed delivery.
        while(state.Events.Count>0&&(state.Events.Count>5000||state.Events.Peek().Time<now.AddSeconds(-30)))state.Events.Dequeue();
    }
    private sealed class State {
        public Queue<Entry> Events {get;}=new();public DateTime? LastDamage,ThreatTime;
        public DateTime? LastIncomingTime;public int LastIncoming;
        public string LastWeapon="",Threat="";
    }
    private readonly record struct Entry(DateTime Time,int Amount,int Kind);
}
public sealed record CombatSnapshot {
    public double OutDps {get;init;} public double InDps {get;init;} public double RepIn {get;init;} public double RepOut {get;init;}
    public double DamageIn30Seconds {get;init;} public int LastIncoming {get;init;} public double? IncomingAgeSeconds {get;init;}
    public int PeakIn {get;init;} public double? AgeSeconds {get;init;} public string LastWeapon {get;init;}="";
    public string Threat {get;init;}="";public double? ThreatAgeSeconds {get;init;}
    public bool RecentThreat=>ThreatAgeSeconds is >=0 and <30;
}
