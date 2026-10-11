using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckMarketJournal(string folder, string? renderPath)
    {
        var path = Path.Combine(folder, "journal");
        var planning = new IndustryWorkspaceStore(path); var project = planning.Create("Keep this project", 34, 1, null, new HashSet<long> { 42 });
        var ledger = new IndustryLedger(path); ledger.Ensure();
        var handler = new JournalHandler(); using var http = new HttpClient(handler);
        var service = new MarketOrdersService(new EveSsoService(), path, http, (_, _) => Task.FromResult("test-token"));
        var pilots = new[] { new EvePilotProfile { CharacterId = 42, CharacterName = "Industrial pilot", Scopes = new[] { MarketOrdersService.WalletScope, MarketOrdersService.Scope } }, new EvePilotProfile { CharacterId = 43, CharacterName = "Market pilot", Scopes = new[] { MarketOrdersService.WalletScope } } };
        service.Select(new long[] { 42 });
        Task.Run(async () =>
        {
            handler.FailSecond = true; await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(service.Journal.Count == 0 && ledger.NextJournal(42) == default, "Wallet page failure preserves records and does not commit a partial checkpoint");
            handler.FailSecond = false; await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(service.Journal.Count == 4 && service.Journal.Single(j => j.Entry.Id == 91).Entry.Amount == -123456.123456789m, "Wallet pagination retains signed decimal precision and optional amounts");
            Check(File.Exists(ledger.PathName + ".v2.bak") && new IndustryWorkspaceStore(path).Snapshot().Projects.Single().Id == project, "Journal schema upgrade creates a backup and preserves project identity");
            int calls = handler.Calls; await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(calls == handler.Calls, "Wallet activity respects its independent refresh checkpoint");
            var past = DateTimeOffset.UtcNow.AddHours(-3); var rows = service.Journal.Select(j => j.Entry).ToList();
            ledger.ImportJournal(42, rows, past, past.AddHours(1)); await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(ledger.Journal().Count == 4, "Repeated wallet imports never duplicate money movement");
            rows.Single(r => r.Id == 91).Amount = -123456.1234567890m;
            ledger.ImportJournal(42, rows, past, past.AddHours(1));
            Check(ledger.Journal().Count == 4, "Equivalent decimal formatting is not treated as conflicting financial evidence");
            ledger.ImportJournal(42, rows, past, past.AddHours(1)); handler.Conflict = true;
            await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(ledger.NextJournal(42) == past.AddHours(1) && ledger.Journal().Single(j => j.Entry.Id == 91).Entry.Amount == -123456.123456789m, "Conflicting wallet evidence rolls back the entire import and checkpoint");
            handler.Conflict = false; handler.MissingPages = true; await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(ledger.NextJournal(42) == past.AddHours(1), "Missing wallet pagination metadata cannot commit a partial import");
            handler.MissingPages = false; handler.Duplicate = true; await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(ledger.NextJournal(42) == past.AddHours(1), "Duplicate wallet entries across pages are rejected before accounting");
            handler.Duplicate = false; handler.Empty = true; await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(service.Journal.Count == 4, "An empty wallet response retains imported evidence outside the current API window");
            calls = handler.Calls; pilots[0].Scopes = Array.Empty<string>(); await service.RefreshJournalAsync(pilots, CancellationToken.None);
            Check(calls == handler.Calls && service.JournalStatus.Contains("permission") && service.Journal.Count == 4, "Missing wallet permission preserves evidence without issuing a request");
        }).GetAwaiter().GetResult();
        var trade = new MarketTransaction { Id = 101, JournalRefId = 91, TypeId = 34, Quantity = 1, UnitPrice = 123456.123456789m, LocationId = 60003760, ResolvedLocation = "Jita IV - Moon 4 - Caldari Navy Assembly Plant", ClientId = 77, Date = handler.Date, Personal = true, Buy = true };
        ledger.ImportTransactions(42, new() { trade }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        ledger.ImportJournal(43, new() { new() { Id = 91, Date = handler.Date, Description = "Transfer", RefType = "player_donation", Amount = 50m } }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        // Reconstruct the previous release's schema from a populated, closed fixture database.
        string oldPath = Path.Combine(folder, "journal-upgrade"); Directory.CreateDirectory(oldPath);
        string oldDatabase = Path.Combine(oldPath, "industry-ledger.db"); File.Copy(ledger.PathName, oldDatabase);
        File.Copy(Path.Combine(path, "industry-workspace.json"), Path.Combine(oldPath, "industry-workspace.json"));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldDatabase};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE wallet_journal; DROP TABLE journal_checkpoint; PRAGMA user_version=2;"; command.ExecuteNonQuery();
        }
        var upgraded = new MarketOrdersService(new EveSsoService(), oldPath, http);
        Check(upgraded.Transactions.Count == 1 && new IndustryWorkspaceStore(oldPath).Snapshot().Projects.Single().Id == project && File.Exists(oldDatabase + ".v2.bak"), "A populated previous-release ledger upgrades without losing transactions or projects");
        using (var backup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldDatabase}.v2.bak;Pooling=False"))
        {
            backup.Open(); using var command = backup.CreateCommand(); command.CommandText = "PRAGMA user_version";
            Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "Journal migration backup remains a usable pre-upgrade database");
        }
        var restarted = new MarketOrdersService(new EveSsoService(), path, http); restarted.Select(new long[] { 42, 43 });
        Check(restarted.Journal.Count == 5, "Wallet evidence survives restart with identical entry identifiers isolated by pilot");
        var view = new MarketOrdersWindow(); view.Initialize(restarted, pilots);
        Check(((TextBlock)view.FindName("PilotSummary")).Text == "Industrial pilot" && restarted.State.SelectedCharacters.SetEquals(new long[] { 42 }), "Market opens one remembered pilot instead of combining selected characters");
        var tabs = (TabControl)view.FindName("MarketTabs"); tabs.SelectedIndex = 6;
        var grid = (DataGrid)view.FindName("JournalGrid"); var totals = (TextBlock)view.FindName("Totals");
        Check(grid.Items.Count == 4 && totals.Text.Contains((123456.123456789m + 12.5m).ToString("N2")) && totals.Text.Contains("200.00"), "Wallet cash flow counts journal amounts once, never adding transaction totals or optional tax again");
        view.SelectPilot(43);
        Check(grid.Items.Count == 1 && restarted.State.SelectedCharacters.SetEquals(new long[] { 43 }), "Pilot cards switch the entire wallet view without combining balances or activity");
        grid.SelectedIndex = 0;
        Check(!((TextBlock)view.FindName("OrderDetail")).Text.Contains("Linked by"), "Journal references never link a transaction belonging to another pilot");
        view.SelectPilot(42);
        grid.SelectedIndex = 0;
        Check(((TextBlock)view.FindName("OrderDetail")).Text.Contains("Tritanium") && ((TextBlock)view.FindName("OrderDetail")).Text.Contains("counted once"), "Wallet detail links a named purchase through the exact pilot and journal reference");
        ((ComboBox)view.FindName("Period")).SelectedIndex = 1;
        Check(grid.Items.Count == 0 && ((Border)view.FindName("EmptyState")).Visibility == Visibility.Visible, "Wallet date filtering presents an explicit empty state");
        ((ComboBox)view.FindName("Period")).SelectedIndex = 0;
        if (renderPath != null) { grid.SelectedIndex = 0; Render(view, Path.ChangeExtension(renderPath, ".wallet.png")); }
        tabs.SelectedIndex = 4;
        Check(((DataGrid)view.FindName("TransactionsGrid")).Items.Count == 1, "Single-pilot selection applies consistently to purchases as well as wallet activity");
        tabs.SelectedIndex = 5;
        Check(((DataGrid)view.FindName("TransactionsGrid")).Items.Count == 0, "Sales and purchases remain independent tabs");
        view.SelectPilot(43);
        Check(restarted.Journal.Count == 5, "Switching pilot cards never deletes another pilot's imported evidence");
        Check(new MarketOrdersService(new EveSsoService(), path, http).State.SelectedCharacters.SetEquals(new long[] { 43 }), "The single selected pilot persists across restart");
        view.Close();
    }
    private sealed class JournalHandler : HttpMessageHandler
    {
        public int Calls; public bool FailSecond, Conflict, MissingPages, Duplicate, Empty;
        public DateTimeOffset Date = DateTimeOffset.UtcNow.AddDays(-2);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; bool second = request.RequestUri!.Query == "?page=2";
            if (second && FailSecond) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var rows = Empty ? Array.Empty<EveWalletJournalEntry>() : second ? new[]
            {
                new EveWalletJournalEntry { Id = Duplicate ? 91 : 93, Date = Date.AddMinutes(-2), RefType = "market_transaction", Description = "Sale", Amount = 200m },
                new EveWalletJournalEntry { Id = 94, Date = Date.AddMinutes(-3), RefType = "unknown_future_activity", Description = "Unspecified" }
            } : new[]
            {
                new EveWalletJournalEntry { Id = 91, Date = Date, RefType = "market_transaction", Description = "Purchase", Amount = Conflict ? -1m : -123456.123456789m },
                new EveWalletJournalEntry { Id = 92, Date = Date.AddMinutes(-1), RefType = "brokers_fee", Description = "Broker fee", Amount = -12.5m, Tax = 2m }
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(rows)) };
            if (!MissingPages) response.Headers.TryAddWithoutValidation("X-Pages", Empty ? "1" : "2");
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromHours(1) };
            return Task.FromResult(response);
        }
    }
}
