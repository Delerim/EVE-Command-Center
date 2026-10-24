using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckSellerDashboard()
    {
        var now=DateTimeOffset.UtcNow;
        var parent=new EveAssetItem{ItemId=200,TypeId=3465,IsSingleton=true,Quantity=1,
            LocationId=SellerDashboardService.Jita44,LocationType="station",LocationFlag="Hangar"};
        var tenCores=new EveAssetItem{ItemId=201,TypeId=12345,Quantity=9,LocationId=200,LocationType="item",LocationFlag="Unlocked"};
        var skins=new EveAssetItem{ItemId=202,TypeId=54321,Quantity=10,LocationId=200,LocationType="item",LocationFlag="Unlocked"};
        var nested=new EveAssetItem{ItemId=203,TypeId=3465,IsSingleton=true,Quantity=1,LocationId=200,LocationType="item",LocationFlag="Unlocked"};
        var pi=new EveAssetItem{ItemId=204,TypeId=12346,Quantity=100,LocationId=203,LocationType="item",LocationFlag="Unlocked"};
        var fitted=new EveAssetItem{ItemId=205,TypeId=12346,Quantity=1,LocationId=200,LocationType="item",LocationFlag="HiSlot0"};
        var assets=new[]{parent,tenCores,skins,nested,pi,fitted};
        var types=new Dictionary<int,SellerTypeInfo>{
            [3465]=new(3465,"Giant Secure Container",12,23,"Accessories"),
            [12345]=new(12345,"Industrial Core II",1,7,"Module"),
            [54321]=new(54321,"Decorative SKIN",2,91,"SKINs"),
            [12346]=new(12346,"Robotics",3,43,"Planetary Commodities")};
        var orders=new List<MarketOrder>{new(){OrderId=9000,TypeId=12345,
            LocationId=SellerDashboardService.Jita44,Remaining=1,Total=1,Price=10000000m,
            RegionId=SellerDashboardService.Forge,Issued=now,Duration=30}};
        var current=new Dictionary<int,SellerHubQuote>{
            [12345]=new(12345,9000000m,10500000m,now),
            [12346]=new(12346,88000m,100000m,now)};
        var before=new Dictionary<int,SellerHubQuote>{
            [12345]=new(12345,10000000m,9500000m,now.AddMinutes(-30))};
        var locations=new Dictionary<long,string>{{SellerDashboardService.Jita44,"Jita IV - Moon 4 - Caldari Navy Assembly Plant"}};
        var containers=new Dictionary<long,string>{{200,"Sales Stock"},{203,"PI Output"}};
        var rows=SellerStockEngine.Build(assets,orders,types,locations,containers,current,before,true,now);
        Check(rows.Count==2,"Seller tracks only sellable stock and excludes containers, SKINs and fitted items");
        var core=rows.Single(x=>x.TypeId==12345);
        Check(core.Stock==9 && core.Listed==1 && core.OpenOrders==1,
            "Nine Industrial Cores remain in inventory while one is already listed; no double subtraction");
        Check(core.State=="UNDERCUT" && core.PreferredHub=="AMARR" && core.JitaShift.StartsWith("-"),
            "An undercut Jita order and higher Amarr price are distinct actionable signals");
        var robotics=rows.Single(x=>x.TypeId==12346);
        Check(robotics.Stock==100 && robotics.Listed==0 && robotics.State=="NOT LISTED" &&
            robotics.ContainerPath.Contains("PI Output"),
            "Nested PI containers are resolved and stock without active orders is highlighted");
        var stale=SellerStockEngine.Build(assets,orders,types,locations,containers,current,before,false,now);
        Check(stale.All(x=>x.State=="ORDERS STALE"),
            "Stale orders never masquerade as unlisted or undercut market stock");
        Check(!SellerStockEngine.IsSellable(new(7,"Some Pretty Coat",1,15,"Apparel")) &&
            !SellerStockEngine.IsSellable(new(8,"SKINR Nanocoating",1,77,"Commodity")) &&
            SellerStockEngine.IsSellable(new(9,"Capital Ship Maintenance Bay",1,7,"Module")),
            "Cosmetics excluded by category and name without hiding real station modules");
        var cyclic=new EveAssetItem{ItemId=400,TypeId=12345,Quantity=1,LocationId=401,LocationFlag="Unlocked",LocationType="item"};
        var loop=new EveAssetItem{ItemId=401,TypeId=12345,Quantity=1,LocationId=400,LocationFlag="Unlocked",LocationType="item"};
        var dictionary=new[]{cyclic,loop}.ToDictionary(x=>x.ItemId);
        Check(!SellerStockEngine.TryGetHangarPath(cyclic,dictionary,locations,containers,out _,out _),
            "Corrupt cyclic ESI container paths cannot create sellable inventory");
        Check(robotics.JitaText=="88,000.00" && robotics.AmarrText=="100,000.00",
            "Station prices show currency precision instead of ambiguous region-wide averages");
    }
}
