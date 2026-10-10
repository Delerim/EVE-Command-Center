using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckMarketOrders(string folder, string? renderPath)
    {
        var path = Path.Combine(folder, "market");
        var handler = new MarketHandler();
        var http = new HttpClient(handler);
        var service = new MarketOrdersService(new EveSsoService(), path, http, (_, _) => Task.FromResult("test-token"));
        var pilot = new EvePilotProfile { CharacterId = 42, CharacterName = "Market pilot", Scopes = new[] { MarketOrdersService.Scope } };
        Task.Run(async () =>
        {
            Check(service.State.SelectedCharacters.Count == 0, "Market selection starts empty with no hardcoded toons");
            service.Select(new long[] { 42 });
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            var first = service.State.Characters.Single();
            Check(first.Orders.Count == 3 && first.Orders.Single(o => o.Buy && !o.Corporation).RemainingValue == 7.5m && first.Error == "",
                "Market orders preserve decimal prices and separate remaining value from spending");
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(handler.Calls == 1, "Market refresh respects the server cache interval");
            first.NextRefresh = default;
            handler.Fail = true;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(service.State.Characters.Single().Orders.Count == 3 && service.State.Characters.Single().Error.Contains("failed"),
                "Failed market refresh retains the last successful order snapshot");
            pilot.Scopes = Array.Empty<string>();
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(handler.Calls == 2 && service.State.Characters.Single().Error.Contains("permission") && service.State.Characters.Single().Orders.Count == 3,
                "Missing market permission performs no request and does not convert cached orders into zero");
            pilot.Scopes = new[] { MarketOrdersService.Scope }; handler.Fail = false;
            service.State.Characters.Single().NextRefresh = default; handler.Malformed = true;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(service.State.Characters.Single().Orders.Count == 3 && service.State.Characters.Single().Error.Contains("failed"),
                "Malformed order responses cannot overwrite usable cached observations");
            handler.Malformed = false; service.State.Characters.Single().NextRefresh = default;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            handler.OmitBuy = true; service.State.Characters.Single().NextRefresh = default;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            var disappeared = service.State.Characters.Single().Unobserved.Single();
            Check(disappeared.Order.OrderId == 123 && disappeared.Order.Remaining == 6 && disappeared.LastSeen != default, "Disappearance retains last quantities without inventing fills or cancellations");
            service.State.Characters.Single().NextRefresh = default;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(service.State.Characters.Single().Unobserved.Count == 1 && service.State.Characters.Single().Unobserved[0].MissingSince == disappeared.MissingSince, "Repeated absence does not duplicate or rewrite first-missing evidence");
            var restart = new MarketOrdersService(new EveSsoService(), path, http);
            Check(restart.State.Characters.Single().Unobserved.Count == 1, "Unobserved order survives restart");
            handler.Fail = true; service.State.Characters.Single().NextRefresh = default;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(service.State.Characters.Single().Unobserved.Count == 1 && service.State.Characters.Single().Orders.Count == 2, "Failed refresh cannot move open orders into missing observations");
            handler.Fail = false; handler.OmitBuy = false; service.State.Characters.Single().NextRefresh = default;
            await service.RefreshAsync(new[] { pilot }, CancellationToken.None);
            Check(service.State.Characters.Single().Unobserved.Count == 0 && service.State.Characters.Single().Orders.Count == 3, "Reappearing order returns to open observations without duplication");
        }).GetAwaiter().GetResult();
        service.Select(Array.Empty<long>());
        Check(service.State.Characters.Single().Orders.Count == 3, "Hiding a market character retains cached observations");
        service.Select(new long[] { 42 });
        var restored = new MarketOrdersService(new EveSsoService(), path, http);
        Check(restored.State.SelectedCharacters.SetEquals(new long[] { 42 }) && restored.State.Characters.Single().Orders.Count == 3,
            "Market character selection and last successful orders survive restart");
        var view = new MarketOrdersWindow(); view.Initialize(restored, new[] { pilot });
        Check(((DataGrid)view.FindName("Orders")).Items.Count == 1, "Embedded market buy tab displays only personal buy orders");
        ((TabControl)view.FindName("SideTabs")).SelectedIndex = 1;
        Check(((DataGrid)view.FindName("Orders")).Items.Count == 1, "Embedded market sell tab switches to personal sell orders");
        if (renderPath != null)
        {
            restored.State.Characters.Single().Unobserved.Add(new() { Order = new MarketOrder { OrderId = 900, TypeId = 35, LocationId = 60003760, ResolvedLocation = "Jita IV - Moon 4", Price = 5.5m, Total = 200, Remaining = 80, Duration = 30, Issued = DateTimeOffset.UtcNow.AddDays(-2) }, LastSeen = DateTimeOffset.UtcNow.AddHours(-2), MissingSince = DateTimeOffset.UtcNow.AddHours(-1) });
            ((System.Windows.Controls.ComboBox)view.FindName("Observation")).SelectedIndex = 1;
            Check(((DataGrid)view.FindName("Orders")).Items.Count == 1, "Market observation filter shows absent orders separately from open orders");
            ((System.Windows.Controls.ComboBox)view.FindName("Observation")).SelectedIndex = 2;
            ((DataGrid)view.FindName("Orders")).SelectedIndex = 1;
            Render(view, Path.ChangeExtension(renderPath, ".market.png"));
        }
        view.Close();
        var cache = Path.Combine(path, "market-orders.json");
        File.WriteAllText(cache, "corrupt-cache");
        var broken = new MarketOrdersService(new EveSsoService(), path, http);
        bool blocked = false; try { broken.Select(new long[] { 42 }); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && File.ReadAllText(cache) == "corrupt-cache", "Unreadable market cache is preserved rather than silently overwritten");
    }

    private sealed class MarketHandler : HttpMessageHandler
    {
        public int Calls; public bool Fail; public bool Malformed; public bool OmitBuy;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var rows = new[]
            {
                new MarketOrder { OrderId = 123, TypeId = 34, LocationId = 60003760, RegionId = 10000002, Price = 1.25m, Total = 10, Remaining = 6, Issued = DateTimeOffset.UtcNow, Duration = 90, Buy = true },
                new MarketOrder { OrderId = 125, TypeId = 34, LocationId = 60003760, RegionId = 10000002, Price = 5, Total = 20, Remaining = 12, Issued = DateTimeOffset.UtcNow, Duration = 30, Buy = true, Corporation = true },
                new MarketOrder { OrderId = 124, TypeId = 35, LocationId = 60003760, RegionId = 10000002, Price = 5.75m, Total = 20, Remaining = 12, Issued = DateTimeOffset.UtcNow, Duration = 30 }
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Malformed ? "[{\"order_id\":123}]" : JsonSerializer.Serialize(rows.Where(r => !OmitBuy || r.OrderId != 123))) };
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromMinutes(20) };
            return Task.FromResult(response);
        }
    }
}
