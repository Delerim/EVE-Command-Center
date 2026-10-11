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
    private static void CheckPurchaseAllocations(string root, string? renderPath)
    {
        string folder = Path.Combine(root, "allocations");
        var store = new IndustryWorkspaceStore(folder); var linked = new HashSet<long> { 42, 43 };
        var a = store.Create("Obelisk materials", 34, 100, null, linked);
        var b = store.Create("Pacifier materials", 34, 100, null, linked);
        var other = store.Create("Different material", 35, 100, null, linked);
        var ledger = new IndustryLedger(folder); ledger.Ensure();
        var purchase = new MarketTransaction { Id = 1, TypeId = 34, Quantity = 100, UnitPrice = 1.23456789m, LocationId = 60003760, ResolvedLocation = "Jita IV - Moon 4 - Caldari Navy Assembly Plant", ClientId = 77, JournalRefId = 101, Date = DateTimeOffset.UtcNow, Buy = true, Personal = true };
        ledger.ImportTransactions(42, new() { purchase, new() { Id = 2, TypeId = 34, Quantity = 10, UnitPrice = 2m, LocationId = 60003760, ClientId = 77, JournalRefId = 102, Date = purchase.Date, Buy = false, Personal = true } }, purchase.Date, purchase.Date.AddHours(1));
        ledger.ImportTransactions(43, new() { purchase }, purchase.Date, purchase.Date.AddHours(1));
        using var http = new HttpClient(new MarketHandler()); var service = new MarketOrdersService(new EveSsoService(), folder, http);
        bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        PurchaseAllocation Split(string purpose, Guid? project, long q) => new(Guid.NewGuid(), purpose, project, "Untrusted display name", q, "Build materials");
        var splits = new[] { Split("Project", a, 60), Split("Project", b, 20), Split("Stock", null, 10) };
        var saved = service.SavePurchaseAllocation(42, 1, 0, splits);
        Check(saved.Revision == 1 && saved.Splits.Sum(s => s.Quantity) == 90 && saved.Splits.Single(s => s.ProjectId == a).ProjectName == "Obelisk materials", "Purchase quantities split across projects and stock using authoritative project names");
        Check(ledger.ProjectPurchases(a).Single().Cost == 74.07407340m && ledger.ProjectPurchases(b).Single().Cost == 24.69135780m, "Attributed acquisition cost uses each split quantity and the exact confirmed unit price");
        Check(new IndustryWorkspaceStore(folder).Snapshot().Reservations.Count == 0 && new IndustryWorkspaceStore(folder).Snapshot().Revision == store.Snapshot().Revision, "Financial attribution never creates physical stock or changes project readiness");
        Check(service.SavePurchaseAllocation(42, 1, 1, saved.Splits).Revision == 1 && service.PurchaseHistory(42, 1).Count == 1, "Repeated identical allocation saves create neither duplicate expense nor audit events");
        Check(Reject(() => service.SavePurchaseAllocation(42, 1, 1, saved.Splits.Append(Split("Stock", null, 11)).ToArray())) && service.PurchaseAllocation(42, 1).Revision == 1, "Purchase allocation cannot exceed confirmed purchased quantity");
        Check(Reject(() => service.SavePurchaseAllocation(42, 1, 1, new[] { Split("Stock", null, long.MaxValue) })), "Oversized allocation quantities fail without arithmetic overflow or persistence");
        Check(Reject(() => service.SavePurchaseAllocation(42, 1, 1, new[] { Split("Project", other, 1) })) && Reject(() => service.SavePurchaseAllocation(42, 1, 1, new[] { Split("Project", Guid.NewGuid(), 1) })), "Project attribution rejects unrelated material types and nonexistent projects");
        Check(Reject(() => service.SavePurchaseAllocation(42, 2, 0, new[] { Split("Stock", null, 1) })) && Reject(() => service.SavePurchaseAllocation(44, 1, 0, new[] { Split("Stock", null, 1) })), "Sales and another pilot's transaction cannot be used as purchase evidence");
        Check(Reject(() => service.SavePurchaseAllocation(42, 1, 1, new[] { splits[0], splits[0] })) && Reject(() => service.SavePurchaseAllocation(42, 1, 1, new[] { Split("Stock", null, 0) })), "Duplicate splits and zero quantities are rejected atomically");
        saved = service.SavePurchaseAllocation(42, 1, 1, saved.Splits.Select(s => s.ProjectId == a ? s with { Quantity = 40 } : s.ProjectId == b ? s with { Quantity = 40 } : s).ToArray());
        Check(saved.Revision == 2 && ledger.ProjectPurchases(a).Single().Cost == ledger.ProjectPurchases(b).Single().Cost && service.PurchaseHistory(42, 1).First().Before.Sum(s => s.Quantity) == 90, "Reallocation moves cost between projects while preserving previous intent");
        var concurrent = new MarketOrdersService(new EveSsoService(), folder, http);
        Check(Reject(() => concurrent.SavePurchaseAllocation(42, 1, 1, Array.Empty<PurchaseAllocation>())) && ledger.ReadAllocation(42, 1).Revision == 2, "Stale allocation editors cannot overwrite another saved split");
        concurrent.SavePurchaseAllocation(43, 1, 0, new[] { Split("Unrelated", null, 100) });
        Check(service.PurchaseAllocation(42, 1).Splits.Sum(s => s.Quantity) == 90 && ledger.ReadAllocation(43, 1).Splits.Single().Purpose == "Unrelated", "Identical transaction identifiers on different pilots retain independent allocation limits");
        using (var c = new SqliteConnection($"Data Source={ledger.PathName};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER fail_allocation_audit BEFORE INSERT ON allocation_audit BEGIN SELECT RAISE(ABORT,'test failure'); END;"; cmd.ExecuteNonQuery();
        }
        bool failed = false; try { service.SavePurchaseAllocation(42, 1, 2, Array.Empty<PurchaseAllocation>()); } catch (SqliteException) { failed = true; }
        Check(failed && ledger.ReadAllocation(42, 1).Revision == 2 && ledger.AllocationHistory(42, 1).Count == 2, "A failed audit write rolls back both allocation changes and revision");
        using (var c = new SqliteConnection($"Data Source={ledger.PathName};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TRIGGER fail_allocation_audit"; cmd.ExecuteNonQuery(); }
        new IndustryWorkspaceStore(folder).DeleteProject(a);
        Check(ledger.Transactions().Count == 3 && ledger.ReadAllocation(42, 1).Splits.Any(s => s.ProjectId == a), "Deleting a project preserves the source purchase and its historical assignment");
        Check(Reject(() => service.SavePurchaseAllocation(42, 1, 2, saved.Splits.Select(s => s.ProjectId == a ? s with { Quantity = 41 } : s).ToArray())), "Removed projects cannot receive new purchase quantities");
        saved = service.SavePurchaseAllocation(42, 1, 2, saved.Splits.Where(s => s.ProjectId != a).ToArray());
        Check(saved.Revision == 3 && ledger.AllocationHistory(42, 1).Any(h => h.Before.Any(s => s.ProjectId == a)), "Removing a historical split releases attribution and retains its explanation");
        saved = service.SavePurchaseAllocation(42, 1, 3, Array.Empty<PurchaseAllocation>());
        Check(ledger.ProjectPurchases(b).Count == 0 && ledger.Transactions().Count == 3, "Clearing allocations reverses attributed cost without deleting source transactions");
        service.SavePurchaseAllocation(42, 1, saved.Revision, new[] { Split("Project", b, 30) });
        string previousFolder = Path.Combine(root, "allocation-upgrade"); Directory.CreateDirectory(previousFolder);
        string previousDatabase = Path.Combine(previousFolder, "industry-ledger.db"); File.Copy(ledger.PathName, previousDatabase);
        File.Copy(Path.Combine(folder, "industry-workspace.json"), Path.Combine(previousFolder, "industry-workspace.json"));
        using (var c = new SqliteConnection($"Data Source={previousDatabase};Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE allocation_audit; DROP TABLE purchase_allocations; PRAGMA user_version=3;"; cmd.ExecuteNonQuery(); }
        var upgraded = new MarketOrdersService(new EveSsoService(), previousFolder, http);
        Check(upgraded.Transactions.Count == 3 && new IndustryWorkspaceStore(previousFolder).Snapshot().Projects.Count == 2 && File.Exists(previousDatabase + ".v3.bak"), "Allocation migration preserves a populated previous-version ledger and creates its backup");
        using (var c = new SqliteConnection($"Data Source={previousDatabase}.v3.bak;Pooling=False"))
        { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; Check(Convert.ToInt32(cmd.ExecuteScalar()) == 3, "Allocation migration backup retains the prior schema"); }
        service.Select(new long[] { 42 });
        var reopened = new MarketOrdersService(new EveSsoService(), folder, http);
        Check(reopened.PurchaseAllocations.Single(s => s.OwnerId == 42).Splits.Single().Quantity == 30 && reopened.PurchaseHistory(42, 1).Count == 5, "Allocation revisions and audit history survive restart");
        var pilots = new List<EvePilotProfile> { new() { CharacterId = 42, CharacterName = "Industrial buyer", Scopes = new[] { MarketOrdersService.WalletScope } } };
        var view = new MarketOrdersWindow { Width = 1800, Height = 1080 }; view.Initialize(reopened, pilots);
        ((TabControl)view.FindName("MarketTabs")).SelectedIndex = 4;
        var trades = (DataGrid)view.FindName("TransactionsGrid"); trades.SelectedIndex = 0;
        Check(((Border)view.FindName("AllocationPanel")).Visibility == Visibility.Visible && ((TextBlock)view.FindName("AllocationSummary")).Text.Contains("70"), "Selecting a purchase opens its named allocation inspector and remaining quantity");
        var target = (ComboBox)view.FindName("AllocationTarget");
        Check(target.SelectedIndex == -1 && target.Items.Cast<object>().Any(p => p.ToString() == "Pacifier materials"), "Matching project suggestions require an explicit destination choice");
        target.SelectedIndex = 0; ((TextBox)view.FindName("AllocationQuantity")).Text = "20";
        ((Button)view.FindName("SaveAllocation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(reopened.PurchaseAllocation(42, 1).Splits.Sum(s => s.Quantity) == 50 && ((TextBlock)view.FindName("AllocationAudit")).Text.Contains("General stock"), "Saving a purchase split refreshes remaining quantity and human-readable history");
        if (renderPath != null) Render(view, Path.ChangeExtension(renderPath, ".allocations.png"));
        ((TextBox)view.FindName("Search")).Text = "Pacifier";
        Check(trades.Items.Count == 1, "Purchase search can find the project explaining why an item was bought");
        view.Close();
        var layout = new MarketOrdersWindow(); layout.AllocationPanelSize = 510;
        ((DataGrid)layout.FindName("TransactionsGrid")).Columns[0].Width = new DataGridLength(220);
        ((Grid)layout.FindName("MarketBody")).RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); layout.Close();
        var layoutRestored = new MarketOrdersWindow();
        Check(layoutRestored.AllocationPanelSize == 510 && ((DataGrid)layoutRestored.FindName("TransactionsGrid")).Columns[0].Width.Value == 220, "Market table widths and allocation-pane size survive reopening"); layoutRestored.Close();
        var workspace = new IndustryWorkspaceView(); workspace.Initialize(new IndustryWorkspaceStore(folder), () => Array.Empty<IndustryPilot>(), pilots);
        ((ListBox)workspace.FindName("Projects")).SelectedIndex = -1;
        ((ListBox)workspace.FindName("Projects")).SelectedIndex = 0;
        ((TabControl)workspace.FindName("WorkspaceTabs")).SelectedItem = workspace.FindName("ProjectPurchasesTab");
        Check(((DataGrid)workspace.FindName("ProjectPurchases")).Items.Count == 1 && ((TextBlock)workspace.FindName("ProjectPurchaseSummary")).Text.Contains((30 * purchase.UnitPrice).ToString("N2")), "Project purchases show only their attributed quantities and exact cost");
        if (renderPath != null) { var shell = new Window { Content = workspace, Width = 1750, Height = 1000 }; workspace.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => ((TabControl)workspace.FindName("WorkspaceTabs")).SelectedItem = workspace.FindName("ProjectPurchasesTab"))); Render(shell, Path.ChangeExtension(renderPath, ".project-purchases.png")); shell.Close(); }
    }
}
