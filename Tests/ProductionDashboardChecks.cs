using System.IO;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckProductionDashboard(string? renderPath = null)
    {
        var now = DateTimeOffset.UtcNow;
        var first = new IndustryProject { Name = "Obelisk", Nodes = new() { new() { TypeId = 34, Name = "Tritanium", Strategy = "Buy", Quantity = 70 } } };
        var second = new IndustryProject { Name = "Pacifier", Nodes = new() { new() { TypeId = 34, Name = "Tritanium", Strategy = "Buy", Quantity = 60 } } };
        var workspace = new IndustryWorkspace { Projects = new() { first, second } };
        var linked = new List<EvePilotProfile> { new() { CharacterId = 42, CharacterName = "Synthetic pilot", Scopes = new[] { "esi-assets.read_assets.v1" } } };
        var inventory = new List<IndustryPilot> { new() { Id = 42, Name = "Synthetic pilot", Updated = now, Error = "", Assets = new() {
            new() { ItemId = 1, TypeId = 34, Quantity = 100, LocationId = 60003760, LocationType = "station", LocationFlag = "Hangar" } } } };
        var summary = ProductionDashboard.Build(workspace, inventory, linked, now);
        Check(summary.Shortfalls == 1 && summary.Materials.Single().Missing == 30 && summary.Materials.Single().Required == 130,
            "Dashboard compares shared material demand once instead of covering two projects with the same stock");
        Check(summary.Materials.Single().Projects.Contains("Obelisk") && summary.Materials.Single().Projects.Contains("Pacifier"), "Dashboard preserves consolidated project ownership");
        second.Paused = true;
        workspace.Reservations.Add(new() { ProjectId = second.Id, NodeId = second.Nodes[0].Id, OwnerId = 42, ItemId = 1, TypeId = 34, LocationId = 60003760, LocationFlag = "Hangar", Path = "Location 60003760 / Hangar", Quantity = 60, SnapshotUtc = now });
        summary = ProductionDashboard.Build(workspace, inventory, linked, now);
        Check(summary.ActiveProjects == 1 && summary.Materials.Single().Available == 40 && summary.Materials.Single().Missing == 30,
            "Paused projects stop demand but continue holding reservations against available stock");
        inventory[0].Assets[0].LocationId = 60000001;
        Check(ProductionDashboard.Build(workspace, inventory, linked, now).NeedsReview == 1, "Moved reserved stock requires review instead of a false shortage or coverage claim");
        workspace.Reservations.Clear(); second.Paused = false;
        linked.Add(new() { CharacterId = 43, CharacterName = "Unloaded pilot", Scopes = new[] { "esi-assets.read_assets.v1" } });
        summary = ProductionDashboard.Build(workspace, inventory, linked, now);
        Check(summary.NeedsReview == 1 && summary.Shortfalls == 0 && summary.Materials.Single().Missing == null,
            "Partial inventories cannot prove a deficit even when another pilot has some known stock");
        Check(summary.Materials.Single().Stock.SortMissing == null && summary.Materials.Single().Stock.MissingLabel == "Unknown",
            "Dashboard and consolidated materials use the same conservative shortage calculation");
        inventory[0].Assets[0].Quantity = 200;
        Check(ProductionDashboard.Build(workspace, inventory, linked, now).Covered == 1, "Known stock can prove coverage despite another unloaded inventory without claiming job readiness");
        linked.RemoveAt(1); inventory[0].Updated = now.AddHours(-2);
        Check(ProductionDashboard.Build(workspace, inventory, linked, now).NeedsReview == 1, "Stale inventory is not treated as current stock");
        inventory[0].Updated = now; inventory[0].Assets.Clear();
        summary = ProductionDashboard.Build(workspace, inventory, linked, now);
        Check(summary.Shortfalls == 1 && summary.Materials.Single().Missing == 130, "Complete successful empty inventory proves a real material shortage");
        linked[0].Scopes = Array.Empty<string>();
        Check(ProductionDashboard.Build(workspace, inventory, linked, now).NeedsReview == 1, "Missing asset permission does not become zero stock");
        first.Archived = second.Archived = true;
        Check(ProductionDashboard.Build(workspace, inventory, linked, now).ActiveProjects == 0 && ProductionDashboard.Build(workspace, inventory, linked, now).Materials.Count == 0,
            "Archived projects produce an explicit empty dashboard");
        string folder = Path.Combine(Path.GetTempPath(), "ecc-production-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); string previous = WorkspaceLayout.PreferencePath;
        try
        {
            WorkspaceLayout.PreferencePath = Path.Combine(folder, "layout.json");
            first.Archived = second.Archived = false; linked[0].Scopes = new[] { "esi-assets.read_assets.v1" };
            inventory[0].Assets.Add(new() { ItemId = 1, TypeId = 34, Quantity = 100, LocationId = 60003760, LocationType = "station", LocationFlag = "Hangar" });
            first.Nodes.Add(new() { TypeId = 35, Name = "Pyerite", Strategy = "Buy", Quantity = 9000 });
            var view = new ProductionSummaryView(); view.ShowSummary(ProductionDashboard.Build(workspace, inventory, linked, now));
            var grid = (DataGrid)view.FindName("DashboardMaterialGaps");
            Check(grid.Items.Count == 2 && grid.Columns[1].HeaderStyle != null && ((DataGridTextColumn)grid.Columns[1]).ElementStyle != null,
                "Dashboard shows named material rows and aligned numeric headings/cells");
            bool opened = false; view.OpenMaterials += (_, _) => opened = true;
            // The route is an event; fixture does not open live services.
            typeof(ProductionSummaryView).GetMethod("Open_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { view, new RoutedEventArgs() });
            Check(opened, "Dashboard materials action exposes navigation without fetching data");
            if (renderPath != null)
            {
                var shell = new Window { Content = view, Width = 1200, Height = 480, Background = System.Windows.Media.Brushes.Black };
                Render(shell, renderPath); shell.Close();
                var dashboard = new CommandCenterWindow(live: false) { Width = 1550, Height = 1050, WindowState = WindowState.Normal };
                ((ProductionSummaryView)dashboard.FindName("ProductionMaterials")).ShowSummary(ProductionDashboard.Build(workspace, inventory, linked, now));
                ((TextBlock)dashboard.FindName("DashboardProjects")).Text = "2 active projects";
                ((TextBlock)dashboard.FindName("DashboardProjectNames")).Text = "Obelisk / Pacifier";
                ((TextBlock)dashboard.FindName("DashboardOrders")).Text = "Saved buy and sell commitments";
                ((TextBlock)dashboard.FindName("DashboardWallet")).Text = "Saved wallet activity";
                dashboard.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => ((FrameworkElement)dashboard.FindName("FinancialCards")).BringIntoView()));
                Render(dashboard, Path.ChangeExtension(renderPath, ".full.png")); dashboard.Close();
            }
            grid.Columns[1].Width = new DataGridLength(177);
            grid.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            var reopenedView = new ProductionSummaryView();
            var reopenedGrid = (DataGrid)reopenedView.FindName("DashboardMaterialGaps");
            reopenedGrid.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check(reopenedGrid.Columns[1].Width.Value == 177, "Dashboard material column widths survive reopening");
            reopenedGrid.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            var store = new IndustryWorkspaceStore(Path.Combine(folder, "workspace"));
            var projectId = store.Create("Navigation fixture", 34, 20, null, new HashSet<long> { 42 });
            long revision = store.Snapshot().Revision;
            var workspaceView = new IndustryWorkspaceView();
            workspaceView.OpenMaterials(); // Requested before the async page initialization completes.
            workspaceView.Initialize(store, () => inventory, linked);
            Check(((TabControl)workspaceView.FindName("WorkspaceTabs")).SelectedItem == workspaceView.FindName("MaterialsTab") &&
                ((FrameworkElement)workspaceView.FindName("ProjectWorkspace")).Visibility == Visibility.Visible,
                "Dashboard route opens consolidated materials after deferred workspace initialization");
            Check(store.Snapshot().Revision == revision && store.Snapshot().Projects.Single().Id == projectId,
                "Dashboard navigation preserves project identity and does not mutate the ledger");
            view.ShowUnavailable();
            Check(grid.Items.Count == 0 && grid.Visibility == Visibility.Collapsed, "Failed workspace load clears previous dashboard quantities");
        }
        finally { WorkspaceLayout.PreferencePath = previous; Directory.Delete(folder, true); }
    }
}
