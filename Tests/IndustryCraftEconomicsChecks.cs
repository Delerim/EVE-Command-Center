using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckCraftEconomics(string folder)
    {
        var now=DateTimeOffset.UtcNow;
        var recipe=new IndustryRecipe {Blueprint=100,Activity="manufacturing",Products=new(){[10]=2},Materials=new(){[34]=10,[35]=1}};
        var blueprint=new IndustryBlueprintLibrary.Blueprint(42,"Builder",7001,100,60003760,"Can",10,20,10,true,true,"Available");
        var suggestion=new IndustryCraftSuggestions.Suggestion(blueprint,recipe,10,10,1,"");
        var prices=new CraftPricingState {AdjustedExpiresUtc=now.AddHours(1),IndicesExpiresUtc=now.AddHours(1),
            AdjustedPrices=new(){[34]=5,[35]=10},ManufacturingIndices=new(){[51]=.1m}};
        prices.Quotes["Jita:34"]=new(8,10,1000,1000,now,now.AddMinutes(20));
        prices.Quotes["Jita:35"]=new(15,20,1000,1000,now,now.AddMinutes(20));
        prices.Quotes["Jita:10"]=new(800,1000,1000,1000,now,now.AddMinutes(20));
        var settings=new CraftEconomicsSettings {SystemName="Fixture",SystemId=51,FacilityTaxPercent=1,JobBonusPercent=50,AlphaPercent=.25m,ExtraPerRun=5};
        var value=IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now);
        Check(value.Materials==220 && value.JobCost==12.3m && value.Extras==10 && value.GrossSale==4000 && value.Broker==120 && value.Tax==300 && value.Profit==3337.7m,
            "Craft profit includes ME-rounded replacement inputs, EIV job costs, SCC/Alpha/facility fees, extras, broker and sales tax");
        var me0=IndustryCraftEconomics.Calculate(suggestion with {Blueprint=blueprint with {ME=0}},2,prices,settings,now);
        Check(me0.Materials==240 && me0.JobCost==value.JobCost,"Blueprint ME lowers actual inputs but does not lower the ME0 adjusted-price installation base");
        settings.InstantSale=true;
        var instant=IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now);
        Check(instant.GrossSale==3200 && instant.Broker==0 && instant.Tax==240 && instant.Profit==2717.7m,
            "Instant sale uses station bids and sales tax without charging listing commission");
        settings.InstantSale=false;
        var small=IndustryCraftEconomics.Calculate(suggestion,1,prices,settings,now);
        Check(small.Broker==100 && small.BreakEvenUnit>0,"Small craft batches include the 100 ISK minimum listing fee and a break-even price");
        var outputQuote=prices.Quotes["Jita:10"];prices.Quotes["Jita:10"]=outputQuote with {Sell=small.BreakEvenUnit};
        Check(IndustryCraftEconomics.Calculate(suggestion,1,prices,settings,now).Profit==0,"Calculated break-even prices recover costs after the minimum broker fee and sales tax");
        prices.Quotes["Jita:10"]=outputQuote;
        var combined=IndustryCraftEconomics.Combine(new[]{small,small});
        Check(combined.Broker==120 && combined.Materials==small.Materials*2 && combined.JobCost==small.JobCost*2,
            "Bundled copies keep per-job rounding while sharing one listing fee per finished product");
        var mixed=IndustryCraftEconomics.Combine(new[]{small,small with {ProductTypeId=11}});
        Check(mixed.BreakEvenUnit==null && mixed.Broker==200,"Mixed-product bundles do not invent a shared per-item break-even price");
        var old=prices.Quotes["Jita:34"];prices.Quotes["Jita:34"]=old with {ExpiresUtc=now.AddSeconds(-1)};
        Check(IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now).Profit==null,"Expired input quotes cannot create a profitable craft recommendation");
        prices.Quotes["Jita:34"]=old;
        settings.FacilityTaxPercent=null;
        Check(IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now).Profit==null,"Unknown facility tax leaves installation cost and profit pending instead of assuming zero");
        settings.FacilityTaxPercent=1;prices.AdjustedExpiresUtc=now.AddSeconds(-1);
        Check(IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now).JobCost==null,"Expired adjusted reference prices invalidate job estimates");
        prices.AdjustedExpiresUtc=now.AddHours(1);
        prices.Quotes["Jita:10"]=prices.Quotes["Jita:10"] with {SellVolume=1};
        Check(IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now).Note.Contains("Top-price volume"),"Craft estimates disclose when a batch exceeds quoted top-price volume");
        settings.ExtraPerRun=5000;
        var loss=IndustryCraftEconomics.Calculate(suggestion,2,prices,settings,now);
        Check(loss.Profit<0 && loss.Status=="PROJECTED LOSS" && loss.Color!=value.Color,"All-in losses use a distinct red assessment even when materials are fully owned");
        Check(IndustryCraftEconomics.Calculate(suggestion,11,prices,settings,now).Profit==null,"Economic estimates reject runs beyond the selected copy's remaining plan capacity");
        bool rejected=false;settings.TaxPercent=100;try{IndustryCraftEconomics.Calculate(suggestion,1,prices,settings,now);}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"Invalid market fees cannot produce divided-by-zero margins or misleading profit");

        Task.Run(async()=>
        {
            var handler=new CraftPricingHandler();
            var path=Path.Combine(folder,"craft-prices");
            var service=new IndustryService(new EveSsoService(),path,new HttpClient(handler));
            var result=await service.RefreshCraftPricesAsync(new[]{34,35,10,34},"Jita","Fixture",CancellationToken.None);
            Check(result.Updated==3 && result.Failed==0 && service.State.CraftPricing.Systems["FIXTURE"]==51,
                "Craft price refresh deduplicates types and resolves the manufacturing solar system");
            var saved=service.State.CraftPricing;
            Check(saved.Quotes["Jita:34"].Sell==10 && saved.Quotes["Jita:34"].Buy==8 && saved.AdjustedPrices[34]==5 && saved.ManufacturingIndices[51]==.1m,
                "ESI price extraction separates station bids/asks from adjusted input values and manufacturing indices");
            int calls=handler.Calls;
            await service.RefreshCraftPricesAsync(new[]{34,35,10},"Jita","Fixture",CancellationToken.None);
            Check(handler.Calls==calls,"Repeated craft price loads reuse server-expiry caches without extra ESI calls");
            await service.QuoteAsync(recipe,CancellationToken.None);
            Check(handler.Calls==calls && service.State.Quotes[34].Sell==10,"The original material planner reuses the same craft quote cache");
            await service.RefreshCraftPricesAsync(new[]{10},"Amarr","",CancellationToken.None);
            Check(saved.Quotes["Amarr:10"].Sell==2000 && saved.Quotes["Jita:10"].Sell==1000,"Hub-specific cache keys prevent Amarr prices contaminating Jita estimates");
            var retained=saved.Quotes["Jita:34"] with {ExpiresUtc=now.AddSeconds(-1)};saved.Quotes["Jita:34"]=retained;
            handler.BrokenPage=true;
            result=await service.RefreshCraftPricesAsync(new[]{34},"Jita","",CancellationToken.None);
            Check(result.Failed==1 && saved.Quotes["Jita:34"]==retained,"Incomplete multi-page market reads retain old quotes and never publish a partial best price");
            handler.BrokenPage=false;handler.Hold=true;
            using var cancel=new CancellationTokenSource();
            var waiting=service.RefreshCraftPricesAsync(new[]{777},"Jita","",cancel.Token);cancel.Cancel();
            bool cancelled=false;try{await waiting;}catch(OperationCanceledException){cancelled=true;}
            handler.Hold=false;
            result=await service.RefreshCraftPricesAsync(new[]{34},"Jita","",CancellationToken.None);
            Check(cancelled && result.Updated==1,"Cancelling a pricing scan releases the shared price gate for later refreshes");
            saved.Settings=new(){SystemId=51,SystemName="Fixture",FacilityTaxPercent=.25m,TaxPercent=3.375m,BrokerPercent=1.5m};service.Save();
            var reopened=new IndustryService(new EveSsoService(),path,new HttpClient(handler));
            Check(reopened.State.CraftPricing.Settings.TaxPercent==3.375m && reopened.State.CraftPricing.Quotes["Jita:34"].Sell==10,
                "Craft pricing, selected system and fee assumptions survive restart in the existing Industry store");
        }).GetAwaiter().GetResult();
    }
    private sealed class CraftPricingHandler:HttpMessageHandler
    {
        public int Calls;public bool BrokenPage,Hold;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if(Hold)await Task.Delay(Timeout.Infinite,ct);
            string path=request.RequestUri!.AbsolutePath,query=request.RequestUri.Query;string body;
            if(path=="/universe/ids")body="{\"systems\":[{\"id\":51,\"name\":\"Fixture\"}]}";
            else if(path=="/markets/prices")body="[{\"type_id\":34,\"adjusted_price\":5},{\"type_id\":35,\"adjusted_price\":10}]";
            else if(path=="/industry/systems")body="[{\"solar_system_id\":51,\"cost_indices\":[{\"activity\":\"manufacturing\",\"cost_index\":0.1},{\"activity\":\"researching_material_efficiency\",\"cost_index\":0.9}]}]";
            else
            {
                if(BrokenPage && query.Contains("page=2"))return new(HttpStatusCode.ServiceUnavailable);
                bool amarr=path.Contains("10000043");long station=amarr?60008494:60003760;
                decimal ask=query.Contains("type_id=34&")?10:query.Contains("type_id=35&")?20:amarr?2000:1000;
                body=JsonSerializer.Serialize(new[]{new{location_id=station,price=ask,volume_remain=10000,is_buy_order=false},
                    new{location_id=station,price=ask*.8m,volume_remain=10000,is_buy_order=true},
                    new{location_id=999L,price=.01m,volume_remain=10000,is_buy_order=false}});
            }
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)};
            response.Headers.Add("X-Pages",BrokenPage?"2":"1");response.Content.Headers.Expires=DateTimeOffset.UtcNow.AddMinutes(20);
            return response;
        }
    }
}
