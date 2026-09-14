using System.Text.Json;
using System.Runtime.CompilerServices;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

// Read-only, bounded local simulation. Never replaces the ESI snapshot.
public static class PlanetaryProjection
{
    public sealed class Result
    {
        public Dictionary<long, Dictionary<int,double>> Stores { get; init; } = new();
        public bool Limited { get; set; }
    }
    private sealed class Cache { public string Json=""; public long Bucket; public DateTimeOffset Start; public Result? Value; }
    private static readonly ConditionalWeakTable<PiColony,Cache> CacheByColony = new();
    private static double N(JsonElement p,string k)=>p.TryGetProperty(k,out var v)&&v.TryGetDouble(out var n)?n:0;
    private static DateTimeOffset? D(JsonElement p,string k)=>p.TryGetProperty(k,out var v)&&v.TryGetDateTimeOffset(out var d)?d:null;
    public static Result Build(PiColony colony,DateTimeOffset now)
    {
        var cache=CacheByColony.GetOrCreateValue(colony); string json=colony.Layout.GetRawText(); long bucket=now.UtcTicks/TimeSpan.TicksPerSecond/30;
        if(cache.Value!=null&&cache.Json==json&&cache.Bucket==bucket&&cache.Start==colony.LastUpdate)return cache.Value;
        var result=Simulate(colony,now);
        cache.Json=json;cache.Bucket=bucket;cache.Start=colony.LastUpdate;cache.Value=result;return result;
    }
    private static Result Simulate(PiColony colony,DateTimeOffset now)
    {
        var pins=colony.Layout.GetProperty("pins").EnumerateArray().ToDictionary(p=>(long)N(p,"pin_id"));
        var routes=colony.Layout.TryGetProperty("routes",out var rr)?rr.EnumerateArray().ToArray():Array.Empty<JsonElement>();
        var result=new Result { Stores=pins.ToDictionary(p=>p.Key,p=>p.Value.TryGetProperty("contents",out var c)?c.EnumerateArray().GroupBy(x=>(int)N(x,"type_id")).ToDictionary(g=>g.Key,g=>g.Sum(x=>N(x,"amount"))):new Dictionary<int,double>()) };
        var stores=result.Stores;
        if(colony.LastUpdate==default||now<=colony.LastUpdate)return result;
        var end=now; if(end-colony.LastUpdate>TimeSpan.FromDays(30)){end=colony.LastUpdate.AddDays(30);result.Limited=true;}
        var factories=new Dictionary<long,PiRecipe>();
        foreach(var p in pins){int id=(int)N(p.Value,"schematic_id");if(id==0&&p.Value.TryGetProperty("factory_details",out var f))id=(int)N(f,"schematic_id");if(PlanetaryAnalysis.Recipes.TryGetValue(id,out var r)&&r.Cycle>0)factories[p.Key]=r;}
        double Capacity(long id)=>PlanetaryAnalysis.Types.GetValueOrDefault((int)N(pins[id],"type_id"))?.Capacity??0;
        double Volume(int id)=>PlanetaryAnalysis.Types.GetValueOrDefault(id)?.Volume??0;
        void RouteOutput(long source,int type,double amount)
        {
            foreach(var r in routes.Where(r=>(long)N(r,"source_pin_id")==source&&(int)N(r,"content_type_id")==type))
            {
                long dest=(long)N(r,"destination_pin_id");if(!stores.ContainsKey(dest))continue;
                double moved=Math.Min(amount,N(r,"quantity")>0?N(r,"quantity"):amount);
                if(Capacity(dest)>0&&Volume(type)>0)moved=Math.Min(moved,Math.Max(0,Capacity(dest)-stores[dest].Sum(x=>x.Value*Volume(x.Key)))/Volume(type));
                else if(factories.TryGetValue(dest,out var consumer))moved=Math.Min(moved,Math.Max(0,consumer.Inputs.GetValueOrDefault(type)-stores[dest].GetValueOrDefault(type)));
                else continue;
                stores[dest][type]=stores[dest].GetValueOrDefault(type)+moved;amount-=moved;if(amount<=0)break;
            }
            // Preserve undelivered output, but do not advertise processor inventory as collectable.
            if(amount>0)stores[source][type]=stores[source].GetValueOrDefault(type)+amount;
        }
        var events=new PriorityQueue<long,long>();var running=new HashSet<long>();
        var at=colony.LastUpdate;
        foreach(var f in factories)
        {
            var last=D(pins[f.Key],"last_cycle_start");
            if(last is {} started&&started<=at&&started.AddSeconds(f.Value.Cycle)>at){events.Enqueue(f.Key,started.AddSeconds(f.Value.Cycle).UtcTicks);running.Add(f.Key);}
        }
        foreach(var p in pins.Where(p=>p.Value.TryGetProperty("extractor_details",out _)))
        {
            var details=p.Value.GetProperty("extractor_details");double cycle=N(details,"cycle_time");var start=D(p.Value,"last_cycle_start")??D(p.Value,"install_time");var expiry=D(p.Value,"expiry_time");
            if(cycle<=0||start==null||expiry==null||expiry<=at)continue;
            var next=start.Value.AddSeconds((Math.Max(0,Math.Floor((at-start.Value).TotalSeconds/cycle))+1)*cycle);
            if(next<=expiry)events.Enqueue(p.Key,next.UtcTicks);
        }
        void StartFactories()
        {
            foreach(var f in factories.OrderBy(f=>f.Key))
            {
                if(running.Contains(f.Key))continue;
                var plan=new List<(long source,int type,double amount)>();bool supplied=true;
                foreach(var input in f.Value.Inputs)
                {
                    double need=input.Value;
                    var sources=new[]{f.Key}.Concat(routes.Where(r=>(long)N(r,"destination_pin_id")==f.Key&&(int)N(r,"content_type_id")==input.Key).Select(r=>(long)N(r,"source_pin_id"))).Distinct();
                    foreach(long source in sources)
                    {
                        if(!stores.TryGetValue(source,out var content))continue;
                        double take=Math.Min(need,content.GetValueOrDefault(input.Key));if(take>0)plan.Add((source,input.Key,take));need-=take;if(need<=0)break;
                    }
                    if(need>0){supplied=false;break;}
                }
                // Do not invent production for unconfigured output routes.
                if(!supplied||!f.Value.Outputs.Keys.All(t=>routes.Any(r=>(long)N(r,"source_pin_id")==f.Key&&(int)N(r,"content_type_id")==t)))continue;
                foreach(var use in plan)stores[use.source][use.type]-=use.amount;
                running.Add(f.Key);events.Enqueue(f.Key,at.AddSeconds(f.Value.Cycle).UtcTicks);
            }
        }
        StartFactories();int count=0;
        while(events.TryPeek(out _,out long ticks)&&ticks<=end.UtcTicks)
        {
            if(++count>200000){result.Limited=true;break;}
            at=new DateTimeOffset(ticks,TimeSpan.Zero);
            var completed=new List<long>();while(events.TryPeek(out _,out long same)&&same==ticks)completed.Add(events.Dequeue());
            foreach(long id in completed)
            {
                if(factories.TryGetValue(id,out var factory)){running.Remove(id);foreach(var output in factory.Outputs)RouteOutput(id,output.Key,output.Value);}
                else if(pins[id].TryGetProperty("extractor_details",out var ex))
                {
                    RouteOutput(id,(int)N(ex,"product_type_id"),N(ex,"qty_per_cycle"));
                    var next=at.AddSeconds(N(ex,"cycle_time"));if(next<=D(pins[id],"expiry_time"))events.Enqueue(id,next.UtcTicks);
                }
            }
            StartFactories();
        }
        return result;
    }
}
