using System.IO;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Views;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckProductionProgress(string? renderPath = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ecc-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new IndustryWorkspaceStore(folder);
            var linked = new HashSet<long> { 41, 42 };
            var id = store.Create("Delivery fixture", 34, 10, null, linked);
            var node = store.Snapshot().Projects.Single().Nodes.Single();
            var now = DateTimeOffset.UtcNow;
            var source = new IndustryStockSource(41, "Builder", 100, 34, 60003760, "Hangar", "Origin", 10, now, true, "Observed");
            var destination = new IndustryStockSource(42, "Hauler", 200, 34, 60003761, "Hangar", "Destination", 6, now, true, "Observed");
            store.Reserve(id, node.Id, source, 10, now, new[] { source });
            var reservation = store.Snapshot().Reservations.Single().Id;
            bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
            Check(Reject(() => store.ConfirmDelivery(reservation, source, 11, "Checked", now, new[] { source })), "Delivery cannot exceed reservation");
            Check(Reject(() => store.ConfirmDelivery(reservation, source, 1, "", now, new[] { source })), "Delivery requires explicit handoff evidence");
            var stale = source with { SnapshotUtc = now.AddHours(-2) };
            Check(Reject(() => store.ConfirmDelivery(reservation, stale, 1, "Checked", now, new[] { stale })), "Stale inventory cannot confirm a receipt");
            store.ConfirmDelivery(reservation, source, 3, "Checked at origin", now, new[] { source });
            var progress = IndustryProductionProgress.Build(node, store.Snapshot(), Array.Empty<IndustryJobLink>(), now);
            Check(progress.Delivered == 3 && progress.Reserved == 10 && progress.StillToDeliver == 7 && progress.ReceiptStatus == "Partially delivered", "Partial delivery is independent of reserved quantity");
            Check(Reject(() => store.Release(reservation)), "Delivered stock cannot accidentally be released");
            store.UndoDelivery(reservation);
            Check(store.Snapshot().Reservations.Single().Quantity == 10 && store.Snapshot().Reservations.Single().DeliveredQuantity == 0, "Undo delivery retains its stock reservation");
            store.ConfirmDelivery(reservation, destination, 6, "Received by Hauler", now, new[] { source with { Quantity = 4 }, destination });
            var state = store.Snapshot();
            Check(state.Reservations.Sum(r => r.Quantity) == 10 && state.Reservations.Single(r => r.OwnerId == 42).DeliveredQuantity == 6 && state.Reservations.Single(r => r.OwnerId == 41).Quantity == 4,
                "Partial handoff moves reservation atomically without counting stock at both ends");
            var reopened = new IndustryWorkspaceStore(folder);
            Check(reopened.Error == "" && reopened.Snapshot().SchemaVersion == 2 && reopened.Snapshot().Reservations.Sum(r => r.DeliveredQuantity) == 6, "Delivery evidence survives reopening with versioned workspace");
            var another = store.Create("Other build", 34, 6, null, linked);
            var anotherNode = store.Snapshot().Projects.Single(p => p.Id == another).Nodes.Single();
            Check(Reject(() => store.Reserve(another, anotherNode.Id, destination, 1, now, new[] { source with { Quantity = 4 }, destination })), "Confirmed destination stock cannot be allocated twice");
            store.ConfirmDelivery(reservation, source with { Quantity = 4 }, 4, "Remaining stock checked", now, new[] { source with { Quantity = 4 }, destination });
            state = store.Snapshot();
            Check(IndustryProductionProgress.Stage(state.Projects.Single(p => p.Id == id), state, Array.Empty<IndustryJobLink>(), now) == "Delivered", "Project becomes delivered only when root receipts cover its required quantity");
            new IndustryLedger(folder).Ensure();
            var ledgerStore = new IndustryWorkspaceStore(folder);
            Check(ledgerStore.Error == "" && ledgerStore.Snapshot().Reservations.Sum(r => r.DeliveredQuantity) == 10,
                "SQLite migration preserves delivery evidence and versioned workspace");
            ledgerStore.UndoDelivery(reservation);
            Check(new IndustryWorkspaceStore(folder).Snapshot().Reservations.Sum(r => r.DeliveredQuantity) == 6,
                "Delivery correction persists in the authoritative SQLite ledger");
            if (renderPath != null)
            {
                var oldPath = WorkspaceLayout.PreferencePath;
                WorkspaceLayout.PreferencePath = Path.Combine(folder, "layout.json");
                try
                {
                    var view = new IndustryWorkspaceView();
                    view.Initialize(ledgerStore, () => Array.Empty<IndustryPilot>(), new());
                    ((FrameworkElement)view.FindName("ProjectGallery")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)view.FindName("ProjectWorkspace")).Visibility = Visibility.Visible;
                    var tabs = (TabControl)view.FindName("WorkspaceTabs");
                    tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string?)t.Header == "Production & delivery");
                    var grid = (DataGrid)view.FindName("ProductionProgress");
                    Check(grid.Items.Count == 1 && grid.CanUserSortColumns, "Production table exposes per-component sortable progress");
                    var window = new Window { Content = view, Width = 1900, Height = 850, Background = System.Windows.Media.Brushes.Black };
                    Render(window, renderPath);
                    tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string?)t.Header == "Production & delivery");
                    Render(window, renderPath);
                    tabs.SelectedItem = view.FindName("ProjectDetailTab");
                    var componentTabs = (TabControl)view.FindName("ComponentTabs");
                    componentTabs.SelectedItem = componentTabs.Items.Cast<TabItem>().Single(t => (string?)t.Header == "Stock & transfers");
                    FrameworkElement element = (FrameworkElement)view.FindName("DeliveryQuantity");
                    while (element.Parent is FrameworkElement parent) { element = parent; if (element is Expander expander) { expander.IsExpanded = true; break; } }
                    Render(window, Path.ChangeExtension(renderPath, "delivery.png"));

                }
                finally { WorkspaceLayout.PreferencePath = oldPath; }
            }
            var recipe = IndustryCatalog.Recipes.First(r => r.Activity == "manufacturing" && r.Products.Any(p => p.Value > 1));
            int product = recipe.Products.First(p => p.Value > 1).Key;
            var jobNode = new IndustryProjectNode { TypeId = product, Quantity = 500, BlueprintTypeId = recipe.Blueprint };
            var job = new IndustryJobLink(41, 1000, 41, product, recipe.Blueprint, 900, 60003760, 2, "active", now.AddDays(-1), now.AddMinutes(-1), now);
            jobNode.ActualJob = job;
            var row = IndustryProductionProgress.Build(jobNode, state, new[] { job }, now);
            Check(row.InProduction == (decimal)recipe.Products[product] * 2 && row.AwaitingCollection == 0 && row.Delivered == 0,
                "Partial batch output uses product units per run and elapsed timer never confirms collection");
            row = IndustryProductionProgress.Build(jobNode, state, new[] { job with { Status = "ready" } }, now);
            Check(row.InProduction == 0 && row.AwaitingCollection > 0 && row.Delivered == 0, "Ready EVE job is awaiting collection, not project delivery");
            row = IndustryProductionProgress.Build(jobNode, state, new[] { job with { Status = "delivered" } }, now);
            Check(row.Delivered == 0 && row.JobStatus.Contains("confirm project receipt"), "Collected EVE output still requires project receipt confirmation");
            row = IndustryProductionProgress.Build(jobNode, state, new[] { job with { SnapshotUtc = now.AddHours(-2) } }, now);
            Check(row.InProduction == null && row.AwaitingCollection == null, "Stale linked jobs produce unknown quantities rather than invented zeroes");
            row = IndustryProductionProgress.Build(jobNode, state, new[] { job with { BlueprintItemId = 901 } }, now);
            Check(row.InProduction == null, "Changed job identity is not silently associated with the project");
            Check(IndustryProductionProgress.Build(jobNode, state, new[] { job with { Status = "cancelled" } }, now).InProduction == 0, "Cancelled jobs do not promise production output");
        }
        finally { Directory.Delete(folder, true); }
    }
}
