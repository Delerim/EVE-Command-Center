using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class PlanetaryAlerts
{
    public static string Warning(PiRow row)=>row.Colony?.Error.Length>0?"":row.SecondsUntilAction is >0 and <=3600?"UNDER 1 HOUR":row.SecondsUntilAction is >3600 and <=14400?"UNDER 4 HOURS":"";
    public static List<string> Observe(PiState state,DateTimeOffset now)
    {
        var output=new List<string>();var current=new HashSet<string>();
        var analysis=PlanetaryAnalysis.Build(state,now);var rows=analysis.Colonies;
        foreach(var row in rows)
        {
            var c=row.Colony!; if(c.Error.Length>0)continue;
            string key=c.CharacterId+":"+c.PlanetId;current.Add(key);
            string status=row.Status.Contains("COLLECT")?"Collect / refill":row.Color=="#FFD166"?"Needs attention / extractor restart":"Healthy";
            if(state.AlertStates.TryGetValue(key,out var prior)&&prior!=status&&status!="Healthy")output.Add(c.Character+" | "+c.Planet+": "+status);
            state.AlertStates[key]=status;
        }
        foreach(var key in state.AlertStates.Keys.Where(k=>!k.StartsWith("warning:")&&!k.StartsWith("haul:")&&!current.Contains(k)).ToArray())state.AlertStates.Remove(key);
        foreach(var group in rows.GroupBy(r=>r.Colony!.CharacterId))
        {
            string key="warning:"+group.Key;
            var due=group.Where(r=>r.Colony!.Error.Length==0&&Warning(r).Length>0).ToArray();
            string stage=due.Any(r=>r.SecondsUntilAction<=3600)?"1":due.Length>0?"4":"";
            if(stage.Length==0){state.AlertStates.Remove(key);continue;}
            if(state.AlertStates.GetValueOrDefault(key)!=stage && !(state.AlertStates.GetValueOrDefault(key)=="1"&&stage=="4"))
            {
                state.AlertStates[key]=stage;
                output.Add(group.First().Colony!.Character+" | PI under "+stage+" hour(s): "+string.Join(", ",due.Select(r=>r.Name)));
            }
        }
        foreach(var haul in analysis.Hauls)
        {
            string key="haul:"+haul.CharacterId, stage=haul.Stage.ToString();
            if(haul.Stage==0){state.AlertStates.Remove(key);continue;}
            if(state.AlertStates.GetValueOrDefault(key)!=stage)
            {
                state.AlertStates[key]=stage;
                output.Add(haul.Character+" | "+haul.Summary);
            }
        }
        foreach(var key in state.AlertStates.Keys.Where(k=>k.StartsWith("haul:")&&!analysis.Hauls.Any(h=>"haul:"+h.CharacterId==k)).ToArray())state.AlertStates.Remove(key);
        return output;
    }
}
