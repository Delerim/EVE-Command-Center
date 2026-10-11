using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
internal static partial class Program
{
    private static void CheckUnifiedWallet(string root, string? renderPath)
    {
        string folder = Path.Combine(root, "unified-wallet");
        var handler = new BalanceHandler(); using var http = new HttpClient(handler);
        var service = new MarketOrdersService(new EveSsoService(), folder, http, (_, _) => Task.FromResult("test-token"));
        var pilots = new[] { new EvePilotProfile { CharacterId = 42, CharacterName = "Builder", Scopes = new[] { MarketOrdersService.WalletScope } }, new EvePilotProfile { CharacterId = 43, CharacterName = "Seller", Scopes = new[] { MarketOrdersService.WalletScope } } };
        service.Select(new[] { 42L });
        Task.Run(async () =>
        {
            await service.RefreshBalanceAsync(pilots, CancellationToken.None);
            Check(service.State.Balances[42].Amount == 123456789.12345678m && handler.Calls == 1, "Unified wallet balance retains exact decimal value for the selected pilot");
            await service.RefreshBalanceAsync(pilots, CancellationToken.None);
            Check(handler.Calls == 1, "Repeated balance refreshes reuse the response cache expiry");
            service.Select(new[] { 43L }); await service.RefreshBalanceAsync(pilots, CancellationToken.None);
            Check(service.State.Balances[43].Amount == 50m && service.State.Balances[42].Amount == 123456789.12345678m, "Balances remain separate when changing pilots");
            service.Select(new[] { 42L }); service.State.Balances[42].NextRefresh = DateTimeOffset.MinValue; handler.Fail = true;
            await service.RefreshBalanceAsync(pilots, CancellationToken.None);
            Check(service.State.Balances[42].Amount == 123456789.12345678m && service.State.Balances[42].Error.Length > 0, "Failed balance refresh retains the last good value with an explicit error");
            handler.Fail = false; handler.Malformed = true; await service.RefreshBalanceAsync(pilots, CancellationToken.None);
            Check(service.State.Balances[42].Amount == 123456789.12345678m, "Malformed balance responses never replace real money with zero");
            int calls = handler.Calls;
            await service.RefreshBalanceAsync(new[] { new EvePilotProfile { CharacterId = 42, CharacterName = "Builder" } }, CancellationToken.None);
            Check(calls == handler.Calls && service.State.Balances[42].Error.Contains("permission"), "Missing wallet scope sends no balance request and preserves cached evidence");
            handler.Malformed = false; await service.RefreshBalanceAsync(pilots, CancellationToken.None);
        }).GetAwaiter().GetResult();
        var ledger = new IndustryLedger(folder); ledger.Ensure(); var now = DateTimeOffset.UtcNow;
        ledger.ImportJournal(42, new() { new() { Id = 1, Date = now, RefType = "market_transaction", Description = "Trade", Amount = 100m }, new() { Id = 2, Date = now, RefType = "brokers_fee", Description = "Fee", Amount = -20m }, new() { Id = 3, Date = now, RefType = "unknown", Description = "No amount" } }, now, now.AddHours(1));
        ledger.ImportJournal(43, new() { new() { Id = 1, Date = now, RefType = "market_transaction", Description = "Other pilot", Amount = 900m } }, now, now.AddHours(1));
        MarketTransaction Plex(long id, long quantity, decimal price, bool buy) => new() { Id = id, TypeId = 44992, Quantity = quantity, UnitPrice = price, Buy = buy, Personal = true, Date = now, LocationId = 60003760, ClientId = 7, JournalRefId = 1 };
        ledger.ImportTransactions(42, new() { Plex(1, 10, 2, true), Plex(2, 30, 4, true), Plex(3, 5, 6, false) }, now, now.AddHours(1));
        var restarted = new MarketOrdersService(new EveSsoService(), folder, http);
        Check(restarted.State.Balances[42].Amount == 123456789.12345678m, "Unified balance snapshots survive restart without another query");
        var view = new MarketOrdersWindow { Width = 1700, Height = 1050 }; view.Initialize(restarted, pilots);
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 7;
        Check(((TextBlock)view.FindName("TodayIn")).Text == "100.00 ISK" && ((TextBlock)view.FindName("TodayOut")).Text == "20.00 ISK" && ((TextBlock)view.FindName("TodayNet")).Text == "80.00 ISK", "Wallet overview counts only the selected pilot's journal once, excluding trade totals and null amounts");
        int beforeViewChanges = handler.Calls; view.SelectPilot(43);
        Check(((TextBlock)view.FindName("TodayIn")).Text == "900.00 ISK" && ((TextBlock)view.FindName("WalletBalanceText")).Text == "50.00 ISK", "Pilot cards switch balance and cash flow together with no combined view");
        view.SelectPilot(42);
        if (renderPath != null) Render(view, Path.ChangeExtension(renderPath, ".wallet-overview.png"));
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 8;
        Check(((DataGrid)view.FindName("TransactionsGrid")).Items.Count == 3 && ((TextBlock)view.FindName("ObservationSummary")).Text.Contains("3.50") && ((TextBlock)view.FindName("ObservationSummary")).Text.Contains("6.00"), "PLEX view reuses imported trades and quantity-weighted buy and sell prices");
        Check(handler.Calls == beforeViewChanges, "Switching wallet and PLEX views causes no duplicate data requests");
        if (renderPath != null) Render(view, Path.ChangeExtension(renderPath, ".plex.png")); view.Close();

    }
    private sealed class BalanceHandler : HttpMessageHandler
    {
        public int Calls; public bool Fail, Malformed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (!request.RequestUri!.AbsolutePath.EndsWith("/wallet")) throw new InvalidOperationException("Unexpected duplicate wallet query");
            var response = new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(Malformed ? "null" : request.RequestUri.AbsolutePath.Contains("/42/") ? "123456789.12345678" : "50") };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(2) };
            return Task.FromResult(response);
        }
    }
}
