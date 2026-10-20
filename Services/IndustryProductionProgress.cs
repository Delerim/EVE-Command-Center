using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

// Stages deliberately overlap: job output is not added to stock or treated as a receipt.
public static class IndustryProductionProgress
{
    public sealed record Row(Guid NodeId, int TypeId, string Name, long Required, decimal Reserved,
        decimal Delivered, decimal? InProduction, decimal? AwaitingCollection, string JobStatus, string ReceiptStatus)
    {
        public decimal StillToDeliver => Math.Max(0, Required - Delivered);
        public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
        public string ProductionText => InProduction?.ToString("N0") ?? "Unknown";
        public string CollectionText => AwaitingCollection?.ToString("N0") ?? "Unknown";
        public string Color => Delivered >= Required ? "#76D7CB" : Delivered > 0 ? "#EDAD83" : "#A5BBC1";
    }
    public static Row Build(IndustryProjectNode node, IndustryWorkspace state,
        IReadOnlyList<IndustryJobLink> observations, DateTimeOffset now)
    {
        var reservations = state.Reservations.Where(r => r.NodeId == node.Id).ToArray();
        decimal reserved = reservations.Sum(r => (decimal)r.Quantity), delivered = reservations.Sum(r => (decimal)r.DeliveredQuantity);
        decimal? production = 0, collection = 0;
        string status = node.PlannedJobId.HasValue ? "Assigned; no EVE job linked" : "No EVE job linked";
        var statuses = new List<string>();
        foreach (var linked in IndustryJobMatching.LinkedJobs(node))
        {
            var current = observations.FirstOrDefault(j => IndustryJobMatching.SameIdentity(j, linked));
            if (current == null || !IndustryWorkspaceInventory.Recent(current.SnapshotUtc, now))
            { production = collection = null; status = "Refresh Industry to check linked job"; }
            else
            {
                decimal? output = IndustryJobMatching.OutputUnits(current);
                if (current.Status is "active" or "paused") production += output;
                if (current.Status == "ready") collection += output;
                status = current.Status switch
                {
                    "active" => "Manufacturing", "paused" => "Job paused", "ready" => "Collect in EVE",
                    "delivered" => "Collected in EVE; confirm project receipt",
                    _ => "Job cancelled / reverted"
                };
            }
            statuses.Add(status);
        }
        if (statuses.Count > 1) status = string.Join(" | ", statuses.GroupBy(x => x).Select(g => $"{g.Count()} {g.Key}"));
        return new(node.Id, node.TypeId, IndustryCatalog.HasName(node.TypeId) ? IndustryCatalog.Name(node.TypeId) : node.Name,
            node.Quantity, reserved, delivered, production, collection, status,
            delivered >= node.Quantity ? "Delivered" : delivered > 0 ? "Partially delivered" : "Not delivered");
    }
    public static string Stage(IndustryProject project, IndustryWorkspace state, IReadOnlyList<IndustryJobLink> jobs, DateTimeOffset now)
    {
        if (project.Archived) return "Archived";
        if (project.Paused) return "Paused";
        var root = Build(project.Nodes.Single(n => n.ParentId == null), state, jobs, now);
        if (root.Delivered >= root.Required) return "Delivered";
        if (root.Delivered > 0) return "Partially delivered";
        var rows = project.Nodes.Select(n => Build(n, state, jobs, now)).ToArray();
        if (rows.Any(r => r.AwaitingCollection > 0)) return "Awaiting collection";
        if (rows.Any(r => r.InProduction > 0)) return "Manufacturing";
        if (rows.Any(r => r.Delivered > 0)) return "Components delivered";
        if (rows.Any(r => r.Reserved > 0)) return "Gathering materials";
        return project.Nodes.Any(n => n.ActualJob != null) ? "Check linked jobs" : project.Nodes.Any(n => n.PlannedJobId.HasValue) ? "Assigned" : "Planning";
    }
}
