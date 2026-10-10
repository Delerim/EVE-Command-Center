using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckIndustryWorkspace(string? renderPath = null)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ecc-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CheckIndustryPlanning(folder, renderPath);
            CheckMarketOrders(folder, renderPath);
            bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
            var material = new IndustryProjectNode { TypeId = 34, Quantity = 80, Strategy = "Buy" };
            var assembly = new IndustryProjectNode { TypeId = 19744, Quantity = 1 };
            material.ParentId = assembly.Id;
            var shoppingSecond = new IndustryProjectNode { TypeId = 34, Quantity = 40, Strategy = "Use Stock" };
            var shoppingState = new IndustryWorkspace { Projects = new()
            {
                new() { Name = "One", Nodes = new() { assembly, material } },
                new() { Name = "Two", Nodes = new() { shoppingSecond } }
            } };
            var shopping = IndustryShoppingList.Build(shoppingState, Array.Empty<IndustryStockSource>(), DateTimeOffset.UtcNow);
            Check(shopping.Count == 1 && shopping[0].Required == 120 && shopping[0].Unreserved == 120,
                "Shopping groups shared materials without counting manufactured parent output as inputs");
            assembly.Strategy = "Buy";
            shopping = IndustryShoppingList.Build(shoppingState, Array.Empty<IndustryStockSource>(), DateTimeOffset.UtcNow);
            Check(shopping.Single(r => r.TypeId == 34).Required == 40 && shopping.Any(r => r.TypeId == 19744),
                "Buying a parent excludes its manufacturing subtree from shopping demand");
            shoppingState.Projects[1].Paused = true;
            Check(IndustryShoppingList.Build(shoppingState, Array.Empty<IndustryStockSource>(), DateTimeOffset.UtcNow).Count == 1,
                "Paused projects do not add procurement demand");
            shoppingSecond.TypeId = 19744;
            var shoppingNow = DateTimeOffset.UtcNow;
            var shoppingSource = new IndustryStockSource(41, "Builder", 999, 19744, 60003760, "Hangar", "Station", 10, shoppingNow, true, "Observed");
            shoppingState.Reservations.Add(new() { NodeId = shoppingSecond.Id, TypeId = 19744, OwnerId = 41, ItemId = 999, LocationId = 60003760, LocationFlag = "Hangar", Path = "Station", Quantity = 4 });
            shopping = IndustryShoppingList.Build(shoppingState, new[] { shoppingSource }, shoppingNow);
            Check(shopping[0].FreeStock == 6 && shopping[0].Reserved == 0,
                "Paused project reservations remain deducted from globally free stock");
            shopping = IndustryShoppingList.Build(shoppingState, new[] { shoppingSource with { Path = "Moved" } }, shoppingNow);
            Check(shopping[0].FreeStock == 0 && shopping[0].Review.Contains("needs review"),
                "Unresolved stock reservations block misleading free-stock totals");
            var linked = new HashSet<long> { 41, 42, 43 };
            var store = new IndustryWorkspaceStore(folder);
            Check(store.Error == "" && store.Snapshot().DefaultLeadId == null && store.Snapshot().Projects.Count == 0,
                "Fresh industry workspace has no hardcoded or forced character defaults");
            Check(Reject(() => store.SaveDefaults(999, null, null, new(), linked)), "Planning defaults reject characters that are not linked");
            store.SaveDefaults(41, 42, 43, new() { [41] = IndustryCharacterRole.Manufacturing, [42] = IndustryCharacterRole.Buying }, linked);
            Guid project = store.Create("Capital project", 19744, 1, 20185, linked);
            Guid root = store.Snapshot().Projects.Single().Nodes.Single().Id;
            Guid child = store.AddComponent(project, root, 34, 80);
            store.EditNode(project, child, 80, 42, "Use Stock", "Component handoff requires hauling", linked);
            store.SaveDefaults(43, null, 42, new(), linked);
            var saved = store.Snapshot().Projects.Single();
            Check(saved.Id == project && saved.LeadId == 41 && saved.Nodes.Single(n => n.Id == child).ExecutorId == 42,
                "Changing defaults preserves project identity, existing lead and delegated executor");
            store.EditProject(project, "Capital project", 43, 42, 41, false, false, "Shared plan", linked);
            Check(store.Snapshot().Projects.Single().Nodes.Single(n => n.Id == child).ParentId == root,
                "Lead reassignment keeps the complete dependency tree consolidated");
            var now = DateTimeOffset.UtcNow;
            var pilot = new IndustryPilot { Id = 42, Name = "Component builder", Updated = now, Error = "", Assets = new()
            {
                new() { ItemId = 900, TypeId = 3293, IsSingleton = true, Quantity = 1, LocationId = 60003760, LocationType = "station", LocationFlag = "Hangar" },
                new() { ItemId = 901, TypeId = 3293, IsSingleton = true, Quantity = 1, LocationId = 900, LocationType = "item", LocationFlag = "Unlocked" },
                new() { ItemId = 902, TypeId = 34, Quantity = 100, LocationId = 901, LocationType = "item", LocationFlag = "Unlocked" },
                new() { ItemId = 903, TypeId = 34, Quantity = 999, LocationId = 123, LocationType = "item", LocationFlag = "Cargo" }
            } };
            var inventory = IndustryWorkspaceInventory.Read(new[] { pilot }, now);
            var source = inventory.Single(s => s.ItemId == 902);
            Check(source.Verified && source.Path.Contains("60003760") && source.Path.Contains("900") && source.Path.Contains("901"),
                "Nested containers resolve by stable parent item IDs with location provenance");
            Check(!inventory.Single(s => s.ItemId == 903).Verified,
                "Cargo and unresolved ancestry cannot be treated as accessible hangar stock");
            Check(!IndustryWorkspaceInventory.Read(new[] { pilot }, now, new HashSet<long>()).Single(s => s.ItemId == 902).Verified,
                "Missing character asset permission cannot authorize cached stock reservations");
            store.Reserve(project, child, source, 80, now, inventory);
            Guid second = store.Create("Second project", 34, 80, null, linked);
            Guid secondNode = store.Snapshot().Projects.Single(p => p.Id == second).Nodes.Single().Id;
            Check(Reject(() => store.Reserve(second, secondNode, source, 21, now, inventory)),
                "Shared Tritanium stack cannot satisfy competing projects twice");
            store.Reserve(second, secondNode, source, 20, now, inventory);
            Check(store.Snapshot().Reservations.Sum(r => r.Quantity) == 100,
                "Cross-project allocation uses only the observed physical quantity");
            Check(Reject(() => store.EditNode(project, child, 79, 42, "Make", "", linked)),
                "Planned quantity cannot shrink below reserved quantity without explicit release");
            Check(Reject(() => store.Reserve(second, secondNode, source with { ItemId = 999, SnapshotUtc = now.AddHours(-2) }, 1, now, inventory)) &&
                  Reject(() => store.Reserve(second, secondNode, source with { ItemId = 999, Verified = false }, 1, now, inventory)),
                "Stale and failed snapshots never produce new reservations");
            Check(Reject(() => store.Reserve(second, secondNode, source with { ItemId = 999, TypeId = 35 }, 1, now, inventory)),
                "Reservation rejects a different product type");
            var state = store.Snapshot(); var reservation = state.Reservations.First();
            Check(IndustryWorkspaceInventory.Review(reservation, state.Reservations, new[] { source with { Quantity = 50 } }, now).StartsWith("SHORT STOCK"),
                "Reduced stock is flagged for review without deleting saved allocations");
            Check(IndustryWorkspaceInventory.Review(reservation, state.Reservations, new[] { source with { LocationId = 888 } }, now).StartsWith("MOVED"),
                "Moved stock is not interpreted as a completed transfer");
            Check(IndustryWorkspaceInventory.Review(reservation, state.Reservations, Array.Empty<IndustryStockSource>(), now).StartsWith("UNVERIFIED"),
                "Disappeared stock retains an uncertain reservation instead of claiming consumption");
            var replacedStack = source with { ItemId = 990 };
            Check(Reject(() => store.Reserve(second, secondNode, replacedStack, 1, now, new[] { replacedStack })),
                "A new stack ID cannot silently double-allocate material with unresolved old reservations");
            Check(Reject(() => store.DeleteProject(project)), "Projects holding reservations cannot be deleted");
            var reopened = new IndustryWorkspaceStore(folder);
            Check(reopened.Snapshot().Projects.Single(p => p.Id == project).Nodes.Single(n => n.Id == child).ExecutorId == 42 && reopened.Snapshot().Reservations.Count == 2,
                "Restart preserves project hierarchy, executing toon, exact stock source and reservations");
            var staleWriter = new IndustryWorkspaceStore(folder);
            int oldEvents = store.Snapshot().Events.Count;
            store.Release(reservation.Id);
            Check(store.Snapshot().Events.Count > oldEvents && store.Snapshot().Reservations.Count == 1,
                "Explicit reservation release retains historical audit events");
            Check(Reject(() => staleWriter.AddComponent(project, root, 35, 1)),
                "An out-of-date workspace instance cannot overwrite newer saved edits");
            var cyclic = store.Snapshot(); var cyclicProject = cyclic.Projects.Single(p => p.Id == project);
            cyclicProject.Nodes.Single(n => n.Id == root).ParentId = child;
            Check(Reject(() => IndustryWorkspaceStore.Validate(cyclic)), "Cyclic project dependencies are rejected before persistence");
            pilot.Assets.Single(a => a.ItemId == 900).LocationId = 901; pilot.Assets.Single(a => a.ItemId == 900).LocationType = "item";
            Check(!IndustryWorkspaceInventory.Read(new[] { pilot }, now).Single(s => s.ItemId == 902).Verified,
                "Cyclic containment is unverified rather than an infinite inventory traversal");
            pilot.Assets.Single(a => a.ItemId == 900).LocationId = 60003760; pilot.Assets.Single(a => a.ItemId == 900).LocationType = "station";
            // Exercise the real embedded UI with isolated state and a linked-character fixture.
            var view = new IndustryWorkspaceView();
            view.Initialize(store, () => new[] { pilot }, new()
            {
                new() { CharacterId = 41, CharacterName = "Lead planner" },
                new() { CharacterId = 42, CharacterName = "Component builder", Scopes = new[] { "esi-assets.read_assets.v1" } },
                new() { CharacterId = 43, CharacterName = "Market pilot" }
            });
            Check(((ListBox)view.FindName("Projects")).Items.Count == 2 && ((TreeView)view.FindName("Nodes")).Items.Count == 1,
                "Embedded production workspace shows consolidated projects and expandable root without ESI calls");
            var tree = (TreeView)view.FindName("Nodes");
            var children = (System.Collections.IEnumerable)tree.Items[0].GetType().GetProperty("Children")!.GetValue(tree.Items[0])!;
            var childRow = children.Cast<object>().Single();
            typeof(IndustryWorkspaceView).GetMethod("Node_Selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(view, new object[] { tree, new RoutedPropertyChangedEventArgs<object>(null!, childRow) });
            var stockGrid = (DataGrid)view.FindName("Stock");
            Check(stockGrid.Items.Count == 2 && ((TextBlock)view.FindName("NodeTitle")).Text.Contains("Tritanium"),
                "Expanding a component exposes matching inventory and its executing-toon editor");
            stockGrid.SelectedIndex = 0;
            ((TextBox)view.FindName("ReserveQuantity")).Text = "40";
            typeof(IndustryWorkspaceView).GetMethod("Reserve_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(view, new object[] { stockGrid, new RoutedEventArgs() });
            Check(new IndustryWorkspaceStore(folder).Snapshot().Reservations.Any(r => r.NodeId == child && r.Quantity == 40 && r.ItemId == 902),
                "Embedded source-selection action persists an exact reservation and survives reopening");
            if (renderPath != null)
            {
                var shell = new IndustryWindow { Width = 1480, Height = 960 };
                BackgroundOperations.Stop();
                ((TabItem)shell.FindName("ProductionTab")).Content = view;
                ((TabControl)shell.FindName("IndustryTabs")).SelectedItem = shell.FindName("ProductionTab");
                Check(((FrameworkElement)shell.FindName("LiveIndustryHeader")).Visibility == Visibility.Visible,
                    "Production queue retains the shared Industry header alongside Jobs");
                ((TabControl)view.FindName("WorkspaceTabs")).SelectedItem = view.FindName("ProjectDetailTab");
                Render(shell, renderPath);
                ((TabControl)view.FindName("WorkspaceTabs")).SelectedItem = view.FindName("MaterialsTab");
                ((Expander)view.FindName("ShoppingExpander")).IsExpanded = true;
                Render(shell, Path.ChangeExtension(renderPath, ".materials.png"));
                ((Expander)view.FindName("ShoppingExpander")).IsExpanded = false;
                ((TabControl)view.FindName("WorkspaceTabs")).SelectedItem = view.FindName("ProjectDetailTab");
                ((TabControl)view.FindName("ComponentTabs")).SelectedIndex = 2;
                ((ScrollViewer)view.FindName("EditorScroll")).ScrollToTop();
                Render(shell, Path.ChangeExtension(renderPath, ".stock.png")); shell.Close();
            }
            string path = Path.Combine(folder, "industry-workspace.json");
            string good = File.ReadAllText(path);
            File.WriteAllText(path, "not json");
            var broken = new IndustryWorkspaceStore(folder);
            Check(broken.Error.Length > 0 && Reject(() => broken.Create("No overwrite", 34, 1, null, linked)) && File.ReadAllText(path) == "not json",
                "Corrupt planning files fail closed and are not silently replaced with an empty workspace");
            broken.RestoreBackup();
            Check(broken.Error == "" && broken.Snapshot().Projects.Count == 2 && Directory.GetFiles(folder, "*.unreadable-*").Length == 1,
                "Explicit backup recovery restores projects and preserves the unreadable original");
            Check(JsonDocument.Parse(File.ReadAllText(path + ".bak")).RootElement.GetProperty("SchemaVersion").GetInt32() == 1,
                "Recovery preserves the known-good backup rather than replacing it with the corrupt primary");
            File.WriteAllText(path, good.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99"));
            var future = new IndustryWorkspaceStore(folder);
            Check(future.Error.Length > 0 && Reject(() => future.RestoreBackup()), "Unknown future schemas cannot be downgraded through backup recovery");
            File.WriteAllText(path, good);
            var blocked = new IndustryWorkspaceStore(folder); long revision = blocked.Snapshot().Revision;
            using (var hold = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                bool failed = false;
                try { blocked.Create("Locked", 34, 1, null, linked); } catch (IOException) { failed = true; }
                Check(failed && blocked.Snapshot().Revision == revision && File.ReadAllText(path) == good,
                    "Failed persistence leaves both in-memory planning state and prior disk contents intact");
            }
        }
        finally { Directory.Delete(folder, true); }
    }
}
