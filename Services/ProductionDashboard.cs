using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Read-only dashboard projection. Shared stock is compared once across all active projects.
public static class ProductionDashboard
{
    public sealed record Material(IndustryShoppingList.Row Stock, string Name, decimal? Missing, string Status, string Color)
    {
        public string Icon => Stock.Icon;
        public decimal? Available => Stock.SortAvailable;
        public string AvailableLabel => Stock.FreeLabel;
        public string MissingLabel => Missing?.ToString("N0") ?? "Check inventory";
        public decimal Required => Stock.Required;
        public decimal Reserved => Stock.Reserved;
        public string Projects => Stock.Projects;
    }
    public sealed record Summary(int ActiveProjects, int Shortfalls, int NeedsReview, int Covered,
        string Detail, IReadOnlyList<Material> Materials);

    public static Summary Build(IndustryWorkspace workspace, IReadOnlyList<IndustryPilot> inventory,
        IReadOnlyList<EvePilotProfile> linked, DateTimeOffset now)
    {
        var authorized = linked.Where(p => p.Scopes.Contains("esi-assets.read_assets.v1")).Select(p => p.CharacterId).ToHashSet();
        var stock = IndustryWorkspaceInventory.Read(inventory, now, authorized);
        bool complete = linked.Count > 0 && linked.All(p => authorized.Contains(p.CharacterId) && inventory.Any(i =>
            i.Id == p.CharacterId && string.IsNullOrWhiteSpace(i.Error) && IndustryWorkspaceInventory.Recent(i.Updated, now)));
        var rows = IndustryShoppingList.Build(workspace, stock, now, complete).Select(row =>
        {
            decimal? missing = row.SortMissing;
            string name = inventory.Select(p => p.TypeNames.GetValueOrDefault(row.TypeId)).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? row.Name;
            return new Material(row, name, missing, missing == null ? "Check inventory" : missing > 0 ? "Missing stock" : "Stock covers demand",
                missing == null ? "#A5BBC1" : missing > 0 ? "#EDAD83" : "#76D7CB");
        }).OrderBy(r => r.Missing.HasValue ? r.Missing > 0 ? 0 : 2 : 1)
          .ThenByDescending(r => r.Missing).ThenBy(r => r.Name).ToArray();
        int active = workspace.Projects.Count(p => !p.Archived && !p.Paused);
        string detail = active == 0 ? "No active builds. Create or resume a project in Production Queue." : rows.Length == 0 ?
            "Active projects have no material plan yet. Open Industry to review their components." :
            "Shared personal stock across active projects. Paused projects keep their reservations. Stock coverage does not confirm delivery, skills or job readiness.";
        if (active > 0 && !complete) detail += " Some personal inventories need refreshing or permission.";
        return new(active, rows.Count(r => r.Missing > 0), rows.Count(r => !r.Missing.HasValue), rows.Count(r => r.Missing == 0), detail, rows);
    }
}
