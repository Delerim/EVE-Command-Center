using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckSellerRefresh(string folder, string? renderPath = null)
    {
        SellerDashboardService? fixture=null;
        Task.Run(async () =>
        {
            var path=Path.Combine(folder,"seller-refresh");
            var handler=new SellerRefreshHandler();
            var sso=new EveSsoService();
            var orders=new MarketOrdersService(sso,path,new HttpClient(handler),(_,_)=>Task.FromResult("fixture-token"));
            var pilots=new[]{new EvePilotProfile { CharacterId=42,CharacterName="Trader A",Scopes=new[]{SellerDashboardService.AssetsScope,SellerDashboardService.OrdersScope} },
                new EvePilotProfile {CharacterId=43,CharacterName="Trader B",Scopes=new[]{SellerDashboardService.AssetsScope,SellerDashboardService.OrdersScope}}};
            var held=new TaskCompletionSource<IReadOnlyList<EveAssetItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool hold=true,failAssets=false;
            var assets=new[]{new EveAssetItem {ItemId=100,TypeId=34,Quantity=9,LocationId=SellerDashboardService.Jita44,LocationType="station",LocationFlag="Hangar"}};
            using var seller=new SellerDashboardService(sso,orders,path,new HttpClient(handler),()=>Task.FromResult<IReadOnlyList<EvePilotProfile>>(pilots),
                async(p,ct)=> { if(failAssets)throw new HttpRequestException("Asset fixture failure"); if(hold)return await held.Task.WaitAsync(ct);return p.CharacterId==42?assets:Array.Empty<EveAssetItem>(); });
            fixture=seller;
            seller.SelectSeller(42);
            var oldRefresh=seller.RefreshAsync();
            seller.SelectSeller(43);
            held.SetResult(assets);
            await oldRefresh;
            Check(seller.SellerCharacterId==43 && seller.Rows.Count==0 && !seller.Busy,
                "Switching sellers during an in-flight asset request cannot publish the previous trader's inventory");
            hold=false;
            bool publishedBeforeQuotes=false;
            seller.Updated+=()=> { if(seller.Busy && seller.Rows.Count>0 && seller.Status.Contains("comparing"))publishedBeforeQuotes=true; };
            await seller.RefreshAsync();
            Check(seller.Rows.Count==2 && seller.Rows.All(r=>r.Stock==0 && r.Listed>0) && publishedBeforeQuotes,
                "Order-only products remain visible and stock/orders are published before public quote work");
            Check(seller.Rows.Single(r=>r.TypeId==34).State=="UNDERCUT" && seller.Rows.Single(r=>r.TypeId==35).OrderHub=="Multiple" && seller.Rows.Single(r=>r.TypeId==35).State=="UNDERCUT",
                "Both single-hub and mixed-hub orders compare each listing with its own station price");
            int calls=handler.OrderCalls,quotes=handler.QuoteCalls;
            await seller.RefreshAsync();
            Check(handler.OrderCalls==calls && handler.QuoteCalls==quotes && seller.Status.Contains("next ESI refresh"),
                "Repeat seller refresh reuses unexpired ESI orders and quotes and reports the next order refresh time");
            failAssets=true;
            await seller.RefreshAsync();
            Check(seller.Status.Contains("REFRESH FAILED") && seller.Rows.Count==2 && !seller.Busy,
                "Failed seller assets retain displayed results and release the refresh gate");
            failAssets=false;
            seller.SelectSeller(42); await seller.RefreshAsync();
            Check(seller.Rows.Single(r=>r.TypeId==34).Stock==9 && seller.Rows.Single(r=>r.TypeId==34).Listed==1,
                "After a seller switch, fresh inventory and cached orders belong to the newly selected character");
            orders.State.Characters.Single(c=>c.CharacterId==42).NextRefresh=default;
            handler.WaitOrder=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var sharedRefresh=orders.RefreshAsync(pilots,CancellationToken.None,new long[]{42});
            var sellerWaiting=seller.RefreshAsync();
            await Task.Delay(20);
            Check(!sellerWaiting.IsCompleted && orders.Busy,"Seller waits for an active shared Market refresh instead of skipping order updates");
            handler.WaitOrder.SetResult(true);await Task.WhenAll(sharedRefresh,sellerWaiting);handler.WaitOrder=null;
            hold=true;
            held=new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel=new CancellationTokenSource();
            var cancelled=seller.RefreshAsync(cancel.Token);cancel.Cancel();await cancelled;
            Check(!seller.Busy && seller.Status.Contains("cancelled") && seller.Rows.Count==2,
                "Seller cancellation retains results and allows later refreshes");
            hold=false;
            await seller.RefreshAsync();
            Check(seller.Status.Contains("positions") && !seller.Busy,"A cancelled seller refresh recovers without reopening the Market window");
            handler.FailGroup=true;
            using var retry=new SellerDashboardService(sso,orders,Path.Combine(path,"retry"),new HttpClient(handler),()=>Task.FromResult<IReadOnlyList<EvePilotProfile>>(pilots),(_,_)=>Task.FromResult<IReadOnlyList<EveAssetItem>>(assets));
            retry.SelectSeller(42);await retry.RefreshAsync();
            Check(retry.Rows.Count==0 && retry.Status.Contains("unresolved"),"Failed category metadata is reported without inventing verified stock");
            handler.FailGroup=false;await retry.RefreshAsync();
            Check(retry.Rows.Count==2,"A later seller refresh retries failed group/category tasks and restores missing products");
        }).GetAwaiter().GetResult();
        if(renderPath!=null)
        {
            var view=new EveCommandCenter.Views.SellerDashboardWindow(fixture);
            var window=new System.Windows.Window {Content=view,Width=1500,Height=900};
            window.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary {Source=new Uri("/EVE Command Center;component/Views/IndustryWorkspaceTheme.xaml",UriKind.Relative)});
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var pilots=(System.Windows.Controls.ComboBox)typeof(EveCommandCenter.Views.SellerDashboardWindow).GetField("_pilots",flags)!.GetValue(view)!;
            pilots.ItemsSource=new[]{"Trader A (fixture)"};pilots.SelectedIndex=0;
            var grid=(System.Windows.Controls.DataGrid)typeof(EveCommandCenter.Views.SellerDashboardWindow).GetField("_grid",flags)!.GetValue(view)!;
            System.Windows.Data.CollectionViewSource.GetDefaultView(grid.ItemsSource).SortDescriptions.Add(new("Stock",System.ComponentModel.ListSortDirection.Ascending));
            typeof(EveCommandCenter.Views.SellerDashboardWindow).GetMethod("Render",flags,null,Type.EmptyTypes,null)!.Invoke(view,null);
            Check(((SellerStockRow)grid.Items[0]).Stock==0,"Seller numeric stock sorting survives a progress/render update");
            var search=(System.Windows.Controls.TextBox)typeof(EveCommandCenter.Views.SellerDashboardWindow).GetField("_search",flags)!.GetValue(view)!;
            search.Text="No matching product";
            typeof(EveCommandCenter.Views.SellerDashboardWindow).GetMethod("Render",flags,null,Type.EmptyTypes,null)!.Invoke(view,null);
            Check(grid.Items.Count==0 && System.Windows.Data.CollectionViewSource.GetDefaultView(grid.ItemsSource).SortDescriptions.Count==1,
                "Refreshing an empty seller filter retains one sort rule without duplicating descriptors");
            search.Clear();
            var undercut=(System.Windows.Controls.TextBlock)typeof(EveCommandCenter.Views.SellerDashboardWindow).GetField("_undercut",flags)!.GetValue(view)!;
            Check(undercut.Text=="3","Seller undercut tile counts actual undercut orders rather than product rows");
            Render(window,Path.ChangeExtension(renderPath,".seller-refresh.png"));
            window.Close();view.Dispose();
        }
    }

    private sealed class SellerRefreshHandler : HttpMessageHandler
    {
        public int OrderCalls,QuoteCalls;
        public bool FailGroup;
        public TaskCompletionSource<bool>? WaitOrder;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string path=request.RequestUri!.AbsolutePath;
            string body;
            if(path.Contains("/characters/"))
            {
                OrderCalls++;
                if(WaitOrder!=null)await WaitOrder.Task.WaitAsync(ct);
                body=JsonSerializer.Serialize(new[]{
                    new MarketOrder{OrderId=1,TypeId=34,LocationId=SellerDashboardService.Jita44,RegionId=SellerDashboardService.Forge,Total=1,Remaining=1,Price=15,Issued=DateTimeOffset.UtcNow,Duration=30},
                    new MarketOrder{OrderId=2,TypeId=35,LocationId=SellerDashboardService.Jita44,RegionId=SellerDashboardService.Forge,Total=2,Remaining=2,Price=15,Issued=DateTimeOffset.UtcNow,Duration=30},
                    new MarketOrder{OrderId=3,TypeId=35,LocationId=SellerDashboardService.AmarrEmperor,RegionId=SellerDashboardService.Domain,Total=3,Remaining=3,Price=30,Issued=DateTimeOffset.UtcNow,Duration=30}});
            }
            else if(path.Contains("/markets/"))
            { QuoteCalls++; body=JsonSerializer.Serialize(new[]{new{location_id=SellerDashboardService.Jita44,price=10m},new{location_id=SellerDashboardService.AmarrEmperor,price=20m}}); }
            else if(path.Contains("/types/"))body="{\"group_id\":1,\"name\":\"Fixture product\"}";
            else if(path.Contains("/groups/"))
            {if(FailGroup)return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);body="{\"category_id\":7,\"name\":\"Module\"}";}
            else body="{\"name\":\"Module\"}";
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)};
            response.Headers.Add("X-Pages","1");response.Content.Headers.Expires=DateTimeOffset.UtcNow.AddMinutes(20);
            return response;
        }
    }
}
