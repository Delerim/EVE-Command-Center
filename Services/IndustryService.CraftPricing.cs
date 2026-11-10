using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed partial class IndustryService
{
    private readonly SemaphoreSlim _craftQuoteGate=new(1,1);
    public sealed record PricingRefresh(int Updated, int Failed, string ReferenceError);
    private async Task<HttpResponseMessage> CraftResponse(string path,CancellationToken ct,object? body=null)
    {
        using var request=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,"https://esi.evetech.net"+path);
        request.Headers.UserAgent.ParseAdd("EVECommandCenter-Industry/3.8.11");
        request.Headers.TryAddWithoutValidation("X-Compatibility-Date","2026-08-25");
        if(body!=null)request.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
        var response=await _http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode) {response.Dispose();throw new HttpRequestException("EVE pricing request failed.");}
        return response;
    }
    public async Task<long> ResolveCraftSystemAsync(string name,CancellationToken ct)
    {
        string key=name.Trim().ToUpperInvariant();
        if(key.Length==0)throw new InvalidOperationException("Enter your manufacturing solar system.");
        if(State.CraftPricing.Systems.TryGetValue(key,out long existing))return existing;
        using var response=await CraftResponse("/universe/ids",ct,new[]{name.Trim()});
        using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if(!document.RootElement.TryGetProperty("systems",out var systems))throw new InvalidOperationException("Manufacturing system not found. Enter its exact solar-system name.");
        var matches=systems.EnumerateArray().Where(s=>s.GetProperty("name").GetString()!.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1 || !matches[0].GetProperty("id").TryGetInt64(out long id) || id<=0)throw new InvalidOperationException("Manufacturing system could not be resolved uniquely.");
        State.CraftPricing.Systems[key]=id;return id;
    }
    private async Task RefreshCraftReference(CancellationToken ct)
    {
        var state=State.CraftPricing;
        if(state.AdjustedExpiresUtc<=DateTimeOffset.UtcNow)
        {
            using var response=await CraftResponse("/markets/prices",ct);
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var prices=new Dictionary<int,decimal>();
            foreach(var row in doc.RootElement.EnumerateArray())
                if(row.TryGetProperty("adjusted_price",out var value) && value.TryGetDecimal(out var adjusted) && adjusted>0)
                    prices[row.GetProperty("type_id").GetInt32()]=adjusted;
            if(prices.Count==0)throw new InvalidDataException("No adjusted industry prices returned.");
            state.AdjustedPrices=prices;state.AdjustedExpiresUtc=MarketOrdersService.RefreshDue(response,DateTimeOffset.UtcNow,TimeSpan.FromHours(1));
        }
        if(state.IndicesExpiresUtc<=DateTimeOffset.UtcNow)
        {
            using var response=await CraftResponse("/industry/systems",ct);
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var indices=new Dictionary<long,decimal>();
            foreach(var row in doc.RootElement.EnumerateArray())
                foreach(var activity in row.GetProperty("cost_indices").EnumerateArray())
                    if(activity.GetProperty("activity").GetString()=="manufacturing" && activity.GetProperty("cost_index").TryGetDecimal(out decimal index) && index>=0 && index<=1)
                        indices[row.GetProperty("solar_system_id").GetInt64()]=index;
            if(indices.Count==0)throw new InvalidDataException("No manufacturing cost indices returned.");
            state.ManufacturingIndices=indices;state.IndicesExpiresUtc=MarketOrdersService.RefreshDue(response,DateTimeOffset.UtcNow,TimeSpan.FromHours(1));
        }
    }
    private async Task<CraftMarketQuote> ReadCraftQuote(string hub,int type,CancellationToken ct)
    {
        int region=hub=="Jita"?MiningMarketService.TheForgeRegionId:MiningMarketService.DomainRegionId;
        long station=hub=="Jita"?MiningMarketService.Jita44StationId:MiningMarketService.AmarrEmperorFamilyStationId;
        decimal? buy=null,sell=null;long buyVolume=0,sellVolume=0;
        int pages=1;DateTimeOffset expires=DateTimeOffset.MaxValue;DateTimeOffset? modified=null;
        for(int page=1;page<=pages;page++)
        {
            using var response=await CraftResponse($"/markets/{region}/orders?order_type=all&type_id={type}&page={page}",ct);
            if(!response.Headers.TryGetValues("X-Pages",out var values)||!int.TryParse(values.SingleOrDefault(),out int count)||count<1||count>100 || page>1 && count!=pages)
                throw new InvalidDataException("Incomplete/changing market orderbook; previous quote retained.");
            pages=count;
            if(page==1)modified=response.Content.Headers.LastModified;
            else if(modified.HasValue && response.Content.Headers.LastModified.HasValue && modified!=response.Content.Headers.LastModified)
                throw new InvalidDataException("Market snapshot changed during pagination; previous quote retained.");
            expires=new[]{expires,MarketOrdersService.RefreshDue(response,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5))}.Min();
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach(var row in doc.RootElement.EnumerateArray())
            {
                if(row.GetProperty("location_id").GetInt64()!=station)continue;
                decimal price=row.GetProperty("price").GetDecimal();long volume=row.GetProperty("volume_remain").GetInt64();
                if(price<=0 || volume<=0)throw new InvalidDataException("Invalid market price or volume.");
                if(row.GetProperty("is_buy_order").GetBoolean())
                {
                    if(!buy.HasValue || price>buy){buy=price;buyVolume=volume;}
                    else if(price==buy)buyVolume=checked(buyVolume+volume);
                }
                else
                {
                    if(!sell.HasValue || price<sell){sell=price;sellVolume=volume;}
                    else if(price==sell)sellVolume=checked(sellVolume+volume);
                }
            }
        }
        return new(buy,sell,buyVolume,sellVolume,DateTimeOffset.UtcNow,expires);
    }
    public async Task<PricingRefresh> RefreshCraftPricesAsync(IEnumerable<int> types,string hub,string system,CancellationToken ct,IProgress<string>? progress=null)
    {
        if(hub is not ("Jita" or "Amarr"))throw new InvalidOperationException("Choose Jita or Amarr.");
        await _craftQuoteGate.WaitAsync(ct);
        var received=new ConcurrentDictionary<int,CraftMarketQuote>();
        int failed=0,done=0;string referenceError="";
        try
        {
            progress?.Report("Loading industry reference prices and system cost indices...");
            try {await RefreshCraftReference(ct);if(!string.IsNullOrWhiteSpace(system))await ResolveCraftSystemAsync(system,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){referenceError=ex.Message;}
            var due=types.Where(t=>t>0).Distinct().Where(t=>!State.CraftPricing.Quotes.TryGetValue(IndustryCraftEconomics.Key(hub,t),out var q)||!IndustryCraftEconomics.Fresh(q,DateTimeOffset.UtcNow)).ToArray();
            using var gate=new SemaphoreSlim(4,4);
            await Task.WhenAll(due.Select(async type=>
            {
                await gate.WaitAsync(ct);
                try {received[type]=await ReadCraftQuote(hub,type,ct);}
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch {Interlocked.Increment(ref failed);}
                finally {int completed=Interlocked.Increment(ref done);progress?.Report($"{hub} station quotes: {completed:N0}/{due.Length:N0} types checked; {failed:N0} deferred.");gate.Release();}
            }));
            return new(received.Count,failed,referenceError);
        }
        finally
        {
            try
            {
                foreach(var pair in received)State.CraftPricing.Quotes[IndustryCraftEconomics.Key(hub,pair.Key)]=pair.Value;
                Save();
            }
            finally {_craftQuoteGate.Release();}
        }
    }
}
