using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckIndustryComponents(string folder)
    {
        bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var linked = new HashSet<long> { 42, 43 }; var now = DateTimeOffset.UtcNow;
        var store = new IndustryWorkspaceStore(Path.Combine(folder, "component-flow"));
        var recipe = IndustryCatalog.Recipes.First(r => r.Activity == "manufacturing" && r.Products.Count == 1 && IndustryCatalog.Name(r.Blueprint) == "Capital Propulsion Engine Blueprint");
        var bp = new IndustryBlueprintLibrary.Blueprint(42, "Builder", 999, recipe.Blueprint, 901, "Capital copies", 10, 20, 100, true, true, "Available");
        Guid project = store.CreateCalculated(IndustryDependencyPlanner.Build(recipe.Blueprint, 10), linked);
        var root = store.Snapshot().Projects.Single().Nodes[0];
        store.SelectBlueprint(project, root.Id, bp);
        var selected = store.Snapshot().Projects.Single().Nodes[0];
        Check(selected.Id == root.Id && selected.BlueprintItemId == 999 && selected.MaterialEfficiency == 10,
            "Selecting a component blueprint preserves identity and applies its material efficiency");
        var job = new IndustryJobLink(42, 123456, 42, root.TypeId, recipe.Blueprint, 999, 60003760, 4, "active", now.AddHours(-1), now.AddHours(2), now);
        Check(Reject(() => store.MatchJob(project, root.Id, job with { ProductTypeId = 34 }, linked, now)) &&
            Reject(() => store.MatchJob(project, root.Id, job with { BlueprintItemId = 998 }, linked, now)) &&
            Reject(() => store.MatchJob(project, root.Id, job with { SnapshotUtc = now.AddHours(-2) }, linked, now)),
            "Job matching rejects wrong products, wrong blueprint instances and stale evidence");
        store.MatchJob(project, root.Id, job, linked, now);
        Check(store.Snapshot().Projects.Single().Nodes[0].ActualJob?.Runs == 4 && store.Snapshot().Reservations.Count == 0,
            "Explicit partial-job association does not invent stock or delivery");
        var restarted = new IndustryWorkspaceStore(Path.Combine(folder, "component-flow"));
        Check(restarted.Snapshot().Projects.Single().Nodes[0].ActualJob == job,
            "Actual EVE job evidence survives restart separately from local job plans");
        Guid second = store.CreateCalculated(IndustryDependencyPlanner.Build(recipe.Blueprint, 10), linked);
        var secondRoot = store.Snapshot().Projects.Single(p => p.Id == second).Nodes[0];
        Check(Reject(() => store.MatchJob(second, secondRoot.Id, job, linked, now)), "One EVE job cannot satisfy two project nodes");
        Check(Reject(() => store.EditNode(project, root.Id, 10, 43, "Make", "", linked)) &&
            Reject(() => store.SelectBlueprint(project, root.Id, bp)), "Linked manufacturing evidence cannot be silently reassigned or recalculated away");
        store.MarkTransfer(project, root.Id, true, "Builder to assembly hangar");
        Check(store.Snapshot().Projects.Single(p => p.Id == project).Nodes[0].TransferNeeded,
            "Transfer requirement is explicit persisted intent, independent of job completion");
        var p = new IndustryPilot { Id = 42, Name = "Builder", Error = "", Updated = now,
            Assets = new() { new() { ItemId = 901, TypeId = 3293, IsSingleton = true, LocationId = 1049228040142, LocationType = "item", LocationFlag = "Hangar" },
                new() { ItemId = 902, TypeId = 34, Quantity = 100, LocationId = 901, LocationFlag = "Unlocked", LocationType = "item" } } };
        Check(!IndustryWorkspaceInventory.Read(new[] { p }, now, linked).Single().Verified,
            "A missing parent is not automatically accepted as a structure");
        p.VerifiedStructures.Add(1049228040142); p.LocationNames[1049228040142] = "Assembly works"; p.AssetNames[901] = "Minerals";
        var stock = IndustryWorkspaceInventory.Read(new[] { p }, now, linked);
        Check(stock.Single().Verified && IndustryDisplayNames.Path(p, 901) == "Assembly works  /  Minerals",
            "Authorized resolved structure stock has a readable nested location and is reservable");
        Guid stockProject = store.Create("Stock test", 34, 50, null, linked); Guid stockRoot = store.Snapshot().Projects.Single(x => x.Id == stockProject).Nodes[0].Id;
        store.Reserve(stockProject, stockRoot, stock.Single(), 40, now, stock);
        p.AssetNames[901] = "Renamed minerals";
        var renamed = IndustryWorkspaceInventory.Read(new[] { p }, now, linked);
        var reservation = store.Snapshot().Reservations.Single();
        Check(IndustryWorkspaceInventory.Review(reservation, store.Snapshot().Reservations, renamed, now).StartsWith("RESERVED"),
            "Renaming a container changes display metadata without invalidating exact-stack reservations");
        store.DeleteProject(stockProject, releaseReservations: true);
        var afterDelete = new IndustryWorkspaceStore(Path.Combine(folder, "component-flow")).Snapshot();
        Check(afterDelete.Reservations.Count == 0 && afterDelete.Projects.Count == 2 && afterDelete.Events.Any(e => e.ProjectId == stockProject && e.Description.Contains("released reservations:")),
            "Confirmed project deletion atomically releases its stock and retains the deleted plan and reservation audit");
        store.UnmatchJob(project, root.Id);
        Check(store.Snapshot().Events.Any(e => e.Description.Contains("123456")), "Unlinking preserves the original EVE job association evidence in history");
        p.Jobs.Add(JsonSerializer.SerializeToElement(new { activity_id = 1, job_id = 123456, installer_id = 42, product_type_id = root.TypeId,
            blueprint_type_id = recipe.Blueprint, blueprint_id = 999, facility_id = 60003760, runs = 4, status = "ready", start_date = now.AddHours(-1), end_date = now }));
        Check(IndustryJobMatching.Read(new[] { p }, linked).Count == 1 && IndustryJobMatching.Read(new[] { p }, new HashSet<long>()).Count == 0,
            "EVE job candidates require authorized complete manufacturing evidence");
        p.Error = "Refresh failed";
        Check(IndustryJobMatching.Read(new[] { p }, linked).Count == 0, "Failed job refresh is not offered as current matching evidence");
    }
}
