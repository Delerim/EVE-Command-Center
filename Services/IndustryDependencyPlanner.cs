using EveCommandCenter.Models;
using System.Security.Cryptography;

namespace EveCommandCenter.Services;

public static class IndustryDependencyPlanner
{
    public static readonly string CatalogHash = HashCatalog();
    private static string HashCatalog()
    {
        using var stream = typeof(IndustryCatalog).Assembly.GetManifestResourceStream("EveCommandCenter.Resources.industry-catalog.json")!;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static List<IndustryProjectNode> Build(int productOrBlueprint, long quantity, IndustryBlueprintLibrary.Blueprint? blueprint = null,
        IReadOnlyList<IndustryRecipe>? recipes = null)
    {
        recipes ??= IndustryCatalog.Recipes;
        var manufacturing = recipes.Where(r => r.Activity == "manufacturing" && r.Products.Count == 1).ToArray();
        var byProduct = manufacturing.SelectMany(r => r.Products.Keys.Select(t => (t, r))).ToLookup(x => x.t, x => x.r);
        var rootRecipe = manufacturing.SingleOrDefault(r => r.Blueprint == (blueprint?.TypeId ?? productOrBlueprint));
        int product = rootRecipe?.Products.Single().Key ?? productOrBlueprint;
        rootRecipe ??= byProduct[product].Count() == 1 ? byProduct[product].Single() : null;
        if (rootRecipe == null) throw new InvalidOperationException("No unambiguous single-product manufacturing recipe. Select a manufacturing blueprint.");
        if (quantity <= 0) throw new InvalidOperationException("Enter a positive output quantity.");
        var nodes = new List<IndustryProjectNode>();
        var ancestry = new HashSet<int>();
        void Expand(int type, long required, Guid? parent, IndustryRecipe? recipe, int me, int depth)
        {
            if (depth > 32 || nodes.Count >= 5000 || !ancestry.Add(type)) throw new InvalidOperationException("Recipe tree is cyclic or exceeds the safe planning limit.");
            var node = new IndustryProjectNode { ParentId = parent, TypeId = type, Name = IndustryCatalog.Name(type), Quantity = required,
                Strategy = recipe == null ? "Buy" : "Make", BlueprintTypeId = recipe?.Blueprint, CalculationSource = CatalogHash };
            nodes.Add(node);
            if (recipe != null)
            {
                decimal output = (decimal)recipe.Products.Single().Value;
                if (output <= 0 || output != decimal.Truncate(output)) throw new InvalidOperationException("Invalid recipe output quantity.");
                long runs = checked((long)decimal.Ceiling(required / output));
                node.PlannedRuns = runs; node.MaterialEfficiency = me;
                node.PlannedOutput = checked((long)(runs * output));
                node.CalculationNote = $"{runs:N0} runs | ME {me}% | output {node.PlannedOutput:N0} | facility/rig bonuses excluded";
                if (parent == null && blueprint != null)
                {
                    if (!blueprint.Available || blueprint.ME is < 0 or > 10 || (blueprint.Copy && runs > blueprint.Runs))
                        throw new InvalidOperationException("Selected blueprint is unavailable, unverified or has insufficient remaining runs.");
                    node.BlueprintItemId = blueprint.ItemId; node.BlueprintOwnerId = blueprint.OwnerId;
                    node.BlueprintLocationId = blueprint.LocationId; node.ExecutorId = blueprint.OwnerId;
                    node.BlueprintRunsAvailable = blueprint.Copy ? blueprint.Runs : null;
                }
                else node.CalculationNote += " | blueprint instance unassigned";
                foreach (var input in recipe.Materials.OrderBy(m => m.Key))
                {
                    if (input.Value <= 0 || !double.IsFinite(input.Value)) throw new InvalidOperationException("Invalid recipe material quantity.");
                    long amount = checked((long)Math.Max(runs, decimal.Ceiling(decimal.Round((decimal)input.Value * runs * (1 - me / 100m), 2, MidpointRounding.AwayFromZero))));
                    var candidates = byProduct[input.Key].ToArray();
                    Expand(input.Key, amount, node.Id, candidates.Length == 1 ? candidates[0] : null, 0, depth + 1);
                }
            }
            else node.CalculationNote = "Purchase input; no unique manufacturing recipe expanded (reactions/invention excluded)";
            ancestry.Remove(type);
        }
        Expand(product, quantity, null, rootRecipe, blueprint?.ME ?? 0, 0);
        return nodes;
    }
}
