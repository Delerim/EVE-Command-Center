using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckPlanetaryProjection()
    {
        var start=new DateTimeOffset(2026,9,11,8,0,0,TimeSpan.Zero);
        var robotics=PlanetaryAnalysis.Recipes.Values.Single(r=>r.Name=="Robotics");
        var upstream=robotics.Inputs.Keys.Select(t=>PlanetaryAnalysis.Recipes.Values.Single(r=>r.Outputs.ContainsKey(t))).ToArray();
        var pins=new List<object>();var routes=new List<object>();
        var inputs=upstream.SelectMany(r=>r.Inputs.Keys).Distinct().ToArray();
        pins.Add(new{pin_id=1L,type_id=2256,contents=inputs.Select(t=>new{type_id=t,amount=80d}).ToArray()});
        for(int i=0;i<upstream.Length;i++)
        {
            long id=i+2;var r=upstream[i];pins.Add(new{pin_id=id,type_id=2470,schematic_id=r.Id,contents=Array.Empty<object>()});
            foreach(var t in r.Inputs.Keys)routes.Add(new{source_pin_id=1L,destination_pin_id=id,content_type_id=t,quantity=r.Inputs[t]});
            foreach(var t in r.Outputs.Keys)routes.Add(new{source_pin_id=id,destination_pin_id=1L,content_type_id=t,quantity=r.Outputs[t]});
        }
        pins.Add(new{pin_id=4L,type_id=2470,schematic_id=robotics.Id,contents=Array.Empty<object>()});
        foreach(var t in robotics.Inputs.Keys)routes.Add(new{source_pin_id=1L,destination_pin_id=4L,content_type_id=t,quantity=robotics.Inputs[t]});
        routes.Add(new{source_pin_id=4L,destination_pin_id=1L,content_type_id=robotics.Outputs.Keys.Single(),quantity=3d});
        var colony=new PiColony{CharacterId=1,Character="Factory pilot",PlanetId=1,LastUpdate=start,Fetched=start,Layout=JsonSerializer.SerializeToElement(new{pins,routes})};
        var original=colony.Layout.GetRawText();
        var first=PlanetaryProjection.Build(colony,start.AddHours(1));
        Check(upstream.All(r=>first.Stores[1].GetValueOrDefault(r.Outputs.Keys.Single())==5),"PI projection produces T2 through routed cycles");
        var done=PlanetaryProjection.Build(colony,start.AddHours(3));
        Check(done.Stores[1].GetValueOrDefault(robotics.Outputs.Keys.Single())==3 && inputs.All(t=>done.Stores[1].GetValueOrDefault(t)==0),"PI projection consumes T1 and produces routed T3 without duplicating stock");
        Check(PlanetaryProjection.Build(colony,start.AddDays(3)).Stores[1].GetValueOrDefault(robotics.Outputs.Keys.Single())==3,"Exhausted factories stop producing in long-lived PI projections");
        Check(colony.Layout.GetRawText()==original,"PI projections preserve the original ESI snapshot");
        var state=new PiState{ContainerId=900,Colonies=new(){colony},Assets=inputs.Select(t=>new EveAssetItem{ItemId=t,LocationId=900,TypeId=t,Quantity=100000}).ToList()};
        var analysis=PlanetaryAnalysis.Build(state,start.AddHours(3));
        Check(analysis.Refills.All(r=>r.Current==0&&r.Snapshot==80&&r.Need==r.Target),"Refills use depleted projected inputs while retaining original snapshot quantities");
        Check(analysis.StockBudget.All(b=>b.Remaining==b.Available-b.Required&&b.Required>13000),"Shared stock budget shows reserves remaining after full refills");
        Check(analysis.FactoryTiers.SelectMany(t=>t.Products).Single(p=>p.TypeId==robotics.Outputs.Keys.Single()).Collect==3,"Factory output summary includes projected collectable T3");
        Check(new PiHaulSummary{Volume=40499}.Stage==0 && new PiHaulSummary{Volume=40500}.Stage==1 && new PiHaulSummary{Volume=44999}.Stage==1 && new PiHaulSummary{Volume=45000}.Stage==2,"PI haul alerts use exact 40500 and 45000 m3 boundaries");
        var hauling=new PiState();
        JsonElement HaulLayout(double units)=>JsonSerializer.SerializeToElement(new{pins=new object[]{new{pin_id=1L,type_id=2256,contents=new[]{new{type_id=2398,amount=units}}},new{pin_id=2L,type_id=2848,extractor_details=new{cycle_time=900,qty_per_cycle=0,product_type_id=2267},expiry_time=start}},routes=Array.Empty<object>()});
        for(int i=0;i<6;i++)hauling.Colonies.Add(new(){CharacterId=2,Character="Hauler",PlanetId=i,LastUpdate=start,Fetched=start,Layout=HaulLayout(37500)});
        var haul=PlanetaryAnalysis.Build(hauling,start).Hauls.Single();
        Check(haul.Volume==42750&&haul.Stage==1,"T1 haul warning combines extracting planets per toon at 40500 m3");
        Check(PlanetaryAlerts.Observe(hauling,start).Count(x=>x.Contains("COLLECT SOON"))==1&&PlanetaryAlerts.Observe(hauling,start).Count==0,"Collection warning fires once per toon, not once per planet");
        hauling.Colonies[0].Layout=HaulLayout(52500);
        Check(PlanetaryAlerts.Observe(hauling,start).Count(x=>x.Contains("haul limit reached"))==1,"Crossing 45000 m3 escalates the toon collection warning");
    }
}
