using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class PlanetaryEconomics
{
    public static string Key(PiColony c)=>$"{c.CharacterId}:{c.PlanetId}";
    public static double TaxBase(int tier)=>tier switch {0=>5,1=>400,2=>7200,3=>60000,4=>1200000,_=>throw new ArgumentOutOfRangeException(nameof(tier))};
    public static double Customs(double quantity,int tier,double rate,bool import)=>quantity*TaxBase(tier)*rate/100*(import?.5:1);
    public static List<PiRecipe> Recipes(PiColony c)=>(c.Layout.ValueKind==System.Text.Json.JsonValueKind.Object?PlanetaryAnalysis.Array(c.Layout,"pins"):System.Array.Empty<System.Text.Json.JsonElement>()).Select(p=>{
        int id=(int)PlanetaryAnalysis.Num(p,"schematic_id");
        if(id==0&&p.TryGetProperty("factory_details",out var f))id=(int)PlanetaryAnalysis.Num(f,"schematic_id");
        return PlanetaryAnalysis.Recipes.GetValueOrDefault(id);
    }).Where(r=>r!=null).Select(r=>r!).DistinctBy(r=>r.Id).ToList();
    // A hypothetical T1-fed batch, not a reconstruction of historical imports/hauls.
    public static Dictionary<int,double> Inputs(PiColony colony,int product,double quantity)
    {
        var result=new Dictionary<int,double>();var recipes=Recipes(colony);
        bool extraction=PlanetaryAnalysis.Array(colony.Layout,"pins").Any(p=>p.TryGetProperty("extractor_details",out _));
        void Expand(int type,double amount,HashSet<int> path) {
            if(PlanetaryAnalysis.Tier(type)==1) {result[type]=result.GetValueOrDefault(type)+amount;return;}
            if(!path.Add(type))throw new InvalidOperationException("Cyclic recipe chain");
            var recipe=recipes.FirstOrDefault(r=>r.Outputs.ContainsKey(type))??throw new InvalidOperationException("Required intermediate recipe is not configured on this planet");
            double cycles=Math.Ceiling(amount/recipe.Outputs[type]);
            foreach(var input in recipe.Inputs)Expand(input.Key,input.Value*cycles,new(path));
        }
        if(PlanetaryAnalysis.Tier(product)==1) {
            if(!extraction)throw new InvalidOperationException("T1 extraction estimate requires an extractor colony");
            return result;
        }
        Expand(product,quantity,new());return result;
    }
    public static PiBatchEstimate Calculate(PiColony colony,int product,double quantity,PiTaxSettings settings,Dictionary<int,PiQuote> prices)
    {
        var inputs=Inputs(colony,product,quantity);
        double? price=settings.SellImmediately?prices.GetValueOrDefault(product)?.Buy:prices.GetValueOrDefault(product)?.Sell;
        double? materials=inputs.All(i=>prices.GetValueOrDefault(i.Key)?.Sell is >0)?inputs.Sum(i=>i.Value*prices[i.Key].Sell!.Value):null;
        double? import=inputs.Count==0?0:settings.PocoPercent is {} rate?inputs.Sum(i=>Customs(i.Value,PlanetaryAnalysis.Tier(i.Key),rate,true)):null;
        double? export=settings.PocoPercent is {} er?Customs(quantity,PlanetaryAnalysis.Tier(product),er,false):null;
        double? gross=price is >0?price*quantity:null;
        double? fees=gross*(settings.SalesTaxPercent+(settings.SellImmediately?0:settings.BrokerPercent))/100;
        return new(){Inputs=inputs,MaterialCost=materials,ImportTax=import,ExportTax=export,Gross=gross,SaleFees=fees,Other=settings.OtherCost};
    }
}
