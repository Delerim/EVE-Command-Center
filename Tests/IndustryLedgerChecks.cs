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
    private static void CheckIndustryLedger(string root, string? renderPath)
    {
        string folder = Path.Combine(root, "ledger"); var linked = new HashSet<long> { 42 };
        var store = new IndustryWorkspaceStore(folder);
        var project = store.Create("Retained project", 34, 5, null, linked);
        var original = store.Snapshot(); string legacy = File.ReadAllText(Path.Combine(folder, "industry-workspace.json"));
        var ledger = new IndustryLedger(folder); ledger.Ensure(); ledger.Ensure();
        Check(ledger.ReadWorkspace().Projects.Single().Id == project && ledger.ReadWorkspace().Events.Single().Id == original.Events.Single().Id,
            "Transactional migration retains stable project, component and audit identities");
        Check(File.ReadAllText(Path.Combine(folder, "industry-workspace.json.pre-ledger.bak")) == legacy, "Migration preserves the exact planning JSON backup");
        store.Create("After migration", 35, 2, null, linked);
        Check(new IndustryWorkspaceStore(folder).Snapshot().Projects.Count == 2 && File.ReadAllText(Path.Combine(folder, "industry-workspace.json")) == legacy,
            "Already-open planning store switches to ledger writes without modifying preserved JSON");
        bool conflict = false;
        var stale = new IndustryWorkspaceStore(folder); store.Create("Third", 36, 1, null, linked);
        try { stale.Create("Stale", 37, 1, null, linked); } catch (InvalidOperationException) { conflict = true; }
        Check(conflict && new IndustryWorkspaceStore(folder).Snapshot().Projects.Count == 3, "Ledger revisions reject stale writers without lost updates");
        var handler = new HistoryHandler(); var http = new HttpClient(handler);
        var service = new MarketOrdersService(new EveSsoService(), folder, http, (_, _) => Task.FromResult("test-token"));
        var pilot = new EvePilotProfile { CharacterId = 42, CharacterName = "History pilot", Scopes = new[] { MarketOrdersService.Scope } };
        service.Select(new long[] { 42 });
        service.State.Characters.Add(new() { CharacterId = 42, Name = pilot.CharacterName, Orders = new() { new MarketOrder { OrderId = 9999, RegionId = 10000002, Price = 1, Total = 1, Remaining = 1, Duration = 30, Issued = DateTimeOffset.UtcNow, TypeId = 34, ResolvedLocation = "Jita IV - Moon 4 - Caldari Navy Assembly Plant", LocationId = 60003760 } } });
        Task.Run(async () =>
        {
            handler.FailSecond = true;
            await service.RefreshHistoryAsync(new[] { pilot }, CancellationToken.None);
            Check(ledger.History().Count == 0 && ledger.NextHistory(42) == default, "Failure on a later history page commits neither rows nor checkpoint");
            handler.FailSecond = false;
            await service.RefreshHistoryAsync(new[] { pilot }, CancellationToken.None);
            Check(service.History.Count == 2 && service.History.Select(h => h.Order.State).Distinct().Count() == 2,
                "All history pages commit together including valid zero-day immediate orders");
            Check(service.History[0].Order.Price == 123456789.123456789m && ledger.NextHistory(42) > DateTimeOffset.UtcNow,
                "History preserves decimal prices exactly and persists the API cache checkpoint");
            int calls = handler.Calls; await service.RefreshHistoryAsync(new[] { pilot }, CancellationToken.None);
            Check(handler.Calls == calls, "History refresh honors its own hourly cache independently of open orders");
            var repeated = service.History.Select(h => h.Order).ToList();
            ledger.ImportHistory(42, repeated, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
            Check(ledger.History().Count == 2, "Repeated imports are idempotent by character and order identity");
            var before = DateTimeOffset.UtcNow.AddHours(-2);
            ledger.ImportHistory(42, repeated, before, before.AddHours(1));
            handler.UnknownState = true;
            await service.RefreshHistoryAsync(new[] { pilot }, CancellationToken.None);
            Check(ledger.History().Count == 2 && ledger.NextHistory(42) == before.AddHours(1), "Unsupported terminal states preserve both prior rows and the retry checkpoint");
            handler.UnknownState = false;
            var corporate = new HistoricalMarketOrder { OrderId = 999, TypeId = 34, LocationId = 60003760, RegionId = 10000002, Price = 1, Total = 1, Remaining = 0, Duration = 30, Issued = before, State = "expired", Corporation = true };
            ledger.ImportHistory(42, new() { corporate }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
            Check(ledger.History().Count == 2, "Corporate history is excluded and an empty personal response never deletes retained history");
            calls = handler.Calls;
            pilot.Scopes = Array.Empty<string>(); await service.RefreshHistoryAsync(new[] { pilot }, CancellationToken.None);
            Check(handler.Calls == calls && service.History.Count == 2 && service.HistoryStatus.Contains("permission"), "Missing history scope makes no request and retains imported history");
        }).GetAwaiter().GetResult();
        var restart = new MarketOrdersService(new EveSsoService(), folder, http);
        Check(restart.History.Count == 2 && new IndustryWorkspaceStore(folder).Snapshot().Projects.Count == 3, "Planning and confirmed order history survive restart in the same ledger");
        var view = new MarketOrdersWindow(); view.Initialize(restart, new[] { pilot });
        ((ComboBox)view.FindName("Observation")).SelectedIndex = 3;
        var grid = (DataGrid)view.FindName("Orders");
        Check(grid.Items.Count == 2, "Confirmed history has an independent named, sortable UI filter");
        if (renderPath != null) { grid.SelectedIndex = 0; Render(view, Path.ChangeExtension(renderPath, ".history.png")); }
        view.Close();
        File.WriteAllText(Path.Combine(folder, "industry-workspace.json"), legacy + " ");
        Check(new IndustryWorkspaceStore(folder).Error.Length > 0, "A legacy application changing the migrated JSON is detected rather than silently discarded");
        string corrupt = Path.Combine(root, "migration-corrupt"); Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "industry-workspace.json"), "broken");
        bool rejected = false; try { new IndustryLedger(corrupt).Ensure(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(corrupt, "industry-ledger.db")), "Invalid legacy planning data cannot migrate into an empty ledger");
    }
    private sealed class HistoryHandler : HttpMessageHandler
    {
        public int Calls; public bool FailSecond; public bool UnknownState;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; bool second = request.RequestUri!.Query == "?page=2";
            if (second && FailSecond) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var order = new HistoricalMarketOrder { OrderId = second ? 912 : 911, TypeId = second ? 35 : 34, LocationId = 60003760, RegionId = 10000002,
                Price = 123456789.123456789m, Total = 100, Remaining = 30, Issued = DateTimeOffset.UtcNow.AddDays(-35), Duration = 0, Buy = true,
                State = UnknownState ? "filled" : second ? "expired" : "cancelled", ResolvedLocation = "Jita IV - Moon 4 - Caldari Navy Assembly Plant" };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { order })) };
            response.Headers.TryAddWithoutValidation("X-Pages", "2"); response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromHours(1) };
            return Task.FromResult(response);
        }
    }
}
