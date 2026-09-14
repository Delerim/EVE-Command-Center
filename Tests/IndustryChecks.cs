using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static IndustryPilot CheckIndustry()
    {
        Check(IndustryCatalog.Recipes.Count>10000,"Bundled industry catalogue covers published CCP blueprint activities");
        Check(IndustryCatalog.MaterialAmount(1,10,10,true)==10 && IndustryCatalog.MaterialAmount(86,10,10,true)==774,"Industry ME rounds at job level and preserves one item per run minimum");
        Check(new[]{1L,3,4,5,8,11}.Select(IndustryActivities.Code).Distinct().Count()==6,"Industry activity tabs distinguish manufacturing, ME, TE, copying, invention and reactions");
        Check(IndustryActivities.Matches("research_material","research_material")&&!IndustryActivities.Matches("research_material","research_time")&&IndustryActivities.Matches("all","invention"),"Industry activity filtering keeps research types separate and supports all activities");
        Check(IndustryActivities.StatusColor("READY")!=IndustryActivities.StatusColor("BUY MATERIALS")&&IndustryActivities.StatusColor("SKILLS REQUIRED")!=IndustryActivities.StatusColor("BLUEPRINT IN USE"),"Industry readiness states have distinct visual colours");
        var recipe=IndustryCatalog.Recipes.First(r=>r.Activity=="manufacturing"&&r.Materials.Count>0&&r.Products.Count==1);
        var pilot=new IndustryPilot {Id=42,Name="Industry Test Pilot",Error="",Updated=DateTimeOffset.UtcNow,
            Blueprints=new(){JsonSerializer.SerializeToElement(new {item_id=123L,type_id=recipe.Blueprint,quantity=-2,runs=5,material_efficiency=10,time_efficiency=20})},
            Skills=new(recipe.Skills),Assets=recipe.Materials.Select(m=>new EveAssetItem {TypeId=m.Key,ItemId=m.Key,LocationId=60003760,LocationFlag="Hangar",Quantity=1000000}).ToList()};
        var plan=IndustryCatalog.Plan(pilot,recipe,1,new());
        Check(plan.Status=="READY"&&plan.Blueprint.StartsWith("BPC"),"Owned BPC with skills and material stock is ready");
        var financialPlan=new IndustryPlan {Recipe=new(){Activity="manufacturing",Products=new(){{2,2}}},Materials=new(){new(){TypeId=1,Required=10,Owned=4}}};
        var prices=new Dictionary<int,IndustryQuote>{{1,new(){Buy=8,Sell=10}},{2,new(){Buy=90,Sell=100}}};
        var costs=new IndustryCostSettings{CopyCost=5,JobCost=10,TaxPercent=5,BrokerPercent=2};
        var finance=IndustryProfitability.Calculate(financialPlan,1,prices,costs);
        Check(finance[0].Profit==71 && Math.Abs(finance[1].Profit!.Value-89.4)<.001,"Industry net profit includes all material replacement, copy cost, installation, sales tax and both order commissions");
        Check(finance[0].Missing==60 && finance[1].Missing==48,"Industry shopping costs exclude owned stock without inflating full-job profit");
        costs.InstantSale=true;
        Check(IndustryProfitability.Calculate(financialPlan,1,prices,costs)[0].Profit==56,"Immediate output sales use buy quotes and omit only the sell-order commission");
        costs.CopyCost=null;
        Check(IndustryProfitability.Calculate(financialPlan,1,prices,costs).All(r=>r.Profit==null),"Unknown BPC costs cannot silently produce a net profit estimate");
        costs.CopyCost=0;prices[1].Buy=null;
        Check(IndustryProfitability.Calculate(financialPlan,1,prices,costs)[1].Profit==null,"Missing buy quotes are not treated as free materials");
        financialPlan.Recipe.Activity="invention";
        Check(IndustryProfitability.Calculate(financialPlan,1,prices,costs).All(r=>r.Profit==null),"Invention BPC output is not valued as a normal market item");
        Check(Math.Abs(IndustryProfitability.Tax(new(){Skills=new(){{16622,5}}})-3.375)<.0001 && IndustryProfitability.Broker(new(){Skills=new(){{3446,5}}})==1.5,"Industry fee defaults apply Accounting and Broker Relations skills");
        Check(IndustryCatalog.Plan(pilot,recipe,6,new()).Status=="BLUEPRINT / RUNS REQUIRED","Planner rejects jobs exceeding BPC remaining runs");
        pilot.Assets[0].LocationFlag="HiSlot0";
        Check(IndustryCatalog.Plan(pilot,recipe,1,new()).Materials.First(m=>m.TypeId==pilot.Assets[0].TypeId).Missing>0,"Fitted equipment is excluded from available industry materials");
        pilot.Assets[0].LocationFlag="Hangar";
        pilot.Jobs.Add(JsonSerializer.SerializeToElement(new {job_id=5,blueprint_id=123L,blueprint_type_id=recipe.Blueprint,activity_id=1,status="active",runs=1,end_date=DateTimeOffset.UtcNow.AddMinutes(-1)}));
        Check(IndustryCatalog.Plan(pilot,recipe,1,new()).Status=="BLUEPRINT IN USE","Undelivered jobs keep their blueprint unavailable");
        Check(IndustryCatalog.Jobs(pilot,DateTimeOffset.UtcNow).Single().Status.Contains("READY"),"Elapsed active industry jobs become ready-to-deliver estimates");
        var pi=new PiState();var now=DateTimeOffset.UtcNow;
        pi.Colonies.Add(new(){CharacterId=1,PlanetId=2,Character="Pilot",Planet="Planet",LastUpdate=now,Layout=JsonSerializer.SerializeToElement(new {pins=new[]{new {pin_id=1,type_id=2848,install_time=now,expiry_time=now.AddMinutes(1),extractor_details=new {cycle_time=60,qty_per_cycle=100,product_type_id=2267}}},routes=Array.Empty<object>()})});
        Check(PlanetaryAlerts.Observe(pi,now).Count==1,"PI nearing expiry warns on first observation");
        Check(PlanetaryAlerts.Observe(pi,now.AddMinutes(2)).Count==1 && PlanetaryAlerts.Observe(pi,now.AddMinutes(3)).Count==0,"Extractor restart produces one PI alert per transition");
        var saved=JsonSerializer.Deserialize<PiState>(JsonSerializer.Serialize(pi))!;
        Check(PlanetaryAlerts.Observe(saved,now.AddMinutes(4)).Count==0,"PI alert deduplication survives restart");
        var directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ecc-industry-check-"+Guid.NewGuid());
        var service=new IndustryService(new EveSsoService(),directory);service.State.Pilots.Add(pilot);int alerts=0;service.Alert+=(_,_)=>alerts++;
        service.CheckAlerts();service.CheckAlerts();
        Check(alerts==1,"Industry ready jobs notify once per pilot rather than on every timer tick");
        var restored=new IndustryService(new EveSsoService(),directory);restored.Alert+=(_,_)=>alerts++;restored.CheckAlerts();
        Check(alerts==1,"Industry ready-job deduplication survives restart");
        var omega=new OmegaPilot();
        Check(omega.Remaining=="Unknown"&&omega.OmegaStatus.Contains("not ESI verified"),"Unknown Omega expiry is never invented from character skills");
        Check(JsonSerializer.Serialize(omega).Contains("Clones"),"Unlinked Omega pilots can be persisted before clone authorization");
        omega.Expiry=DateTimeOffset.UtcNow.AddDays(-1);
        Check(omega.OmegaStatus.Contains("verify")&&!omega.OmegaStatus.StartsWith("Alpha"),"Expired manual Omega date requests verification rather than claiming live Alpha status");
        return pilot;
    }
}
