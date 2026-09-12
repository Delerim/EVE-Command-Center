using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class IndustryCatalog
{
    private sealed class Catalog { public Dictionary<int,string> Types {get;set;}=new(); public List<IndustryRecipe> Recipes {get;set;}=new(); }
    private static readonly Catalog Data = Load();
    private static Catalog Load() { using var s=typeof(IndustryCatalog).Assembly.GetManifestResourceStream("EveCommandCenter.Resources.industry-catalog.json")!; return JsonSerializer.Deserialize<Catalog>(s)!; }
    public static IReadOnlyList<IndustryRecipe> Recipes => Data.Recipes;
    public static string Name(int id) => Data.Types.GetValueOrDefault(id,"Type " + id);
    public static long Num(JsonElement j,string key) => j.TryGetProperty(key,out var v) && v.TryGetInt64(out var n) ? n : 0;
    public static string Text(JsonElement j,string key) => j.TryGetProperty(key,out var v) ? v.ToString() : "";
    public static int Level(IndustryPilot p,string name) => p.Skills.GetValueOrDefault(Data.Types.FirstOrDefault(t=>t.Value==name).Key);
    public static string Activity(long id) => id switch {1=>"Manufacturing",3=>"TE research",4=>"ME research",5=>"Copying",8=>"Invention",9 or 11=>"Reaction",_=>"Activity " + id};
    public static List<IndustryJobView> Jobs(IndustryPilot p, DateTimeOffset now) => p.Jobs.OrderBy(j=>Text(j,"end_date")).Select(j =>
    {
        int type=(int)Num(j,"product_type_id"); if(type==0)type=(int)Num(j,"blueprint_type_id");
        var end=DateTimeOffset.TryParse(Text(j,"end_date"),out var d)?d:default;
        string status=Text(j,"status"); if(status=="active" && end!=default && end<=now)status="READY TO DELIVER (EST.)";
        return new IndustryJobView {Name=Name(type),Icon=$"https://images.evetech.net/types/{type}/icon?size=32",Activity=Activity(Num(j,"activity_id")),Status=status,Runs=Num(j,"runs").ToString(),End=end==default?"Unknown":end.ToLocalTime().ToString("dd MMM HH:mm"),Location="Facility " + Num(j,"facility_id")};
    }).ToList();
    public static double MaterialAmount(double quantity,int runs,int me,bool manufacturing) => manufacturing ? Math.Max(runs,Math.Ceiling(Math.Round(quantity*runs*(1-me/100.0),2,MidpointRounding.AwayFromZero))) : Math.Ceiling(quantity*runs);
    public static IndustryPlan Plan(IndustryPilot pilot,IndustryRecipe recipe,int runs,Dictionary<int,IndustryQuote> quotes)
    {
        runs=Math.Clamp(runs,1,10000);
        var candidates=pilot.Blueprints.Where(b=>Num(b,"type_id")==recipe.Blueprint).OrderByDescending(b=>Num(b,"material_efficiency")).ToArray();
        bool Copy(JsonElement b)=>Num(b,"quantity")==-2;
        bool Fits(JsonElement b)=>recipe.Activity=="invention" ? Copy(b) && Num(b,"runs")>=runs : recipe.Activity is "copying" or "research_material" or "research_time" ? !Copy(b) : !Copy(b)||Num(b,"runs")>=runs;
        bool InUse(JsonElement b)=>pilot.Jobs.Any(j=>Num(j,"blueprint_id")==Num(b,"item_id") && Text(j,"status") is "active" or "paused" or "ready");
        var eligible=candidates.Where(Fits).OrderBy(InUse).ToArray(); bool owned=eligible.Length>0;
        var bp=owned?eligible[0]:candidates.FirstOrDefault(); bool present=bp.ValueKind==JsonValueKind.Object;
        int me=present?(int)Num(bp,"material_efficiency"):0, te=present?(int)Num(bp,"time_efficiency"):0;
        // Only stock in hangars/containers, never fitted modules or ship/drone cargo.
        var stock=pilot.Assets.Where(a=>!a.IsSingleton && a.Quantity>0 && (a.LocationFlag=="Hangar" || a.LocationFlag=="Unlocked" || a.LocationFlag=="Locked" || a.LocationFlag.StartsWith("CorpSAG"))).GroupBy(a=>a.TypeId).ToDictionary(g=>g.Key,g=>g.Sum(a=>(double)a.Quantity));
        var plan=new IndustryPlan {Recipe=recipe,Blueprint=present?$"{(Copy(bp)?"BPC":"BPO")} | ME {me}% / TE {te}% | {(Copy(bp)?Num(bp,"runs")+" runs":"Original")}":"Blueprint not owned"};
        plan.Materials=recipe.Materials.Select(m=>new IndustryMaterial {TypeId=m.Key,Required=MaterialAmount(m.Value,runs,me,recipe.Activity=="manufacturing"),Owned=stock.GetValueOrDefault(m.Key),UnitPrice=quotes.GetValueOrDefault(m.Key)?.Sell}).OrderByDescending(m=>m.Missing>0).ThenBy(m=>m.Name).ToList();
        var missing=recipe.Skills.Where(s=>pilot.Skills.GetValueOrDefault(s.Key)<s.Value).ToArray();
        plan.Skills=recipe.Skills.Count==0?"No additional recipe skills listed":string.Join(" | ",recipe.Skills.Select(s=>$"{Name(s.Key)} {pilot.Skills.GetValueOrDefault(s.Key)}/{s.Value}"));
        bool busy=present && InUse(bp);
        plan.Status=pilot.Error.Length>0?"DATA / PERMISSIONS NEEDED":!owned?"BLUEPRINT / RUNS REQUIRED":busy?"BLUEPRINT IN USE":missing.Length>0?"SKILLS REQUIRED":plan.Materials.Any(m=>m.Missing>0)?"BUY MATERIALS":"READY";
        double seconds=recipe.Seconds*runs;
        if(recipe.Activity=="manufacturing")seconds*= (1-te/100.0)*(1-.04*Level(pilot,"Industry"))*(1-.03*Level(pilot,"Advanced Industry"));
        plan.Duration=recipe.Activity=="manufacturing"?$"{TimeSpan.FromSeconds(seconds).TotalHours:N1}h base estimate with blueprint TE and Industry skills; facility, implants and specialized skill bonuses excluded":$"SDE base activity time: {recipe.Seconds:N0}s; research levels, invention skills and facility effects vary";
        plan.Products=string.Join(" | ",recipe.Products.Select(p=>$"{p.Value*runs:N0} {Name(p.Key)}"));
        if(recipe.Activity=="invention")plan.Products+=$" | Base success chance {recipe.Probability:P0}; output is conditional, not guaranteed";
        bool priced=plan.Materials.All(m=>m.UnitPrice.HasValue);
        double? sale=recipe.Products.Count>0 && recipe.Products.Keys.All(t=>quotes.GetValueOrDefault(t)?.Sell is >0) ? recipe.Products.Sum(p=>p.Value*runs*quotes[p.Key].Sell!.Value) : null;
        if(recipe.Activity is "invention" or "copying" or "research_material" or "research_time")sale=null; // BPCs have no ordinary market price.
        plan.Economics=(priced?$"Buy missing: {plan.Materials.Sum(m=>m.Missing*m.UnitPrice!.Value):N0} ISK | All input replacement: {plan.Materials.Sum(m=>m.Required*m.UnitPrice!.Value):N0} ISK":"Input prices incomplete") + (sale.HasValue?$" | Output sell listing value: {sale:N0} ISK":" | Output market value unavailable") + " | Jita 4-4 sell orders; before fees, installation, hauling and order depth";
        return plan;
    }
}
