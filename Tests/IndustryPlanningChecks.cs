using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckIndustryPlanning(string folder, string? renderPath)
    {
        bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var recipes = new List<IndustryRecipe>
        {
            new() { Blueprint = 100, Activity = "manufacturing", Products = new() { [10] = 2 }, Materials = new() { [20] = 3, [34] = 1 } },
            new() { Blueprint = 200, Activity = "manufacturing", Products = new() { [20] = 1 }, Materials = new() { [34] = 7 } }
        };
        var bp = new IndustryBlueprintLibrary.Blueprint(42, "Builder", 9001, 100, 901, "Container 901", 10, 20, 2, true, true, "Available");
        var nodes = IndustryDependencyPlanner.Build(100, 3, bp, recipes);
        Check(nodes[0].TypeId == 10 && nodes[0].PlannedRuns == 2 && nodes[0].PlannedOutput == 4 && nodes[0].Quantity == 3,
            "Blueprint planning converts blueprint to product and retains overproduction separately from demand");
        Check(nodes.Single(n => n.TypeId == 20).Quantity == 6 && nodes.Where(n => n.TypeId == 34).Select(n => n.Quantity).Order().SequenceEqual(new long[] { 2, 42 }),
            "Recursive inputs use batch rounding and at least one input per run; child ME defaults to zero");
        Check(nodes[0].BlueprintItemId == 9001 && nodes[0].ExecutorId == 42 && nodes.All(n => n.CalculationSource.Length == 64),
            "Calculated plan retains selected blueprint identity, executor and static catalog hash");
        Check(Reject(() => IndustryDependencyPlanner.Build(100, 5, bp, recipes)) && Reject(() => IndustryDependencyPlanner.Build(100, 1, bp with { Available = false }, recipes)),
            "Planner rejects insufficient BPC runs and unavailable blueprints");
        recipes[1].Materials.Add(10, 1);
        Check(Reject(() => IndustryDependencyPlanner.Build(100, 1, null, recipes)), "Cyclic recipes abort rather than producing partial dependency plans");
        recipes[1].Materials.Remove(10);
        var obelisk = IndustryCatalog.Recipes.Single(r => r.Activity == "manufacturing" && IndustryCatalog.Name(r.Blueprint) == "Obelisk Blueprint");
        var full = IndustryDependencyPlanner.Build(obelisk.Blueprint, 1);
        Check(full.Count > 10 && full[0].TypeId == obelisk.Products.Single().Key && full.Any(n => n.ParentId != full[0].Id && n.ParentId != null),
            "Actual Obelisk recipe expands into components and their own material breakdowns");

        var linked = new HashSet<long> { 42 };
        var store = new IndustryWorkspaceStore(Path.Combine(folder, "planner"));
        Guid id = store.CreateCalculated(nodes, linked);
        Check(Reject(() => store.CreateCalculated(IndustryDependencyPlanner.Build(100, 1, bp, recipes), linked)),
            "The same blueprint copy runs cannot be committed across projects twice");
        nodes[0].Quantity = 999;
        var saved = new IndustryWorkspaceStore(Path.Combine(folder, "planner")).Snapshot();
        Check(saved.Projects.Single().Nodes[0].Quantity == 3 && saved.Projects.Single().Nodes[0].BlueprintItemId == 9001,
            "Calculated blueprint plan persists through restart and cannot be mutated by its caller");
        var root = saved.Projects.Single().Nodes[0];
        Check(Reject(() => store.EditNode(id, root.Id, 7, 42, "Make", "", linked)),
            "Calculated quantities cannot be edited without recalculating dependent materials");
        Guid draft = store.Create("Obelisk Blueprint", obelisk.Blueprint, 1, null, linked);
        Guid oldRoot = store.Snapshot().Projects.Single(p => p.Id == draft).Nodes[0].Id;
        store.ExpandDraft(draft, full);
        Check(store.Snapshot().Projects.Single(p => p.Id == draft).Nodes[0].Id == oldRoot && store.Snapshot().Projects.Single(p => p.Id == draft).Name == "Obelisk",
            "Old blueprint-as-product draft expands into product requirements while retaining project/root identity");
        Check(Reject(() => store.ExpandDraft(draft, full)), "Re-expansion cannot silently replace existing component assignments");
        store.DeleteProject(id);
        Check(new IndustryWorkspaceStore(Path.Combine(folder, "planner")).Snapshot().Projects.All(p => p.Id != id) && store.Snapshot().Events.Any(e => e.ProjectId == id && e.Description.StartsWith("Deleted")),
            "Project deletion persists and retains its audit history");

        var now = DateTimeOffset.UtcNow;
        var pilot = new IndustryPilot { Id = 42, Name = "Blueprint owner", Updated = now, Error = "", Assets = new()
        {
            new() { ItemId = 901, TypeId = 3293, IsSingleton = true, LocationId = 60003760, LocationType = "station", LocationFlag = "Hangar" }
        } };
        pilot.Blueprints = Enumerable.Range(1, 2100).Select(i => JsonSerializer.SerializeToElement(new
        { item_id = (long)i + 10000, type_id = obelisk.Blueprint, location_id = i % 2 == 0 ? 901L : 60003760L, quantity = -2, runs = 2, material_efficiency = 10, time_efficiency = 20 })).ToList();
        var library = IndustryBlueprintLibrary.Read(new[] { pilot }, linked, now);
        Check(library.Count == 2100 && library.Count(b => b.Location.Contains("container 901")) == 1050,
            "Blueprint library retains over 2000 instances and resolves specific container paths");
        Check(IndustryBlueprintLibrary.Read(new[] { pilot }, new HashSet<long>(), now).All(b => !b.Available),
            "Blueprint library does not claim availability without blueprint scope");
        pilot.Jobs.Add(JsonSerializer.SerializeToElement(new { blueprint_id = 10001L, status = "active" }));
        Check(!IndustryBlueprintLibrary.Read(new[] { pilot }, linked, now).Single(b => b.ItemId == 10001).Available,
            "Blueprints in active industry jobs are unavailable for new planning");
        var view = new IndustryWorkspaceView();
        view.Initialize(store, () => new[] { pilot }, new() { new() { CharacterId = 42, CharacterName = "Blueprint owner", Scopes = new[] { "esi-characters.read_blueprints.v1", "esi-assets.read_assets.v1" } } });
        var locations = (System.Windows.Controls.ComboBox)view.FindName("BlueprintLocation");
        locations.SelectedItem = locations.Items.Cast<string>().Single(s => s.Contains("container 901"));
        var grid = (DataGrid)view.FindName("Blueprints");
        Check(grid.Items.Count == 1050, "Selecting a blueprint container filters the real embedded library table");
        grid.SelectedIndex = 0;
        typeof(IndustryWorkspaceView).GetMethod("CreateBlueprint_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, new object[] { grid, new RoutedEventArgs() });
        Check(store.Snapshot().Projects.Count == 2 && store.Snapshot().Projects.Last().Nodes[0].BlueprintOwnerId == 42 && store.Snapshot().Projects.Last().Nodes.Count > 10,
            "Library create action persists a complete component tree from the selected blueprint instance");
        if (renderPath != null)
        {
            var shell = new IndustryWindow { Width = 1480, Height = 960 };
            BackgroundOperations.Stop();
            ((TabItem)shell.FindName("ProductionTab")).Content = view;
            ((TabControl)view.FindName("WorkspaceTabs")).SelectedItem = view.FindName("LibraryTab");
            Render(shell, Path.ChangeExtension(renderPath, ".library.png"));
            ((TabControl)view.FindName("WorkspaceTabs")).SelectedItem = view.FindName("ProjectDetailTab");
            ((ScrollViewer)view.FindName("EditorScroll")).ScrollToVerticalOffset(180);
            Render(shell, Path.ChangeExtension(renderPath, ".tree.png"));
            shell.Close();
        }
        var delete = typeof(IndustryWorkspaceView).GetMethod("DeleteProject_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        delete.Invoke(view, new object[] { view, new RoutedEventArgs() });
        Check(store.Snapshot().Projects.Count == 2, "Delete UI requires explicit inline confirmation");
        ((CheckBox)view.FindName("ConfirmDelete")).IsChecked = true;
        delete.Invoke(view, new object[] { view, new RoutedEventArgs() });
        Check(store.Snapshot().Projects.Count == 1, "Confirmed delete removes only the selected project");
    }
}
