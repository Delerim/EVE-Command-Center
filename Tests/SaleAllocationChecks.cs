using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
using Microsoft.Data.Sqlite;
internal static partial class Program
{
    private static void CheckSaleAllocations(string root, string? renderPath)
    {
        string folder = Path.Combine(root, "sale-allocations");
        var linked = new HashSet<long> { 42, 43 }; var store = new IndustryWorkspaceStore(folder);
        var project = store.Create("Obelisk production", 20187, 4, null, linked);
        var second = store.Create("Obelisk second batch", 20187, 2, null, linked);
        var other = store.Create("Other build", 34, 2, null, linked);
        store.AddComponent(other, store.Snapshot().Projects.Single(p => p.Id == other).Nodes[0].Id, 20187, 1);
        var ledger = new IndustryLedger(folder); ledger.Ensure(); var date = DateTimeOffset.UtcNow;
        MarketTransaction Trade(long id, bool buy) => new() { Id = id, TypeId = 20187, Quantity = 4, UnitPrice = 1234567890.123m, LocationId = 60003760, ResolvedLocation = "Jita IV - Moon 4 - Caldari Navy Assembly Plant", ClientId = 7, JournalRefId = 9, Date = date, Buy = buy, Personal = true };
        ledger.ImportTransactions(42, new() { Trade(1, false), Trade(2, true) }, date, date.AddHours(1));
        ledger.ImportTransactions(43, new() { Trade(1, false) }, date, date.AddHours(1));
        using var http = new HttpClient(new MarketHandler()); var service = new MarketOrdersService(new EveSsoService(), folder, http);
        PurchaseAllocation Split(Guid? id, long qty) => new(Guid.NewGuid(), id == null ? "Stock" : "Project", id, "ignored", qty, "Produced for this build");
        var a = Split(project, 2); var b = Split(second, 1); var stock = Split(null, 1);
        bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        Check(Reject(() => service.SaveSaleAllocation(42, 1, 0, new[] { a }, false)), "Project sale attribution requires explicit confirmation of product origin");
        var saved = service.SaveSaleAllocation(42, 1, 0, new[] { a, b, stock }, true);
        Check(saved.Revision == 1 && saved.Splits.Sum(s => s.Quantity) == 4 && saved.Splits.Any(s => s.ProjectName == "Obelisk production"), "A sale splits between matching projects and general stock using authoritative names");
        Check(store.ProjectSales(project).Single().GrossProceeds == 2469135780.246m && store.ProjectPurchases(project).Count == 0, "Gross project receipts preserve exact decimal prices and never enter purchase cost totals");
        Check(new IndustryWorkspaceStore(folder).Snapshot().Revision == store.Snapshot().Revision && store.Snapshot().Reservations.Count == 0, "A sale link does not invent production completion, transfers or material consumption");
        Check(Reject(() => service.SaveSaleAllocation(42, 2, 0, new[] { a }, true)) && Reject(() => service.SavePurchaseAllocation(42, 1, 0, new[] { a })), "Purchase and sale allocations reject source trades of the wrong side");
        Check(Reject(() => service.SaveSaleAllocation(42, 1, 1, new[] { a with { Quantity = 5 } }, true)), "Sale allocations cannot exceed actual units sold");
        Check(Reject(() => service.SaveSaleAllocation(42, 1, 1, new[] { Split(other, 1) }, true)), "A component requirement alone cannot suggest a project as the source of a sold final product");
        Check(Reject(() => service.SaveSaleAllocation(42, 1, 0, new[] { a }, true)), "Stale sale editors cannot overwrite saved allocations");
        Check(service.SaveSaleAllocation(42, 1, 1, saved.Splits, false).Revision == 1 && service.SaleHistory(42, 1).Count == 1, "Unchanged sale allocations are idempotent and retain their confirmation");
        service.SaveSaleAllocation(43, 1, 0, new[] { Split(second, 1) }, true);
        Check(service.SaleAllocation(42, 1).Splits.Sum(s => s.Quantity) == 4 && service.SaleAllocation(43, 1).Splits.Sum(s => s.Quantity) == 1, "Matching transaction identifiers on different sellers remain independent");
        using (var c = new SqliteConnection($"Data Source={ledger.PathName};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER sale_failure BEFORE INSERT ON sale_allocation_audit BEGIN SELECT RAISE(ABORT, 'test'); END;"; cmd.ExecuteNonQuery(); }
        bool failed = false; try { service.SaveSaleAllocation(42, 1, 1, new[] { a }, true); } catch (SqliteException) { failed = true; }
        Check(failed && service.SaleAllocation(42, 1).Revision == 1 && service.SaleHistory(42, 1).Count == 1, "Failed sale audit writes roll back quantities and revision together");
        using (var c = new SqliteConnection($"Data Source={ledger.PathName};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TRIGGER sale_failure"; cmd.ExecuteNonQuery(); }
        string previous = Path.Combine(root, "sale-upgrade"); Directory.CreateDirectory(previous);
        File.Copy(ledger.PathName, Path.Combine(previous, "industry-ledger.db")); File.Copy(Path.Combine(folder, "industry-workspace.json"), Path.Combine(previous, "industry-workspace.json"));
        using (var c = new SqliteConnection($"Data Source={Path.Combine(previous, "industry-ledger.db")};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE sale_allocation_audit; DROP TABLE sale_allocations; PRAGMA user_version=4"; cmd.ExecuteNonQuery(); }
        var upgraded = new MarketOrdersService(new EveSsoService(), previous, http);
        Check(upgraded.Transactions.Count == 3 && new IndustryWorkspaceStore(previous).Snapshot().Projects.Count == 3 && File.Exists(Path.Combine(previous, "industry-ledger.db.v4.bak")), "Sales migration preserves populated prior-version projects and transactions with a backup");
        using (var c = new SqliteConnection($"Data Source={Path.Combine(previous, "industry-ledger.db.v4.bak")};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; Check(Convert.ToInt32(cmd.ExecuteScalar()) == 4, "Sale migration backup retains the original schema"); }
        var reopened = new MarketOrdersService(new EveSsoService(), folder, http); reopened.Select(new[] { 42L });
        Check(reopened.SaleAllocations.Count == 2 && reopened.SaleHistory(42, 1).Count == 1, "Sales links and audit history survive restart");
        var pilots = new List<EvePilotProfile> { new() { CharacterId = 42, CharacterName = "Market seller", Scopes = new[] { MarketOrdersService.WalletScope } } };
        var view = new MarketOrdersWindow { Width = 1800, Height = 1400 }; view.Initialize(reopened, pilots);
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 5; ((DataGrid)view.FindName("TransactionsGrid")).SelectedIndex = 0;
        var splits = (DataGrid)view.FindName("AllocationSplits"); splits.SelectedItem = splits.Items.Cast<PurchaseAllocation>().Single(s => s.ProjectId == project);
        Check(((TextBlock)view.FindName("AllocationTitle")).Text.Contains("sale") && ((CheckBox)view.FindName("ConfirmSaleProject")).Visibility == Visibility.Visible && !((Button)view.FindName("SaveAllocation")).IsEnabled, "Sales use the shared polished editor with a visible origin confirmation");
        ((CheckBox)view.FindName("ConfirmSaleProject")).IsChecked = true;
        Check(((Button)view.FindName("SaveAllocation")).IsEnabled, "Confirming product origin enables the valid sale assignment");
        if (renderPath != null) Render(view, Path.ChangeExtension(renderPath, ".sale-allocation.png")); view.Close();
        var workspace = new IndustryWorkspaceView(); workspace.Initialize(store, () => Array.Empty<IndustryPilot>(), pilots);
        ((ListBox)workspace.FindName("Projects")).SelectedIndex = -1; ((ListBox)workspace.FindName("Projects")).SelectedIndex = 0;
        ((TabControl)workspace.FindName("WorkspaceTabs")).SelectedItem = workspace.FindName("ProjectSalesTab");
        Check(((DataGrid)workspace.FindName("ProjectSales")).Items.Count == 1 && !((TextBlock)workspace.FindName("ProjectSalesNote")).Text.Contains("profit is"), "Project sales expose attributed receipts and provenance without claiming profit");
        if (renderPath != null)
        { var shell = new Window { Content = workspace, Width = 1750, Height = 1000 }; workspace.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => ((TabControl)workspace.FindName("WorkspaceTabs")).SelectedItem = workspace.FindName("ProjectSalesTab"))); Render(shell, Path.ChangeExtension(renderPath, ".project-sales.png")); shell.Close(); }
        store.DeleteProject(project);
        Check(reopened.SaleAllocation(42, 1).Splits.Any(s => s.ProjectId == project) && reopened.Transactions.Count == 3, "Deleting a project preserves its past sale links and original transactions");
        Check(Reject(() => reopened.SaveSaleAllocation(42, 1, 1, new[] { a with { Quantity = 3 } }, true)), "Removed projects cannot receive new sold quantities");
        reopened.SaveSaleAllocation(42, 1, 1, saved.Splits.Where(s => s.ProjectId != project).ToArray(), false);
        Check(store.ProjectSales(project).Count == 0 && reopened.SaleHistory(42, 1).Count == 2, "Removing sale attribution reverses receipts while preserving history");
    }
}
