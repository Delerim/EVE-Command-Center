using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class PlanetaryAlerts
{
    public static List<string> Observe(PiState state,DateTimeOffset now)
    {
        var output=new List<string>();var current=new HashSet<string>();
        foreach(var row in PlanetaryAnalysis.Build(state,now).Colonies)
        {
            var c=row.Colony!; if(c.Error.Length>0)continue;
            string key=c.CharacterId+":"+c.PlanetId;current.Add(key);
            string status=row.Status.Contains("COLLECT")?"Collect / refill":row.Color=="#FFD166"?"Needs attention / extractor restart":"Healthy";
            if(state.AlertStates.TryGetValue(key,out var prior)&&prior!=status&&status!="Healthy")output.Add(c.Character+" | "+c.Planet+": "+status);
            state.AlertStates[key]=status;
        }
        foreach(var key in state.AlertStates.Keys.Where(k=>!current.Contains(k)).ToArray())state.AlertStates.Remove(key);
        return output;
    }
}
