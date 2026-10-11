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
    private static void CheckPilotSync(string root, string? renderPath)
    {
        string folder = Path.Combine(root, "sync-settings");
        var pilots = new[] { new EvePilotProfile { CharacterId = 42, CharacterName = "Builder", Scopes = new[] { MarketOrdersService.WalletScope } }, new EvePilotProfile { CharacterId = 43, CharacterName = "Seller", Scopes = new[] { MarketOrdersService.WalletScope, MarketOrdersService.Scope } } };
        var settings = new PilotSyncSettings(folder); settings.EnsurePilots(pilots, new HashSet<long> { 43 });
        Check(settings.Enabled(42, "wallet") && settings.Enabled(43, "wallet") && !settings.Enabled(42, "market") && settings.Enabled(43, "market"), "Sync defaults enable wallets independently of migrated market selection");
        var seller = settings.Get(43); seller.ShowWallet = false; settings.Save(new[] { seller });
        Check(new PilotSyncSettings(folder).Enabled(43, "wallet") && !new PilotSyncSettings(folder).Get(43).ShowWallet, "Dashboard visibility persists independently without disabling wallet imports");
        var clone = settings.Get(42); clone.Wallet = false;
        Check(settings.Enabled(42, "wallet"), "Unsaved sync edits do not alter the running policy");
        using var handler = new SyncHandler(); using var http = new HttpClient(handler);
        MarketOrdersService service = null!;
        service = new MarketOrdersService(new EveSsoService(), folder, http, (_, _) => { service.Select(new[] { 42L }); return Task.FromResult("test"); });
        service.Select(new[] { 43L });
        Task.Run(() => FinancialSync.RunAsync(service, settings, pilots, CancellationToken.None)).GetAwaiter().GetResult();
        Check(handler.Paths.Count(p => p.Contains("/42/wallet")) == 3 && handler.Paths.Count(p => p.Contains("/43/wallet")) == 3, "Scheduled wallet sync covers balance, journal and trades for every enabled pilot");
        Check(!handler.Paths.Any(p => p.Contains("/42/orders")) && handler.Paths.Count(p => p.Contains("/43/orders")) == 2, "Market orders and history are requested only for the independently enabled market pilot");
        Check(service.State.SelectedCharacters.SetEquals(new[] { 42L }) && service.State.Balances.Count == 2, "Background targets and cache commits preserve a viewed-pilot change during refresh");
        Check(service.JournalReports[42].Contains("Builder") && service.JournalReports[43].Contains("Seller"), "Background wallet reports retain independent pilot identity");
        var marketView = new MarketOrdersWindow(); marketView.Initialize(service, pilots); marketView.SelectPilot(42);
        ((TabControl)marketView.FindName("MarketTabs")).SelectedIndex = 7;
        Check(((TextBlock)marketView.FindName("HistoryStatus")).Text.Contains("Builder") && !((TextBlock)marketView.FindName("HistoryStatus")).Text.Contains("Seller"), "Visible wallet status belongs to the viewed pilot rather than the last background import"); marketView.Close();
        int calls = handler.Paths.Count;
        Task.Run(() => FinancialSync.RunAsync(service, settings, pilots, CancellationToken.None)).GetAwaiter().GetResult();
        Check(handler.Paths.Count == calls, "Scheduled refreshes share endpoint cache expiry and do not duplicate wallet requests");
        clone.Wallet = false; clone.Industry = false; clone.Planetary = false; clone.Pilot = false; clone.Clones = false; settings.Save(new[] { clone });
        Check(!settings.Enabled(42, "industry") && !settings.Enabled(42, "pi") && !settings.Enabled(42, "pilot") && !settings.Enabled(42, "clones"), "Each nonfinancial automatic-sync function has an independent saved policy");
        service.State.Balances[42].NextRefresh = default;
        Task.Run(() => FinancialSync.RunAsync(service, settings, pilots, CancellationToken.None)).GetAwaiter().GetResult();
        Check(handler.Paths.Count == calls && service.State.Balances[42].Amount == 100, "Disabling sync stops scheduled requests while preserving saved balance");
        var summary = FinancialDashboard.Build(service, settings, pilots, DateTimeOffset.UtcNow);
        Check(summary.Wallet.Contains("100.00") && summary.OrderCoverage.Contains("1/1") && !summary.Orders.Contains("not loaded"), "Dashboard totals honor their visibility filters and distinguish saved empty orders from missing data");
        var noHistory = new MarketOrdersService(new EveSsoService(), Path.Combine(root, "empty-financial-dashboard"), http);
        var empty = FinancialDashboard.Build(noHistory, settings, pilots, DateTimeOffset.UtcNow);
        Check(empty.Orders.Contains("not loaded") && empty.Wallet.Contains("Not loaded") && empty.Trades.Contains("no imported"), "Unloaded dashboard feeds are not presented as confirmed zero activity");
        var ledger = new IndustryLedger(folder); ledger.ImportJournal(42, new() { new() { Id = 1, Date = DateTimeOffset.UtcNow, RefType = "market_transaction", Description = "Paid", Amount = -25 } }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        ledger.ImportTransactions(42, new() { new() { Id = 1, Date = DateTimeOffset.UtcNow, TypeId = 34, Quantity = 5, UnitPrice = 5, Buy = true, Personal = true, LocationId = 60003760, ClientId = 7, JournalRefId = 1 } }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var reopened = new MarketOrdersService(new EveSsoService(), folder, http);
        var cash = FinancialDashboard.Build(reopened, settings, pilots, DateTimeOffset.UtcNow);
        Check(cash.Wallet.Contains("-25.00") && !cash.Wallet.Contains("-50.00") && cash.Trades.Contains("25.00"), "Dashboard cash flow counts wallet entries once while showing trade spending separately");
        reopened.Journal.Add(new(42, DateTimeOffset.UtcNow, new EveWalletJournalEntry { Id = 99, Date = DateTimeOffset.UtcNow, RefType = "unknown", Description = "Unknown amount", Amount = null }));
        Check(FinancialDashboard.Build(reopened, settings, pilots, DateTimeOffset.UtcNow).Wallet.Contains("Incomplete (missing amounts)"), "Unknown journal amounts make dashboard cash flow incomplete rather than a false zero or partial total");
        string bad = Path.Combine(root, "bad-sync"); Directory.CreateDirectory(bad); File.WriteAllText(Path.Combine(bad, "pilot-sync.json"), "broken");
        var corrupt = new PilotSyncSettings(bad); corrupt.EnsurePilots(pilots, new HashSet<long>());
        Check(corrupt.Error.Length > 0 && !corrupt.Enabled(42, "wallet") && File.ReadAllText(Path.Combine(bad, "pilot-sync.json")) == "broken", "Corrupt sync settings fail closed and preserve the original file");
        var view = new PilotSyncView(); view.Initialize(settings, pilots, service);
        Check(((DataGrid)view.FindName("SyncRows")).Items.Count == 2 && ((TextBlock)view.FindName("SyncDetail")).Text.Contains("Automatic sync off"), "Setup exposes editable per-pilot functions and an explicit disabled status");
        if (renderPath != null)
        {
            var shell = new Window { Content = view, Width = 1250, Height = 780, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(7,24,27)), Foreground = System.Windows.Media.Brushes.White };
            shell.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/EVE Command Center;component/Views/ContractsTheme.xaml", UriKind.Relative) });
            Render(shell, Path.ChangeExtension(renderPath, ".sync-settings.png")); shell.Close();
            var dashboard = new CommandCenterWindow(live:false) { Width = 1850, Height = 1100, WindowState = WindowState.Normal };
            ((TextBlock)dashboard.FindName("DashboardProjects")).Text = "2 projects | 0 paused\n3 linked EVE jobs";
            ((TextBlock)dashboard.FindName("DashboardProjectNames")).Text = "Obelisk / Pacifier";
            ((TextBlock)dashboard.FindName("DashboardOrders")).Text = cash.Orders;
            ((TextBlock)dashboard.FindName("DashboardOrderCoverage")).Text = cash.OrderCoverage;
            ((TextBlock)dashboard.FindName("DashboardWallet")).Text = cash.Wallet;
            ((TextBlock)dashboard.FindName("DashboardWalletCoverage")).Text = cash.WalletCoverage;
            ((TextBlock)dashboard.FindName("DashboardTrades")).Text = cash.Trades;
            dashboard.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => ((FrameworkElement)dashboard.FindName("FinancialCards")).BringIntoView()));
            Render(dashboard, Path.ChangeExtension(renderPath, ".financial-dashboard.png")); dashboard.Close();
        }
    }
    private sealed class SyncHandler : HttpMessageHandler
    {
        public List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("/wallet") ? "100" : "[]") };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            response.Headers.Add("X-Pages", "1"); return Task.FromResult(response);
        }
    }
}
