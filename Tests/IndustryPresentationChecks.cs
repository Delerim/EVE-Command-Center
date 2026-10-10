using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
internal static partial class Program
{
    private static void RenderCloneNames(string renderPath)
    {
        var pilot = new OmegaPilot { Id = 42, Name = "Blueprint owner", Error = "", Updated = DateTimeOffset.UtcNow,
            LocationNames = new() { [60003760] = "Jita IV - Moon 4 - Caldari Navy Assembly Plant", [60008740] = "Joppaya IX - Moon 9 - Ardishapur Family Bureau" },
            ImplantNames = new() { [10208] = "Training implant", [10212] = "Navigation implant" },
            Clones = JsonSerializer.SerializeToElement(new { home_location = new { location_id = 60003760 }, jump_clones = new[] {
                new { name = "Mining clone", location_id = 60008740, implants = new[] { 10208, 10212 } },
                new { name = "Travel clone", location_id = 60003760, implants = Array.Empty<int>() } } }) };
        var window = new OmegaWindow { Width = 1600, Height = 1300 };
        BackgroundOperations.Stop();
        ((DataGrid)window.FindName("Pilots")).ItemsSource = new[] { pilot };
        ((DataGrid)window.FindName("Pilots")).SelectedItem = pilot;
        Check(((ItemsControl)window.FindName("CloneCards")).Items.Count == 2 && ((TextBlock)window.FindName("CloneSummary")).Text.Contains("Jita"), "Clone inspector renders named stations and individual clone cards");
        var cards = ((ItemsControl)window.FindName("CloneCards")).Items.Cast<object>().ToArray();
        Check((string?)cards[0].GetType().GetProperty("Name")!.GetValue(cards[0]) == "Mining clone", "Supplied custom clone names are preserved");
        Render(window, Path.ChangeExtension(renderPath, ".clones.png"));
        window.Close();
    }
    private static void CheckIndustryPresentation(string folder)
    {
        var now = DateTimeOffset.UtcNow;
        var recipe = IndustryCatalog.Recipes.First(r => r.Activity == "manufacturing" && r.Skills.Count > 0 && r.Products.Count == 1);
        var node = new IndustryProjectNode { TypeId = recipe.Products.Single().Key, BlueprintTypeId = recipe.Blueprint, Strategy = "Make", ExecutorId = 42, PlannedRuns = 5 };
        var pilot = new IndustryPilot { Id = 42, Name = "Builder", Error = "", Updated = now, Skills = new(recipe.Skills) };
        var auth = new EvePilotProfile { CharacterId = 42, CharacterName = "Builder", Scopes = new[] { "esi-characters.read_blueprints.v1", "esi-skills.read_skills.v1" } };
        var copy = new IndustryBlueprintLibrary.Blueprint(42, "Builder", 123, recipe.Blueprint, 60003760, "Jita IV - Moon 4", 10, 20, 5, true, true, "Available");
        IndustryPresentation.ReadinessState State() => IndustryPresentation.Readiness(node, new[] { pilot }, new[] { auth }, new[] { copy }, now, 42);
        Check(State().Summary == "\u2713 BPC   \u2713 Skills", "Readiness confirms a usable copy and trained recipe skills for the assigned pilot");
        pilot.Skills.Clear();
        Check(State().Summary.Contains("\u00d7 Skills") && State().Detail.Contains("Missing:"), "Known missing skills are distinct from unknown data and list their names");
        pilot.Updated = now.AddHours(-3);
        Check(State().Summary.Contains("? Skills") && State().Summary.Contains("? Blueprint"), "Stale snapshots cannot claim skill or blueprint readiness");
        pilot.Updated = now; copy = copy with { Runs = 2 };
        Check(State().Summary.Contains("! Blueprint"), "A copy with insufficient runs is not marked available for this build");
        copy = copy with { Runs = 5 }; node.ExecutorId = null;
        pilot.Skills = new(recipe.Skills);
        Check(State().Summary == "\u2713 BPC   \u2713 Skills", "Unassigned components check project owner blueprint and skills");
        node.ExecutorId = 99;
        Check(State().Summary.Contains("\u2713 BPC") && State().Summary.Contains("? Skills"), "Changing executor leaves owner blueprint check intact and checks new executor skills");
        node.ExecutorId = null;
        Check(IndustryPresentation.Readiness(node, new[] { pilot }, new[] { auth }, new[] { copy with { OwnerId = 99 } }, now, 42).Summary.Contains("\u00d7 Blueprint"), "Another pilot blueprint cannot satisfy owner availability");
        var project = new IndustryProject { LeadId = 42, Nodes = new() { node } };
        var requirements = IndustryProjectSkills.Read(project);
        Check(requirements.Requirements.Count == recipe.Skills.Count && requirements.MissingRecipes == 0, "Project aggregates root recipe skills");
        var child = new IndustryProjectNode { ParentId = node.Id, BlueprintTypeId = recipe.Blueprint, Strategy = "Make", Name = "Component" };
        project.Nodes.Add(child);
        Check(IndustryProjectSkills.Read(project).Requirements.Count == recipe.Skills.Count, "Repeated skill requirements are deduplicated");
        child.BlueprintTypeId = int.MaxValue;
        Check(IndustryProjectSkills.Read(project).MissingRecipes == 1, "Missing component recipes prevent false complete readiness");
        child.Strategy = "Buy";
        Check(IndustryProjectSkills.Read(project).MissingRecipes == 0, "Bought component skills are excluded");
        var workspace = new IndustryWorkspace { Projects = new() { project } };
        var absent = IndustryShoppingList.Build(workspace, Array.Empty<IndustryStockSource>(), now, true);
        Check(absent.All(r => r.OwnedLabel == "N/A"), "Complete inventories with no matching stock show N/A");
        Check(IndustryShoppingList.Build(workspace, Array.Empty<IndustryStockSource>(), now).All(r => r.OwnedLabel == "Unknown"), "Incomplete inventories never claim absence");
        Check(!IndustryPresentation.History("Assigned local manufacturing plan for component 12345678-1234-1234-1234-123456789abc to 999999; facility 60003760.").Contains("999999"), "History presentation hides internal keys while retaining the original evidence");
        var omega = new OmegaPilot { Clones = JsonSerializer.SerializeToElement(new { home_location = new { location_id = 60003760, location_type = "station" } }), LocationNames = new() { [60003760] = "Jita IV - Moon 4 - Caldari Navy Assembly Plant" } };
        Check(omega.Home.StartsWith("Jita") && !omega.Home.Contains("60003760"), "Clone home resolves to a station name");
        omega.LocationNames.Clear();
        Check(omega.Home == "Location name unavailable", "An unresolved clone station never exposes its numeric key");
        Check(IndustryCatalog.Name(int.MaxValue) == "Item name unavailable", "Missing item names have an honest human-readable fallback");
        var covered = new IndustryShoppingList.Row(34, 100, 20, 80, 90, "Project", "") { HasVerifiedStock = true };
        Check(covered.MissingLabel == "0" && covered.Unreserved == 80 && covered.StockStatus == "Stock covers demand", "Available stock is distinct from unreserved allocation demand");
        Check((covered with { FreeStock = 30 }).MissingLabel == "50", "Shortfall subtracts available stock and reservations for these builds");
        Check((covered with { HasVerifiedStock = false }).MissingLabel == "Unknown", "Unknown stock cannot claim a calculated shortfall");
        Check((covered with { Review = "Reservation/source needs review" }).MissingLabel == "Review", "Unverified reservations block shortage claims");
        var descending = new TableSorting.Values("SortMissing", System.ComponentModel.ListSortDirection.Descending);
        var ascending = new TableSorting.Values("SortMissing", System.ComponentModel.ListSortDirection.Ascending);
        var small = covered with { FreeStock = 78 }; var large = covered with { FreeStock = 70 }; var unknown = covered with { HasVerifiedStock = false };
        Check(descending.Compare(large, small) < 0 && ascending.Compare(large, small) > 0, "Shortfall sorting uses numbers, not formatted text");
        Check(descending.Compare(unknown, small) > 0 && ascending.Compare(unknown, small) > 0, "Unknown shortfalls remain after numeric values in both directions");
        var view = new IndustryWorkspaceView();
        ((ColumnDefinition)view.FindName("NavigationWidth")).Width = new GridLength(440);
        var sortingGrid = (DataGrid)view.FindName("Shopping");
        sortingGrid.ItemsSource = new[] { unknown, large, small };
        var shortageColumn = sortingGrid.Columns.Single(c => c.Header.ToString() == "Missing stock");
        var sortMethod = typeof(DataGrid).GetMethod("OnSorting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        sortMethod.Invoke(sortingGrid, new object[] { new DataGridSortingEventArgs(shortageColumn) });
        Check(ReferenceEquals(sortingGrid.Items[0], small) && ReferenceEquals(sortingGrid.Items[2], unknown), "Clicking the actual missing-stock heading sorts small to large with unknown last");
        sortMethod.Invoke(sortingGrid, new object[] { new DataGridSortingEventArgs(shortageColumn) });
        Check(ReferenceEquals(sortingGrid.Items[0], large) && ReferenceEquals(sortingGrid.Items[2], unknown), "Clicking again reverses numeric order without putting unknown stock first");
        var grid = (DataGrid)view.FindName("Shopping"); grid.Columns[0].Width = new DataGridLength(275); grid.Columns[1].DisplayIndex = 3;
        ((Grid)view.FindName("ExistingJobsFrame")).Height = 350;
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var reopened = new IndustryWorkspaceView();
        Check(((ColumnDefinition)reopened.FindName("NavigationWidth")).Width.Value == 440 && ((DataGrid)reopened.FindName("Shopping")).Columns[0].Width.Value == 275 && ((DataGrid)reopened.FindName("Shopping")).Columns[1].DisplayIndex == 3 && ((Grid)reopened.FindName("ExistingJobsFrame")).Height == 350,
            "Pane width, table width and column order survive reopening");
        File.WriteAllText(WorkspaceLayout.PreferencePath, "{invalid");
        var recovered = new IndustryWorkspaceView();
        Check(((ColumnDefinition)recovered.FindName("NavigationWidth")).Width.Value == 390, "Invalid view preferences fall back safely without touching project data");
        File.WriteAllText(WorkspaceLayout.PreferencePath, "{\"Columns\":null,\"Heights\":null}");
        _ = new IndustryWorkspaceView();
        Check(true, "Incomplete preference collections cannot prevent the workspace from opening");
        File.Delete(WorkspaceLayout.PreferencePath);
    }
}
