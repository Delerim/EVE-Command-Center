using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckMarketTransactions(string folder, string? renderPath)
    {
        var pilot = new EvePilotProfile { CharacterId = 42, CharacterName = "Market pilot", Scopes = new[] { MarketOrdersService.WalletScope } };
        string Token(string[] scopes) => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = "CHARACTER:EVE:42", name = "Market pilot", scp = scopes }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        Check(!EveSsoService.CachedTokenCoversProfile(Token(Array.Empty<string>()), pilot), "Reconnected profiles reject cached access tokens missing newly granted permissions");
        Check(EveSsoService.CachedTokenCoversProfile(Token(pilot.Scopes), pilot), "Unchanged granted permissions keep valid access-token caching");
        Check(MarketOrdersService.Failure(new HttpRequestException("hidden", null, HttpStatusCode.Forbidden)).Contains("403") && !MarketOrdersService.Failure(new HttpRequestException()).Contains("HttpRequestException"), "Market errors explain denied access and network failures without exposing exception jargon");
        using (var response = new HttpResponseMessage(HttpStatusCode.OK))
        {
            var expiry = DateTimeOffset.UtcNow.AddMinutes(4); response.Content = new StringContent("[]"); response.Content.Headers.Expires = expiry;
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromHours(1) };
            Check((MarketOrdersService.RefreshDue(response, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)) - expiry).Duration() < TimeSpan.FromSeconds(1), "A cached response's absolute expiry is not extended by another hour");
        }
        string path = Path.Combine(folder, "transactions");
        var planning = new IndustryWorkspaceStore(path); var project = planning.Create("Keep during upgrade", 34, 1, null, new HashSet<long> { 42 });
        var ledger = new IndustryLedger(path); ledger.Ensure();
        var handler = new TransactionHandler(); using var http = new HttpClient(handler);
        var service = new MarketOrdersService(new EveSsoService(), path, http, (_, _) => Task.FromResult("test-token"));
        Check(File.Exists(ledger.PathName + ".v1.bak") && new IndustryWorkspaceStore(path).Snapshot().Projects.Single().Id == project, "Ledger schema upgrade retains planning and creates a consistent pre-upgrade database backup");
        service.Select(new long[] { 42 });
        Task.Run(async () =>
        {
            handler.FailLater = true; await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(ledger.Transactions().Count == 0 && ledger.NextTransactions(42) == default, "Later transaction-page failure commits neither partial spending nor a success checkpoint");
            handler.FailLater = false;
            await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(service.Transactions.Count == 2 && service.Transactions.Any(r => r.Transaction.Buy) && service.Transactions.Any(r => !r.Transaction.Buy), "Cursor pagination imports purchases and sales while excluding corporation transactions");
            Check(service.Transactions.Single(r => r.Transaction.Buy).Transaction.Total == 246913578.246913578m, "Confirmed transaction totals retain decimal precision and integer-safe identities");
            int calls = handler.Calls; await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(handler.Calls == calls, "Transaction cache prevents unnecessary repeat API calls");
            var rows = service.Transactions.Select(r => r.Transaction).ToList();
            var earlier = DateTimeOffset.UtcNow.AddHours(-3);
            ledger.ImportTransactions(42, rows, earlier, earlier.AddHours(1));
            await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(ledger.Transactions().Count == 2, "Repeated transaction imports do not double-count purchases or sales");
            ledger.ImportTransactions(42, rows, earlier, earlier.AddHours(1));
            handler.RepeatCursor = true; await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(ledger.Transactions().Count == 2 && ledger.NextTransactions(42) == earlier.AddHours(1), "Non-advancing transaction cursors fail safely without changing imported totals or checkpoint");
            handler.RepeatCursor = false; handler.Inclusive = true;
            await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(ledger.Transactions().Count == 2 && ledger.NextTransactions(42) > DateTimeOffset.UtcNow, "Inclusive transaction cursor boundaries deduplicate exact evidence and terminate without duplicate spending");
            ledger.ImportTransactions(42, rows, earlier, earlier.AddHours(1));
            bool conflict = false; rows[0].UnitPrice += 1;
            try { ledger.ImportTransactions(42, rows, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)); } catch (InvalidDataException) { conflict = true; }
            Check(conflict && ledger.NextTransactions(42) == earlier.AddHours(1), "Conflicting immutable transaction evidence rolls back instead of rewriting historical spending");
            calls = handler.Calls; pilot.Scopes = Array.Empty<string>();
            await service.RefreshTransactionsAsync(new[] { pilot }, CancellationToken.None);
            Check(handler.Calls == calls && service.Transactions.Count == 2 && service.TransactionStatus.Contains("permission"), "Missing wallet permission retains existing transactions without issuing a request");
        }).GetAwaiter().GetResult();
        var restart = new MarketOrdersService(new EveSsoService(), path, http);
        Check(restart.Transactions.Count == 2, "Transaction history and exact values survive restart in the shared ledger");
        restart.Select(Array.Empty<long>()); Check(ledger.Transactions().Count == 2, "Hiding market pilots never deletes their imported financial evidence");
        restart.Select(new long[] { 42 });
        foreach (var record in restart.Transactions) record.Transaction.ResolvedLocation = "Jita IV - Moon 4 - Caldari Navy Assembly Plant";
        var view = new MarketOrdersWindow(); view.Initialize(restart, new[] { pilot });
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 4;
        var grid = (DataGrid)view.FindName("TransactionsGrid");
        Check(grid.Items.Count == 1 && ((TextBlock)view.FindName("Totals")).Text.Contains("not profit"), "Transaction UI separates gross purchases from profit and respects purchase filtering");
        if (renderPath != null) { grid.SelectedIndex = 0; Render(view, Path.ChangeExtension(renderPath, ".transactions.png")); Check(((TextBlock)view.FindName("OrderDetail")).Text.Contains("Confirmed by"), "Selected transaction details remain visible after rendering and source refresh"); }
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 5;
        Check(grid.Items.Count == 1 && ((TextBlock)view.FindName("ObservationSummary")).Text.Contains("sales"), "Sales tab shows confirmed sales independently of purchases");
        view.Close();
    }
    private sealed class TransactionHandler : HttpMessageHandler
    {
        public int Calls; public bool FailLater; public bool RepeatCursor; public bool Inclusive;
        private readonly DateTimeOffset date = DateTimeOffset.UtcNow.AddDays(-2);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; string query = request.RequestUri!.Query;
            if (FailLater && query.Length > 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            MarketTransaction Row(long id, bool buy, bool personal = true) => new() { Id = id, TypeId = buy ? 34 : 35, Quantity = 2, UnitPrice = 123456789.123456789m, LocationId = 60003760, ClientId = 77, JournalRefId = id + 1, Date = date, Buy = buy, Personal = personal };
            var rows = query.Length == 0 || RepeatCursor ? new[] { Row(900000000002, true), Row(900000000001, false, false) } : query == "?from_id=900000000001" ? new[] { Row(900000000000, false) } : Array.Empty<MarketTransaction>();
            if (Inclusive && query == "?from_id=900000000001") rows = new[] { Row(900000000001, false, false), Row(900000000000, false) };
            if (Inclusive && query == "?from_id=900000000000") rows = new[] { Row(900000000000, false) };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(rows)) };
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromHours(1) }; return Task.FromResult(response);
        }
    }
}
