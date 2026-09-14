using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public static class IndustryProfitability
{
    // CCP: Broker Fee and Sales Tax. NPC station default assumes neutral standings.
    public static double Tax(IndustryPilot pilot) => 7.5 * (1 - .11 * Math.Clamp(pilot.Skills.GetValueOrDefault(16622),0,5));
    public static double Broker(IndustryPilot pilot) => 3 - .3 * Math.Clamp(pilot.Skills.GetValueOrDefault(3446),0,5);
    public static List<IndustryProfitRow> Calculate(IndustryPlan plan,int runs,Dictionary<int,IndustryQuote> quotes,IndustryCostSettings costs)
    {
        bool marketOutput=plan.Recipe.Activity is "manufacturing" or "reaction";
        double? Sum(IEnumerable<KeyValuePair<int,double>> items,bool buy) {
            double total=0;
            foreach(var item in items.Where(i=>i.Value>0)) {
                var q=quotes.GetValueOrDefault(item.Key);var price=buy?q?.Buy:q?.Sell;
                if(price is not >0 || !double.IsFinite(price.Value))return null;
                total+=item.Value*price.Value;
            }
            return total;
        }
        var sale=marketOutput?Sum(plan.Recipe.Products.Select(p=>new KeyValuePair<int,double>(p.Key,p.Value*Math.Clamp(runs,1,10000))),costs.InstantSale):null;
        var rows=new List<IndustryProfitRow>();
        for(int i=0;i<2;i++) {
            bool buy=i==1;
            var all=Sum(plan.Materials.Select(m=>new KeyValuePair<int,double>(m.TypeId,m.Required)),buy);
            var missing=Sum(plan.Materials.Select(m=>new KeyValuePair<int,double>(m.TypeId,m.Missing)),buy);
            double? purchaseFees=buy?all*costs.BrokerPercent/100:0;
            double? saleFees=sale*(costs.TaxPercent+(costs.InstantSale?0:costs.BrokerPercent))/100;
            double? total=all+purchaseFees+costs.CopyCost+costs.JobCost;
            double? profit=sale-saleFees-total;
            rows.Add(new(){Name=buy?"PLACE BUY ORDERS":"BUY MATERIALS DIRECT",Materials=all,Missing=missing,
                PurchaseFees=purchaseFees,Sale=sale,SaleFees=saleFees,Total=total,Profit=profit,
                Note=!marketOutput?"Blueprint/research output has no ordinary market sale quote":costs.CopyCost==null||costs.JobCost==null?"Enter blueprint and installation costs (0 if none)":profit==null?"Load quotes; one or more prices are missing":"Estimate; owned materials included at replacement value"});
        }
        return rows;
    }
}
