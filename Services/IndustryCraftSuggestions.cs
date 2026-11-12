using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

// Direct manufacturing inputs only: owned components are consumed as stock, not expanded
// into speculative sub-jobs with unowned blueprints. All quantities use the planner's math.
public static class IndustryCraftSuggestions
{
    public sealed record Selection(IndustryBlueprintLibrary.Blueprint Blueprint, long Runs);
    public sealed record Suggestion(IndustryBlueprintLibrary.Blueprint Blueprint, IndustryRecipe Recipe,
        long RemainingRuns, long MaxRuns, decimal Coverage, string Inputs)
    {
        public string Name => IndustryCatalog.Name(Recipe.Products.Single().Key);
        public string Owner => Blueprint.Owner;
        public string Location => Blueprint.Location;
        public string Icon => $"https://images.evetech.net/types/{Recipe.Products.Single().Key}/icon?size=64";
        public string StockColor => MaxRuns > 0 ? "#77DEC0" : "#EDBC73";
        public string StockLabel => MaxRuns > 0 ? "MATERIALS READY" : "INPUTS NEEDED";
        public IndustryCraftEconomics.Estimate Economics { get; init; } = new(0,0,null,null,null,null,null,null,"Refresh prices and configure job costs.");
        public string Efficiency => Blueprint.Efficiency;
        public long ItemId => Blueprint.ItemId;
        public string CoverageText => Coverage.ToString("P0");
        public string Readiness => MaxRuns > 0 ? "Materials available" : "More materials needed";
        public long MaxOutput => checked(MaxRuns * (long)Recipe.Products.Single().Value);
    }
    public sealed record Allocation(Guid NodeId, IndustryStockSource Source, long Quantity);
    public sealed record Bundle(List<IndustryProjectNode> Nodes, List<Allocation> Allocations);

    public static long Material(double amount, long runs, int me) =>
        checked((long)Math.Max(runs, decimal.Ceiling(decimal.Round((decimal)amount * runs * (1 - me / 100m), 2, MidpointRounding.AwayFromZero))));

