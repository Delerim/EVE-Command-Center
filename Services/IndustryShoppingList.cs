using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// A projection of saved intent, not an automatic stock allocation or recipe calculation.
public static class IndustryShoppingList
{
    public sealed record Row(int TypeId, decimal Required, decimal Reserved, decimal Unreserved,
        decimal FreeStock, string Projects, string Review)
    {
        public string Name => IndustryCatalog.Name(TypeId);
    }

    public static IReadOnlyList<Row> Build(IndustryWorkspace state, IReadOnlyList<IndustryStockSource> stock, DateTimeOffset now)
    {
        var demand = new List<(IndustryProject Project, IndustryProjectNode Node)>();
        foreach (var project in state.Projects.Where(p => !p.Paused && !p.Archived))
        {
            var children = project.Nodes.ToLookup(n => n.ParentId);
            void Visit(IndustryProjectNode node)
            {
                // Buying/using a finished component replaces its manufacturing subtree.
                if (node.Strategy != "Make" || !children[node.Id].Any()) demand.Add((project, node));
                else foreach (var child in children[node.Id]) Visit(child);
            }
            foreach (var root in children[null]) Visit(root);
        }
        return demand.GroupBy(d => d.Node.TypeId).Select(group =>
        {
            var ids = group.Select(d => d.Node.Id).ToHashSet();
            var reservations = state.Reservations.Where(r => ids.Contains(r.NodeId)).ToArray();
            decimal required = group.Sum(d => (decimal)d.Node.Quantity);
            decimal reserved = reservations.Sum(r => (decimal)r.Quantity);
            bool review = state.Reservations.Where(r => r.TypeId == group.Key).Any(r =>
                !IndustryWorkspaceInventory.Review(r, state.Reservations, stock, now).StartsWith("RESERVED -", StringComparison.Ordinal));
            decimal free = stock.Where(s => s.TypeId == group.Key && s.Verified && IndustryWorkspaceInventory.Recent(s.SnapshotUtc, now))
                .GroupBy(s => (s.OwnerId, s.ItemId)).Select(g => g.First())
                .Sum(s => Math.Max(0, s.Quantity - state.Reservations.Where(r => r.OwnerId == s.OwnerId && r.ItemId == s.ItemId).Sum(r => (decimal)r.Quantity)));
            string notes = review ? "Reservation/source needs review; free stock blocked" : "Free stock is not allocated; hauling may be needed";
            if (group.Any(d => d.Node.Strategy == "Make")) notes += "; Make leaf has no input plan";
            if (group.Any(d => d.Project.Nodes.Any(n => n.Strategy == "Make" && d.Project.Nodes.Any(c => c.ParentId == n.Id) && state.Reservations.Any(r => r.NodeId == n.Id))))
                notes += "; parent stock reserved: child demand not reduced; review sourcing strategy";
            return new Row(group.Key, required, reserved, Math.Max(0, required - reserved), review ? 0 : free,
                string.Join(" | ", group.Select(d => d.Project.Name).Distinct()), notes);
        }).OrderBy(r => r.Name).ToArray();
    }
}
