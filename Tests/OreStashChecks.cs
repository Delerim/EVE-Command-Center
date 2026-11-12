using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckOreStash(string? renderPath = null)
    {
        var now = DateTimeOffset.UtcNow;
        Check(OreStashEngine.Advice(15) == "SELL" && OreStashEngine.Advice(15.01m) == "ASSESS" &&
            OreStashEngine.Advice(19.99m) == "ASSESS" && OreStashEngine.Advice(20) == "DON'T SELL",
            "Balanced advice accepts a fair 15% gap and rejects the user's 10b-for-8b scenario");
        EveAssetItem Item(long id, int type, long qty, long location, string flag = "Hangar", string locationType = "station") =>
            new() { ItemId = id, TypeId = type, Quantity = qty, LocationId = location, LocationFlag = flag, LocationType = locationType };
        var items = new[] {
            Item(1,1,100,60000001), Item(2,100,1,60000001), Item(3,100,1,2,"Unlocked","item"), Item(4,1,200,3,"Unlocked","item"),
            Item(5,200,1,60000002), Item(6,1,50,5,"Cargo","item"), Item(7,1,25,777,"SpecializedOreHold","item"),
            Item(8,1,10000,5,"HiSlot0","item"), Item(9,1,10000,60000001,"CorpSAG1"), Item(10,2,40,60000001),
            Item(11,1,10000,888,"Cargo","item"), Item(20,100,1,21,"Unlocked","item"), Item(21,100,1,20,"Unlocked","item"),
            Item(22,1,10000,20,"Unlocked","item"), Item(1,1,100,60000001)
        };
        var types = new Dictionary<int,OreWatchType> { [1] = new(1,"Compressed Bitumens"), [2] = new(2,"Bitumens") };
        var snapshot = new OreAssetSnapshot(42, items, now, now.AddMinutes(30), 777);
        var positions = OreStashEngine.Positions(snapshot, types);
        Check(positions.Where(x => x.TypeId == 1).Sum(x => x.Quantity) == 375 && positions.Count == 5,
            "Hangars, nested containers, parked ship cargo and current ship ore hold are counted once");
        Check(!positions.Any(x => x.Quantity == 10000), "Fitted slots, corporation flags, unresolved cargo and container cycles are excluded");
        var book = new OreOrderBook(1, new[] { new OreBookOrder(95,1000) }, new[] { new OreBookOrder(100,1000) }, now, now.AddMinutes(10));
        var books = new Dictionary<int,OreOrderBook> { [1] = book, [2] = book with {TypeId=2} };
        var settings = new OreWatchSettings(42,true);
        var rows = OreStashEngine.Evaluate(snapshot, positions, books, settings, now);
        Check(rows.Single(x => x.TypeId==1).Advice=="SELL" && rows.Single(x => x.TypeId==1).Quantity==375 && rows.Single(x => x.TypeId==1).GapPercent==14.5m,
            "Advice values the actual combined stash quantity and accepts a reasonable corp payout");
        Check(rows.Single(x=>x.TypeId==2).Advice=="ASSESS" && !rows.Single(x=>x.TypeId==2).DataVerified,
            "Unmapped raw ore asks for a compression assessment rather than using an unverified conversion");
        var mappedTypes = new Dictionary<int,OreWatchType>(types) { [2] = new(2,"Bitumens",1,"Compressed Bitumens") };
        var mapped = OreStashEngine.Evaluate(snapshot,OreStashEngine.Positions(snapshot,mappedTypes),books,settings,now);
        Check(mapped.Count==1 && mapped[0].Quantity==415 && mapped[0].UncompressedUnits==40 && mapped[0].Advice=="SELL" && mapped[0].Reason.Contains("After compressing"),
            "Verified modern raw ore can qualify after compression and is combined one-to-one with existing compressed units");
        var limited = new Dictionary<int,OreOrderBook>(books) { [1] = book with { Bids = new[]{new OreBookOrder(95,400)} } };
        Check(OreStashEngine.Evaluate(snapshot,OreStashEngine.Positions(snapshot,mappedTypes),limited,settings,now).Single().Advice=="ASSESS",
            "Raw plus compressed stock share one order-depth check, preventing duplicate use of the same liquidity");
        books[1] = book with { Bids = new[] { new OreBookOrder(95,100) } };
        Check(OreStashEngine.Evaluate(snapshot,positions,books,settings,now).Single(x=>x.TypeId==1).Advice=="ASSESS",
            "Insufficient bid depth for the held volume prevents a false selling point");
        books[1] = book with { Bids = new[] { new OreBookOrder(95,1000,500) } };
        Check(OreStashEngine.Evaluate(snapshot,positions,books,settings,now).Single(x=>x.TypeId==1).Advice=="ASSESS",
            "Buy-order minimum quantities are respected when checking stash liquidity");
        Check(new OreOrderBook(1,new[]{new OreBookOrder(100,100),new OreBookOrder(90,200)},Array.Empty<OreBookOrder>(),now,now).ImmediateBuyValue(200)==19000,
            "Immediate Jita bid value consumes price levels instead of multiplying the whole stash by the best bid");
        books[1] = book with {ExpiresUtc=now.AddMinutes(-10)};
        Check(OreStashEngine.Evaluate(snapshot,positions,books,settings,now).All(x=>x.Advice=="ASSESS"), "Expired market data never issues SELL");
        books[1] = book;
        Check(OreStashEngine.Evaluate(snapshot with {ExpiresUtc=now.AddMinutes(-10)},positions,books,settings,now).All(x=>x.Advice=="ASSESS"),
            "Expired inventory cannot trigger selling advice");
        Check(OreStashEngine.Evaluate(snapshot with {ObservedUtc=now.AddDays(-1)},positions,books,settings,now).Single(x=>x.TypeId==1).Advice=="SELL",
            "Unchanged inventory with a still-valid provider expiry is not rejected merely because its last-modified time is older");
        bool wrong=false; try { OreStashEngine.Evaluate(snapshot with {CharacterId=43},positions,books,settings,now); } catch(InvalidOperationException){wrong=true;}
        Check(wrong,"A different character's asset snapshot is rejected");

        var sell = rows.Single(x=>x.TypeId==1) with {CorpPayout=200_000_000};
        var memory = new Dictionary<int,OreAlertMemory>();
        Check(OreStashService.ObserveAlerts(new[]{sell},settings,memory,now).Count==1 && OreStashService.ObserveAlerts(new[]{sell},settings,memory,now.AddMinutes(10)).Count==0,
            "One grouped selling opportunity is emitted, with no repeat on each scan");
        var restored = JsonSerializer.Deserialize<Dictionary<int,OreAlertMemory>>(JsonSerializer.Serialize(memory))!;
        Check(OreStashService.ObserveAlerts(new[]{sell},settings,restored,now.AddDays(2)).Count==0,"Alert deduplication survives restart while an ore stays at SELL");
        OreStashService.ObserveAlerts(new[]{sell with {Advice="ASSESS",GapPercent=16}},settings,memory,now.AddHours(25));
        Check(OreStashService.ObserveAlerts(new[]{sell},settings,memory,now.AddHours(26)).Count==0,"Small threshold oscillations do not rearm selling alerts");
        OreStashService.ObserveAlerts(new[]{sell with {Advice="ASSESS",GapPercent=30,DataVerified=false}},settings,memory,now.AddHours(26));
        Check(OreStashService.ObserveAlerts(new[]{sell},settings,memory,now.AddHours(27)).Count==0,"Missing or stale price evidence cannot rearm a selling alert");
        OreStashService.ObserveAlerts(new[]{sell with {Advice="DON'T SELL",GapPercent=22}},settings,memory,now.AddHours(27));
        Check(OreStashService.ObserveAlerts(new[]{sell},settings,memory,now.AddHours(28)).Count==1,"A real move away from SELL and back can produce a new alert after cooldown");
        OreStashService.ObserveAlerts(Array.Empty<OreStashAdvice>(),settings,memory,now.AddHours(29));
        Check(OreStashService.ObserveAlerts(new[]{sell},settings,memory,now.AddHours(30)).Count==0,"Removing and re-adding ore cannot evade the 24-hour cooldown");
        Check(OreStashService.ObserveAlerts(new[]{sell with {CorpPayout=1}},settings,new(),now).Count==0,"Tiny stacks stay below the minimum notification value");
        Check(OreStashService.ObserveAlerts(new[]{sell},settings with {Notifications=false},new(),now).Count==0,"Notification opt-out is respected");

        string folder=Path.Combine(Path.GetTempPath(),"ecc-ore-check-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var source = new FakeOreSource(snapshot,types,books);
            using var service = new OreStashService(source,folder);
            service.Configure(settings); service.RefreshAsync(force:true).GetAwaiter().GetResult();
            Check(service.LastScanSucceeded && source.AssetOwners.SequenceEqual(new[]{42L}) && service.State.Rows.Count==2,
                "Only the selected main character's assets are queried, even with linked alts");
            source.FailAssets=true; service.RefreshAsync(force:true).GetAwaiter().GetResult();
            Check(!service.LastScanSucceeded && service.State.Rows.Count==2 && service.Status.StartsWith("SCAN FAILED"),"A failed refresh keeps the stash but invalidates its advice");
            source.FailAssets=false; service.RefreshAsync(force:true).GetAwaiter().GetResult();
            var dashboard=new MiningDashboardWindow(new StatTrackerService(),new AppSettings());
            var view=(OreStashView)dashboard.FindName("OreStashPanel"); view.AttachAsync(service).GetAwaiter().GetResult(); dashboard.OpenOreStash();
            Check(((DataGrid)view.FindName("OreRows")).Items.Count==2 && ((ComboBox)view.FindName("MainPilot")).SelectedItem is EvePilotProfile p && p.CharacterId==42,
                "The stash tab shows selected main character and their actual ore positions");
            if(renderPath!=null){dashboard.Width=1250;dashboard.Height=780;Render(dashboard,renderPath);} dashboard.Close();
            using var restarted=new OreStashService(source,folder);
            Check(restarted.State.Settings.CharacterId==42 && !restarted.LastScanSucceeded,"Saved monitoring selection survives restart without presenting cached advice as fresh");
            service.Configure(settings with {CharacterId=43});
            Check(service.State.Rows.Count==0 && service.State.Alerts.Count==0,"Changing the main clears the previous character's stash and alert state");
            service.Configure(settings with {Enabled=false}); int calls=source.AssetOwners.Count;
            service.RefreshAsync(force:true).GetAwaiter().GetResult(); Check(source.AssetOwners.Count==calls,"Paused monitoring never queries assets");
            service.Configure(settings with {CharacterId=44}); service.RefreshAsync(force:true).GetAwaiter().GetResult();
            Check(source.AssetOwners.Count==calls && service.Status.Contains("Reconnect"),"Missing asset scope stops scanning before an authenticated asset request");
            var notices=new NotificationCenterService(Path.Combine(folder,"notices"));
            CenterNotification Notice(string detail,string key="point1")=>new(){Id="ORE:42:1",Source="ORE",Title="Ore selling point",Detail=detail,AlertKey=key,Active=true,Updated=now};
            notices.SyncOreAlerts(new[]{Notice("price one")},now); notices.MarkRead(); notices.SyncOreAlerts(new[]{Notice("price two")},now.AddMinutes(10));
            Check(notices.Items.Single().Read,"Price movement within a continuing opportunity does not repeatedly mark a read notification unread");
            notices.SyncOreAlerts(Array.Empty<CenterNotification>(),now); notices.SyncOreAlerts(new[]{Notice("new point","point2")},now);
            Check(!notices.Items.Single().Read && notices.Items.Single().Active,"A genuinely new selling opportunity becomes unread again");
            Task.Run(async () =>
            {
                using var race = new OreStashService(source,Path.Combine(folder,"race"));
                race.Configure(settings);
                source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int popups=0; race.SellingPoint+=_=>popups++;
                var pendingScan=race.RefreshAsync(force:true);
                race.Configure(settings with {CharacterId=43});
                source.Pending.SetResult(snapshot); await pendingScan;
                Check(race.State.Settings.CharacterId==43 && race.State.Rows.Count==0 && popups==0,
                    "An old in-flight scan cannot publish another character's stash after changing main");
                source.Pending=null;
                await race.RefreshAsync(force:true);
                Check(race.LastScanSucceeded && source.AssetOwners.Last()==43,
                    "The next scan queries only the newly selected main");
                await CheckOreStashSource();
            }).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(folder,true); }
    }
    private sealed class FakeOreSource(OreAssetSnapshot snapshot,IReadOnlyDictionary<int,OreWatchType> types,IReadOnlyDictionary<int,OreOrderBook> books):IOreStashSource
    {
        public List<long> AssetOwners {get;}=new(); public bool FailAssets;
        public TaskCompletionSource<OreAssetSnapshot>? Pending;
        public Task<IReadOnlyList<EvePilotProfile>> PilotsAsync()=>Task.FromResult<IReadOnlyList<EvePilotProfile>>(new[]{
            new EvePilotProfile{CharacterId=42,CharacterName="Main pilot",Scopes=new[]{"esi-assets.read_assets.v1"}},
            new EvePilotProfile{CharacterId=43,CharacterName="Alt pilot",Scopes=new[]{"esi-assets.read_assets.v1"}},
            new EvePilotProfile{CharacterId=44,CharacterName="Needs reconnect"}});
        public Task<OreAssetSnapshot> AssetsAsync(EvePilotProfile pilot,CancellationToken ct)
        { AssetOwners.Add(pilot.CharacterId); if(FailAssets)throw new IOException("Asset provider unavailable");return Pending?.Task ?? Task.FromResult(snapshot with {CharacterId=pilot.CharacterId}); }
        public Task<IReadOnlyDictionary<int,OreWatchType>> OreTypesAsync(IEnumerable<int> owned,CancellationToken ct)=>Task.FromResult(types);
        public Task<OreOrderBook> BookAsync(int id,CancellationToken ct)=>Task.FromResult(books[id]);
        public Task<string?> LocationAsync(long id,EvePilotProfile pilot,CancellationToken ct)=>Task.FromResult<string?>("Personal station "+id);
    }
    private static async Task CheckOreStashSource()
    {
        var calls=new List<string>();
        HttpResponseMessage Response(string json,int pages=1)
        {
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)};
            response.Headers.Add("X-Pages",pages.ToString()); response.Headers.Date=DateTimeOffset.UtcNow;
            response.Content.Headers.Expires=DateTimeOffset.UtcNow.AddMinutes(5); return response;
        }
        using var client=new HttpClient(new OreTestHandler(request=>
        {
            string path=request.RequestUri!.PathAndQuery; calls.Add(path);
            if(path.Contains("categories/25"))return Response("{\"groups\":[100]}");
            if(path.Contains("groups/100"))return Response("{\"category_id\":25,\"types\":[1,2]}");
            if(path.Contains("types/1"))return Response("{\"name\":\"Compressed Bitumens\"}");
            if(path.Contains("types/2"))return Response("{\"name\":\"Bitumens\"}");
            if(path.Contains("universe/ids"))return Response("{\"inventory_types\":[{\"id\":1,\"name\":\"Compressed Bitumens\"}]}");
            return path.Contains("page=1")
                ? Response("[{\"type_id\":1,\"location_id\":60003760,\"is_buy_order\":true,\"price\":95,\"volume_remain\":1000,\"min_volume\":10},{\"type_id\":1,\"location_id\":60000001,\"is_buy_order\":true,\"price\":9999,\"volume_remain\":1000}]",2)
                : Response("[{\"type_id\":1,\"location_id\":60003760,\"is_buy_order\":false,\"price\":100,\"volume_remain\":1000}]",2);
        }));
        using var source=new OreStashSource(new EveSsoService(),client);
        var ore=await source.OreTypesAsync(new[]{1,200},CancellationToken.None);
        Check(ore.Count==1 && ore.ContainsKey(1) && !calls.Any(x=>x.Contains("types/200")),
            "Authoritative ore category filters inventory before resolving metadata for non-ore assets");
        ore=await source.OreTypesAsync(new[]{1,2},CancellationToken.None);
        Check(ore[2].MarketTypeId==1 && ore[2].MarketName=="Compressed Bitumens",
            "Raw-to-compressed mapping requires an exact modern name match within the authoritative ore category");
        var book=await source.BookAsync(1,CancellationToken.None);
        Check(book.Buy==95 && book.Sell==100 && book.Bids.Single().Minimum==10 && calls.Count(x=>x.Contains("orders"))==2,
            "All orderbook pages are read and prices are restricted to Jita 4-4, with minimum volume retained");
        await source.BookAsync(1,CancellationToken.None);
        Check(calls.Count(x=>x.Contains("orders"))==2,"Unexpired market snapshots are reused instead of repeatedly querying ESI");
        using var badClient=new HttpClient(new OreTestHandler(_=>Response("[]",26)));
        using var badSource=new OreStashSource(new EveSsoService(),badClient);
        bool refused=false;try{await badSource.BookAsync(1,CancellationToken.None);}catch(InvalidOperationException){refused=true;}
        Check(refused,"An oversized incomplete order book is rejected instead of silently truncating at a page limit");
        using var mismatchClient=new HttpClient(new OreTestHandler(_=>Response("[{\"type_id\":2}]")));
        using var mismatchSource=new OreStashSource(new EveSsoService(),mismatchClient);
        refused=false;try{await mismatchSource.BookAsync(1,CancellationToken.None);}catch(InvalidOperationException){refused=true;}
        Check(refused,"A market response for another item cannot price the selected ore");
    }
    private sealed class OreTestHandler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(respond(request)); }
}