    private static Dictionary<int, IndustryRecipe> Recipes(IReadOnlyList<IndustryRecipe>? recipes) =>
        (recipes ?? IndustryCatalog.Recipes).Where(r => r.Activity == "manufacturing" && r.Products.Count == 1 &&
            r.Materials.Count > 0 && r.Materials.All(m => m.Value > 0 && double.IsFinite(m.Value)) &&
            r.Products.Single().Value > 0 && double.IsFinite(r.Products.Single().Value) &&
            r.Products.Single().Value == Math.Truncate(r.Products.Single().Value))
        .GroupBy(r => r.Blueprint).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());

    private static long Remaining(IndustryBlueprintLibrary.Blueprint bp, IndustryWorkspace state)
    {
        decimal used = state.Projects.Where(p => !p.Archived).SelectMany(p => p.Nodes)
            .Where(n => n.BlueprintOwnerId == bp.OwnerId && n.BlueprintItemId == bp.ItemId)
            .Sum(n => (decimal)(n.PlannedRuns ?? 0));
        return checked((long)Math.Max(0, bp.Runs - used));
    }

    private static List<(IndustryStockSource Source, long Free)> FreeStock(IndustryWorkspace state,
        IReadOnlyList<IndustryStockSource> stock, DateTimeOffset now, ISet<(long OwnerId, long ItemId)>? allowedSources = null)
    {
        var blocked = state.Reservations.Where(r => !IndustryWorkspaceInventory.Review(r, state.Reservations, stock, now)
            .StartsWith("RESERVED -", StringComparison.Ordinal)).Select(r => r.TypeId).ToHashSet();
        return stock.GroupBy(s => (s.OwnerId, s.ItemId)).Where(g => g.Distinct().Count() == 1).Select(g => g.First())
            .Where(s => (allowedSources == null || allowedSources.Contains((s.OwnerId, s.ItemId))) && s.Verified && s.Quantity > 0 && IndustryWorkspaceInventory.Recent(s.SnapshotUtc, now) && !blocked.Contains(s.TypeId))
            .Select(s => (s, checked((long)Math.Max(0, s.Quantity - state.Reservations.Where(r => r.OwnerId == s.OwnerId && r.ItemId == s.ItemId).Sum(r => (decimal)r.Quantity)))))
            .Where(x => x.Item2 > 0).OrderBy(x => x.s.OwnerId).ThenBy(x => x.s.ItemId).ToList();
    }

    public static IReadOnlyList<Suggestion> Read(IEnumerable<IndustryBlueprintLibrary.Blueprint> blueprints,
        IndustryWorkspace state, IReadOnlyList<IndustryStockSource> stock, DateTimeOffset now,
        IReadOnlyList<IndustryRecipe>? recipes = null, ISet<(long OwnerId, long ItemId)>? allowedSources = null)
    {
        var byBlueprint = Recipes(recipes);
        var free = FreeStock(state, stock, now, allowedSources).GroupBy(s => s.Source.TypeId).ToDictionary(g => g.Key, g => g.Sum(s => (decimal)s.Free));
        var result = new List<Suggestion>();
        foreach (var bp in blueprints.Where(b => b.Available && b.Copy && b.ME is >= 0 and <= 10)
                     .DistinctBy(b => (b.OwnerId, b.ItemId)))
        {
            if (!byBlueprint.TryGetValue(bp.TypeId, out var recipe)) continue;
            long remaining = Remaining(bp, state);
            if (remaining == 0) continue;
            try
            {
                bool Fits(long runs) => recipe.Materials.All(m => Material(m.Value, runs, bp.ME) <= free.GetValueOrDefault(m.Key));
                long low = 0, high = Math.Min(remaining, checked((long)decimal.Floor(long.MaxValue / (decimal)recipe.Products.Single().Value)));
                // Overflow on an impossible large batch means it does not fit; smaller batches may.
                while (low < high)
                {
                    long mid = low + (high - low) / 2 + 1;
                    bool fits;
                    try { fits = Fits(mid); } catch (OverflowException) { fits = false; }
                    if (fits) low = mid; else high = mid - 1;
                }
                decimal coverage = recipe.Materials.Average(m => Math.Min(1, free.GetValueOrDefault(m.Key) / Material(m.Value, 1, bp.ME)));
                string inputs = string.Join("\n", recipe.Materials.OrderBy(m => IndustryCatalog.Name(m.Key)).Select(m =>
                    $"{IndustryCatalog.Name(m.Key)}: {Material(m.Value, 1, bp.ME):N0} needed / {free.GetValueOrDefault(m.Key):N0} free (1 run)"));
                result.Add(new(bp, recipe, remaining, low, coverage, inputs));
            }
            catch (OverflowException) { /* Invalid catalog quantities cannot create actionable suggestions. */ }
        }
        return result.OrderByDescending(s => s.MaxRuns > 0).ThenByDescending(s => s.Coverage)
            .ThenBy(s => s.Name).ThenBy(s => s.ItemId).ToArray();
    }

    public static Bundle Plan(IReadOnlyList<Selection> selections, IndustryWorkspace state,
        IReadOnlyList<IndustryStockSource> stock, DateTimeOffset now, IReadOnlyList<IndustryRecipe>? recipes = null, ISet<(long OwnerId, long ItemId)>? allowedSources = null)
    {
        if (selections.Count == 0 || selections.Select(s => (s.Blueprint.OwnerId, s.Blueprint.ItemId)).Distinct().Count() != selections.Count)
            throw new InvalidOperationException("Select one or more different blueprint copies.");
        var byBlueprint = Recipes(recipes);
        var free = FreeStock(state, stock, now, allowedSources);
        var nodes = new List<IndustryProjectNode>();
        var allocations = new List<Allocation>();
        foreach (var selection in selections)
        {
            var bp = selection.Blueprint;
            if (!bp.Available || !bp.Copy || bp.ME is < 0 or > 10 || selection.Runs <= 0 || selection.Runs > Remaining(bp, state) || !byBlueprint.TryGetValue(bp.TypeId, out var recipe))
                throw new InvalidOperationException("Blueprint unavailable, runs already planned, or manufacturing recipe missing. Reload suggestions.");
            long output = checked((long)((decimal)recipe.Products.Single().Value * selection.Runs));
            var root = new IndustryProjectNode { TypeId = recipe.Products.Single().Key, Name = IndustryCatalog.Name(recipe.Products.Single().Key),
                Quantity = output, PlannedOutput = output, PlannedRuns = selection.Runs, MaterialEfficiency = bp.ME,
                BlueprintTypeId = bp.TypeId, BlueprintItemId = bp.ItemId, BlueprintOwnerId = bp.OwnerId,
                BlueprintLocationId = bp.LocationId, BlueprintRunsAvailable = bp.Runs, ExecutorId = bp.OwnerId,
                CalculationSource = IndustryDependencyPlanner.CatalogHash,
                CalculationNote = $"{selection.Runs:N0} runs | ME {bp.ME}% | direct inputs from observed stock; facility/rig bonuses excluded" };
            nodes.Add(root);
            foreach (var material in recipe.Materials.OrderBy(m => m.Key))
            {
                long required = Material(material.Value, selection.Runs, bp.ME), missing = required;
                var input = new IndustryProjectNode { ParentId = root.Id, TypeId = material.Key, Name = IndustryCatalog.Name(material.Key),
                    Quantity = required, Strategy = "Use Stock", ExecutorId = bp.OwnerId,
                    CalculationSource = IndustryDependencyPlanner.CatalogHash, CalculationNote = "Direct manufacturing input; stock reserved, delivery unverified" };
                nodes.Add(input);
                for (int i = 0; i < free.Count && missing > 0; i++)
                {
                    var entry = free[i];
                    if (entry.Source.TypeId != material.Key || entry.Free == 0) continue;
                    long amount = Math.Min(missing, entry.Free);
                    allocations.Add(new(input.Id, entry.Source, amount));
                    free[i] = (entry.Source, entry.Free - amount); missing -= amount;
                    if (entry.Source.OwnerId != bp.OwnerId || entry.Source.LocationId != bp.LocationId)
                    { input.TransferNeeded = true; input.TransferNote = "Review hauling from reserved source to the manufacturing facility."; }
                }
                if (missing > 0) throw new InvalidOperationException($"Bundle needs {missing:N0} more {IndustryCatalog.Name(material.Key)}. Selected BPCs share the same free stock; reduce runs or remove a blueprint.");
            }
        }
        return new(nodes, allocations);
    }

    // A deterministic suggestion, not an optimiser: take compatible copies in the visible
    // order, subtracting shared inputs after each choice. Preview still rechecks the bundle.
    public static IReadOnlyList<Selection> SuggestBundle(IEnumerable<Suggestion> candidates, long runs,
        IndustryWorkspace state, IReadOnlyList<IndustryStockSource> stock, DateTimeOffset now,
        ISet<(long OwnerId, long ItemId)>? allowedSources = null)
    {
        if (runs <= 0) throw new InvalidOperationException("Enter a positive whole run count.");
        var free=FreeStock(state,stock,now,allowedSources).GroupBy(s=>s.Source.TypeId).ToDictionary(g=>g.Key,g=>g.Sum(s=>(decimal)s.Free));
        var result=new List<Selection>();
        foreach(var candidate in candidates.DistinctBy(s=>(s.Blueprint.OwnerId,s.Blueprint.ItemId)))
        {
            if(candidate.MaxRuns<runs)continue;
            var needs=candidate.Recipe.Materials.ToDictionary(m=>m.Key,m=>Material(m.Value,runs,candidate.Blueprint.ME));
            if(needs.Any(m=>m.Value>free.GetValueOrDefault(m.Key)))continue;
            foreach(var material in needs)free[material.Key]-=material.Value;
            result.Add(new(candidate.Blueprint,runs));
        }
        return result;
    }
}
