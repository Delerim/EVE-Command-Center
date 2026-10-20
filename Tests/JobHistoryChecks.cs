using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
internal static partial class Program
{
    private static void CheckJobHistory(string? renderPath = null)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ecc-job-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var oldLayout = WorkspaceLayout.PreferencePath;
        try
        {
            WorkspaceLayout.PreferencePath = Path.Combine(folder, "layout.json");
            var store = new IndustryWorkspaceStore(folder);
            var recipe = IndustryCatalog.Recipes.First(r => r.Activity == "manufacturing" && r.Name == "Capital Propulsion Engine Blueprint");
            var now = DateTimeOffset.UtcNow;
            var authorized = new HashSet<long> { 42 };
            var service = new IndustryService(new EveSsoService(), folder);
            service.JobsObserved += rows => store.ObserveLinkedJobs(rows, now);
            var project = store.CreateCalculated(IndustryDependencyPlanner.Build(recipe.Blueprint, 10), authorized);
            var node = store.Snapshot().Projects.Single().Nodes[0];
            var job = new IndustryJobLink(42, 100, 42, node.TypeId, recipe.Blueprint, 900, 60003760, 3, "active", now.AddDays(-1), now.AddHours(1), now.AddMinutes(-10));
            store.MatchJob(project, node.Id, job, authorized, now);
            Check(store.Snapshot().JobHistory.Count == 1 && store.Snapshot().SchemaVersion == 4, "Manual job association saves initial permanent evidence");
            long revision = store.Snapshot().Revision;
            store.ObserveLinkedJobs(new[] { job with { SnapshotUtc = now.AddMinutes(-9) } }, now);
            Check(store.Snapshot().Revision == revision, "Unchanged job responses do not rewrite workspace or duplicate history");
            store.ObserveLinkedJobs(new[] { job with { JobId = 200, Status = "ready", SnapshotUtc = now } }, now);
            Check(store.Snapshot().Revision == revision, "Unlinked jobs are never silently associated or retained as project output");
            store.ObserveLinkedJobs(new[] { job with { BlueprintItemId = 901, Status = "ready", SnapshotUtc = now } }, now);
            Check(store.Snapshot().Revision == revision, "Changed job identity cannot rewrite linked history");
            var paused = job with { Status = "paused", SnapshotUtc = now.AddMinutes(-8) };
            service.PublishJobObservations(42, "Builder", new() { JsonSerializer.SerializeToElement(new
            {
                activity_id = 1, job_id = paused.JobId, installer_id = paused.InstallerId, product_type_id = paused.ProductTypeId,
                blueprint_type_id = paused.BlueprintTypeId, blueprint_id = paused.BlueprintItemId, facility_id = paused.FacilityId,
                runs = paused.Runs, status = paused.Status, start_date = paused.StartUtc, end_date = paused.EndUtc
            }) }, paused.SnapshotUtc);
            Check(store.Snapshot().Projects.Single().Nodes[0].ActualJob?.Status == "paused", "Successful job-response hook persists linked status transitions");
            var resumed = job with { SnapshotUtc = now.AddMinutes(-7), EndUtc = now.AddHours(2) };
            store.ObserveLinkedJobs(new[] { resumed }, now);
            var ready = resumed with { Status = "ready", SnapshotUtc = now.AddMinutes(-5) };
            store.ObserveLinkedJobs(new[] { ready }, now);
            Check(store.Snapshot().JobHistory.Select(h => h.Job.Status).SequenceEqual(new[] { "active", "paused", "active", "ready" }), "Pause, resume and ready transitions retain ordered observations");
            revision = store.Snapshot().Revision;
            store.ObserveLinkedJobs(new[] { resumed with { SnapshotUtc = now.AddMinutes(-4) } }, now);
            Check(store.Snapshot().Revision == revision, "Ready jobs cannot regress to active from an inconsistent response");
            var delivered = ready with { Status = "delivered", SnapshotUtc = now.AddMinutes(-2) };
            store.ObserveLinkedJobs(new[] { delivered }, now);
            var state = store.Snapshot();
            Check(state.Reservations.Count == 0 && state.JobHistory.Count == 5, "Collected EVE job records history without inventing receipts or consumption");
            node = state.Projects.Single().Nodes[0];
            var progress = IndustryProductionProgress.Build(node, state, Array.Empty<IndustryJobLink>(), now.AddDays(90));
            Check(progress.InProduction == 0 && progress.AwaitingCollection == 0 && progress.Delivered == 0 && progress.JobStatus.Contains("Collected"), "Terminal evidence survives EVE retention without becoming a project receipt");
            revision = state.Revision;
            store.ObserveLinkedJobs(new[] { delivered }, now);
            store.ObserveLinkedJobs(new[] { ready with { SnapshotUtc = now } }, now);
            Check(store.Snapshot().Revision == revision, "Duplicate or regressed terminal observations leave saved history unchanged");
            new IndustryLedger(folder).Ensure();
            var reopened = new IndustryWorkspaceStore(folder);
            Check(reopened.Error == "" && reopened.Snapshot().JobHistory.Count == 5, "Permanent batch history survives SQLite migration and reopening");

            service.PublishJobObservations(42, "Builder", new(), now);
            Check(service.JobHistoryError == "" && store.Snapshot().Revision == revision, "Empty successful job response never implies completion or deletes history");
            service.JobsObserved += _ => throw new IOException("Synthetic persistence failure");
            service.PublishJobObservations(42, "Builder", new(), now);
            Check(service.JobHistoryError.Length > 0 && service.Status.Contains("Job history"), "History persistence failure is visible without aborting industry refresh");
            if (renderPath != null)
            {
                var view = new IndustryWorkspaceView();
                view.Initialize(store, () => Array.Empty<IndustryPilot>(), new() { new() { CharacterId = 42, CharacterName = "Builder" } });
                ((FrameworkElement)view.FindName("ProjectGallery")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)view.FindName("ProjectWorkspace")).Visibility = Visibility.Visible;
                var window = new Window { Content = view, Width = 1900, Height = 850 };
                Render(window, renderPath);
                var tabs = (TabControl)view.FindName("WorkspaceTabs");
                tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string?)t.Header == "Job history");
                Check(((DataGrid)view.FindName("JobHistoryGrid")).Items.Count == 5, "Project history inspector displays every retained transition");
                Render(window, renderPath);
            }
            store.UnmatchJob(project, node.Id, job.JobId);
            Check(store.Snapshot().JobHistory.Count == 5 && store.Snapshot().Projects.Single().Nodes[0].ActualJob == null, "Unlinking removes current association but retains its complete history");
            store.DeleteProject(project);
            Check(new IndustryWorkspaceStore(folder).Snapshot().JobHistory.Count == 5, "Deleting a project preserves historical job evidence");
        }
        finally { WorkspaceLayout.PreferencePath = oldLayout; Directory.Delete(folder, true); }
    }
}
