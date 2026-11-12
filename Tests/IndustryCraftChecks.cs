using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckCraftSuggestions(string folder, string? renderPath = null)
    {
        CheckCraftEconomics(folder);
        var now = DateTimeOffset.UtcNow;
        var recipes = new List<IndustryRecipe> {
            new() { Blueprint=100, Activity="manufacturing", Products=new(){[10]=2}, Materials=new(){[34]=6} },
            new() { Blueprint=200, Activity="manufacturing", Products=new(){[11]=1}, Materials=new(){[34]=6} }
        };
        var a = new IndustryBlueprintLibrary.Blueprint(42,"Builder",9001,100,60003760,"Materials can",0,0,2,true,true,"Available");
        var b = a with { ItemId=9002, TypeId=200 };
        var stock = new[] { new IndustryStockSource(42,"Builder",9010,34,60003760,"Hangar","Station / can",12,now,true,"Observed") };
        var empty = new IndustryWorkspace();
        var suggestions = IndustryCraftSuggestions.Read(new[]{a,b},empty,stock,now,recipes);
        Check(suggestions.All(s=>s.MaxRuns==2) && suggestions.Single(s=>s.Blueprint==a).MaxOutput==4,
            "Craft alternatives respect BPC runs and multi-unit output quantities");
        var selections = new[]{new IndustryCraftSuggestions.Selection(a,1),new IndustryCraftSuggestions.Selection(b,1)};
        bool Reject(Action action) { try { action(); return false; } catch(InvalidOperationException) { return true; } }
        Check(Reject(()=>IndustryCraftSuggestions.Plan(selections.Select(s=>s with { Runs=2 }).ToArray(),empty,stock,now,recipes)),
            "Independently craftable BPCs cannot double spend shared materials in a bundle");
        var plan = IndustryCraftSuggestions.Plan(selections,empty,stock,now,recipes);
        var auto=IndustryCraftSuggestions.SuggestBundle(suggestions,2,empty,stock,now);
        Check(auto.Count==1 && IndustryCraftSuggestions.Plan(auto,empty,stock,now,recipes).Allocations.Sum(a=>a.Quantity)==12,
            "Automatic compatible-bundle selection subtracts shared stock rather than selecting conflicting alternatives");
        Check(plan.Nodes.Count(n=>n.ParentId==null)==2 && plan.Allocations.Sum(x=>x.Quantity)==12 && plan.Nodes.Where(n=>n.ParentId!=null).All(n=>n.Strategy=="Use Stock"),
            "Craft bundle consumes direct owned inputs and keeps each BPC as a separate build");
        Check(IndustryCraftSuggestions.Material(3,2,10)==6 && IndustryCraftSuggestions.Material(1,10,10)==10,
            "Craft suggestions use batch ME rounding and the one-per-run input minimum");
        Check(IndustryCraftSuggestions.Read(new[]{a with { Available=false }},empty,stock,now,recipes).Count==0 &&
            IndustryCraftSuggestions.Read(new[]{a},empty,new[]{stock[0] with { SnapshotUtc=now.AddHours(-2) }},now,recipes).Single().MaxRuns==0,
            "Unavailable BPCs and stale stock never produce available-to-craft claims");
        var near = IndustryCraftSuggestions.Read(new[]{a},empty,new[]{stock[0] with { Quantity=3 }},now,recipes).Single();
        Check(near.MaxRuns==0 && near.Coverage==0.5m && near.Inputs.Contains("3 free"),
            "Suggested projects expose material coverage and gaps for incomplete builds");
        var path=Path.Combine(folder,"craft-bundles");
        var store=new IndustryWorkspaceStore(path);
        var linked=new HashSet<long>{42};
        Check(Reject(()=>store.CreateCraftBundle("Too large",selections.Select(s=>s with { Runs=2 }).ToArray(),new[]{a,b},stock,linked,now,recipes)) && store.Snapshot().Projects.Count==0,
            "Infeasible craft project writes neither partial projects nor reservations");
        Guid id=store.CreateCraftBundle("Two blueprints, one project",selections,new[]{a,b},stock,linked,now,recipes);
        var saved=new IndustryWorkspaceStore(path).Snapshot();
        Check(saved.SchemaVersion==5 && saved.Projects.Single().IsBundle && saved.Projects.Single().Nodes.Count(n=>n.ParentId==null)==2 && saved.Reservations.Sum(r=>r.Quantity)==12,
            "One craft project and all exact-stack reservations survive restart atomically");
        Check(IndustryProductionProgress.Stage(saved.Projects.Single(),saved,Array.Empty<IndustryJobLink>(),now)=="Gathering materials" &&
            IndustryShoppingList.Build(saved,stock,now).Single().Unreserved==0,
            "Bundle progress and consolidated procurement handle all output roots");
        Check(IndustryCraftSuggestions.Read(new[]{a,b},saved,stock,now,recipes).All(s=>s.MaxRuns==0 && s.RemainingRuns==1),
            "Saved craft projects subtract both reserved stock and planned runs from later suggestions");
        var moved = new[]{stock[0] with { ItemId=9011 }};
        Check(IndustryCraftSuggestions.Read(new[]{a},saved,moved,now,recipes).Single().MaxRuns==0,
            "Moved or missing reservation sources block matching replacement stock until reviewed");
        var outside = new[]{stock[0],stock[0] with { ItemId=9020,Quantity=20,LocationId=60008494 }};
        Check(IndustryCraftSuggestions.Read(new[]{a},saved,outside,now,recipes,new HashSet<(long,long)>{(42,9020)}).Single().MaxRuns==1,
            "Source filters retain full reservation evidence while using only the selected can's free stock");
        new IndustryLedger(path).Ensure();
        Check(new IndustryWorkspaceStore(path).Snapshot().Projects.Single().Id==id,
            "Craft bundle migrates into the existing shared financial ledger without changing identity");
        var orders=new MarketOrdersService(new EveSsoService(),path);
        Check(orders.SaleProjects(10).Single().Id==id && orders.SaleProjects(11).Single().Id==id,
            "Sales of either bundled output can be attributed to the same project");

        // Real catalog + nested can fixture drives the embedded controls and visual audit.
        var recipe=IndustryCatalog.Recipes.First(r=>r.Activity=="manufacturing" && r.Products.Count==1 && r.Materials.Count>0 && r.Materials.All(m=>m.Value>0 && m.Value<1e8));
        var pilot=new IndustryPilot {Id=42,Name="Builder",Updated=now,Error="",
            Blueprints=new(){JsonSerializer.SerializeToElement(new {item_id=7001L,type_id=recipe.Blueprint,location_id=60003760L,quantity=-2,runs=5,material_efficiency=10,time_efficiency=20}),
                JsonSerializer.SerializeToElement(new {item_id=7002L,type_id=recipe.Blueprint,location_id=60003760L,quantity=-2,runs=5,material_efficiency=0,time_efficiency=20})},
            Assets=new(){new(){ItemId=8000,TypeId=3465,Quantity=1,IsSingleton=true,LocationId=60003760,LocationFlag="Hangar",LocationType="station"}},
            AssetNames=new(){[8000]="Manufacturing materials"},LocationNames=new(){[60003760]="Jita 4-4"} };
        long item=8100;
        foreach(var m in recipe.Materials)pilot.Assets.Add(new(){ItemId=item++,TypeId=m.Key,Quantity=IndustryCraftSuggestions.Material(m.Value,10,10),LocationId=8000,LocationFlag="Unlocked",LocationType="item"});
        var uiStore=new IndustryWorkspaceStore(Path.Combine(folder,"craft-ui"));
        var pricing=new IndustryService(new EveSsoService(),Path.Combine(folder,"craft-ui-pricing"));
        var pricingState=pricing.State.CraftPricing;
        pricingState.Settings=new(){SystemId=51,SystemName="Fixture",FacilityTaxPercent=.25m};
        pricingState.Systems["FIXTURE"]=51;pricingState.ManufacturingIndices[51]=.01m;
        pricingState.AdjustedExpiresUtc=pricingState.IndicesExpiresUtc=now.AddHours(1);
        foreach(var material in recipe.Materials)
        {pricingState.AdjustedPrices[material.Key]=1;pricingState.Quotes["Jita:"+material.Key]=new(8,10,100000000,100000000,now,now.AddHours(1));}
        decimal me0Cost=recipe.Materials.Sum(m=>(decimal)IndustryCraftSuggestions.Material(m.Value,1,0)*10);
        decimal me10Cost=recipe.Materials.Sum(m=>(decimal)IndustryCraftSuggestions.Material(m.Value,1,10)*10);
        decimal jobCost=recipe.Materials.Sum(m=>(decimal)m.Value)*.0525m;
        decimal salePrice=((me0Cost+me10Cost)/2+jobCost)/.895m/(decimal)recipe.Products.Single().Value;
        pricingState.Quotes["Jita:"+recipe.Products.Single().Key]=new(salePrice*.8m,salePrice,10000,10000,now,now.AddHours(1));
        var view=new IndustryWorkspaceView();
        view.Initialize(uiStore,()=>new[]{pilot},new(){new(){CharacterId=42,CharacterName="Builder",Scopes=new[]{"esi-assets.read_assets.v1","esi-characters.read_blueprints.v1"}}},pricing);
        typeof(IndustryWorkspaceView).GetMethod("OpenCraft_Click",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(view,new object[]{view,new RoutedEventArgs()});
        var grid=(DataGrid)view.FindName("CraftGrid");
        Check(grid.Items.Count==2,"Available to craft reads both real BPC instances and material stacks nested in a named can");
        Check(grid.Items.Cast<IndustryCraftSuggestions.Suggestion>().Count(s=>s.Economics.Profit>0)==1 && grid.Items.Cast<IndustryCraftSuggestions.Suggestion>().Count(s=>s.Economics.Profit<0)==1,
            "Craft list distinguishes profitable and loss-making copies using actual blueprint ME and complete costs");
        var profitable=(CheckBox)view.FindName("CraftProfitOnly");profitable.IsChecked=true;profitable.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check(grid.Items.Count==1 && ((IndustryCraftSuggestions.Suggestion)grid.Items[0]).Economics.Profit>0,"Profitable-only filtering excludes known losses");
        profitable.IsChecked=false;profitable.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        grid.SelectAll();
        Check(((Button)view.FindName("CreateCraftButton")).IsEnabled && ((TextBlock)view.FindName("CraftInputs")).Text.Contains("Manufacturing materials"),
            "Multiple selected BPCs produce an actionable bundle preview with named source provenance");
        var window=new Window {Content=view,Width=1380,Height=920,Background=System.Windows.Media.Brushes.Black};
        if(renderPath!=null)Render(window,Path.ChangeExtension(renderPath,".craft-ready.png"));
        if(renderPath!=null)
        {
            window.Width=1280; window.Height=850;
            Render(window,Path.ChangeExtension(renderPath,".craft-compact.png"));
            Check(grid.ActualHeight>=100 && ((FrameworkElement)view.FindName("CreateCraftButton")).ActualHeight>=25,
                "Craft list and project creation remain visible at compact window sizes");
            Check(grid.Columns.Single(c=>c.Header.ToString()=="NET PROFIT").ActualWidth>=145,
                "Compact craft tables retain readable money columns instead of crushing all fields into the viewport");
            window.Width=1380; window.Height=920;
        }
        ((Button)view.FindName("CreateCraftButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(uiStore.Snapshot().Projects.Single().Nodes.Count(n=>n.ParentId==null)==2 && ((TreeView)view.FindName("Nodes")).Items.Count==2,
            "Create craft bundle opens one saved project containing both selectable output trees");
        if(renderPath!=null)Render(window,Path.ChangeExtension(renderPath,".craft-project.png"));
        window.Close();
    }
}
