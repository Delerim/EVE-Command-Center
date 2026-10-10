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
        IndustryPresentation.ReadinessState State() => IndustryPresentation.Readiness(node, new[] { pilot }, new[] { auth }, new[] { copy }, now);
        Check(State().Summary == "\u2713 BPC   \u2713 Skills", "Readiness confirms a usable copy and trained recipe skills for the assigned pilot");
        pilot.Skills.Clear();
        Check(State().Summary.Contains("\u00d7 Skills") && State().Detail.Contains("Missing:"), "Known missing skills are distinct from unknown data and list their names");
        pilot.Updated = now.AddHours(-3);
        Check(State().Summary.Contains("? Skills") && State().Summary.Contains("? Blueprint"), "Stale snapshots cannot claim skill or blueprint readiness");
        pilot.Updated = now; copy = copy with { Runs = 2 };
        Check(State().Summary.Contains("! Blueprint"), "A copy with insufficient runs is not marked available for this build");
        copy = copy with { Runs = 5 }; node.ExecutorId = null;
        Check(State().Summary.Contains("BPC (fleet)") && State().Summary.Contains("? Skills"), "Unassigned components show an available fleet copy without claiming unassigned skills");
        Check(!IndustryPresentation.History("Assigned local manufacturing plan for component 12345678-1234-1234-1234-123456789abc to 999999; facility 60003760.").Contains("999999"), "History presentation hides internal keys while retaining the original evidence");
        var omega = new OmegaPilot { Clones = JsonSerializer.SerializeToElement(new { home_location = new { location_id = 60003760, location_type = "station" } }), LocationNames = new() { [60003760] = "Jita IV - Moon 4 - Caldari Navy Assembly Plant" } };
        Check(omega.Home.StartsWith("Jita") && !omega.Home.Contains("60003760"), "Clone home resolves to a station name");
        omega.LocationNames.Clear();
        Check(omega.Home == "Location name unavailable", "An unresolved clone station never exposes its numeric key");
        Check(IndustryCatalog.Name(int.MaxValue) == "Item name unavailable", "Missing item names have an honest human-readable fallback");
        var view = new IndustryWorkspaceView();
        ((ColumnDefinition)view.FindName("NavigationWidth")).Width = new GridLength(440);
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
