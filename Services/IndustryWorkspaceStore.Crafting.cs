using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed partial class IndustryWorkspaceStore
{
    public Guid CreateCraftBundle(string name, IReadOnlyList<IndustryCraftSuggestions.Selection> selections,
        IReadOnlyList<IndustryBlueprintLibrary.Blueprint> currentBlueprints, IReadOnlyList<IndustryStockSource> stock,
        ISet<long> linked, DateTimeOffset now, IReadOnlyList<IndustryRecipe>? recipes = null, ISet<(long OwnerId, long ItemId)>? allowedSources = null)
    {
        Guid id = Guid.NewGuid();
        Change(state =>
        {
            Require(!string.IsNullOrWhiteSpace(name), "Enter a project name.");
            Require(selections.All(s => currentBlueprints.Contains(s.Blueprint)), "Blueprint snapshot changed. Reload suggestions.");
            Linked(state.DefaultLeadId, linked); Linked(state.DefaultBuyerId, linked); Linked(state.DefaultSellerId, linked);
            foreach (var selection in selections) Linked(selection.Blueprint.OwnerId, linked);
            // Recalculate against the current revision inside the same atomic write as all reservations.
            var plan = IndustryCraftSuggestions.Plan(selections, state, stock, now, recipes, allowedSources);
            state.SchemaVersion = Math.Max(state.SchemaVersion, 5);
            state.Projects.Add(new() { Id = id, IsBundle = true, Name = name.Trim(),
                LeadId = state.DefaultLeadId, BuyerId = state.DefaultBuyerId, SellerId = state.DefaultSellerId,
                Nodes = plan.Nodes, Notes = "Suggested craft bundle. Inputs reserved from observed personal stock. Verify skills, facility and delivery in EVE." });
            state.Reservations.AddRange(plan.Allocations.Select(a => new IndustryReservation { ProjectId = id, NodeId = a.NodeId,
                OwnerId = a.Source.OwnerId, ItemId = a.Source.ItemId, TypeId = a.Source.TypeId,
                LocationId = a.Source.LocationId, LocationFlag = a.Source.LocationFlag, Path = a.Source.Path,
                Quantity = a.Quantity, SnapshotUtc = a.Source.SnapshotUtc }));
        }, "Created craft bundle and reserved its combined direct inputs atomically; delivery and manufacturing unverified.", id);
        return id;
    }
}
