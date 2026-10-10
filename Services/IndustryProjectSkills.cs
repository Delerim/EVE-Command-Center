using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public static class IndustryProjectSkills
{
    public sealed record Requirement(int TypeId, int Level, string Products)
    {
        public string Name => IndustryCatalog.Name(TypeId);
        public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
    }
    public sealed record Plan(IReadOnlyList<Requirement> Requirements, int MissingRecipes, int Builds);
    public static Plan Read(IndustryProject project)
    {
        var recipes = new List<(IndustryRecipe Recipe, string Product)>();
        int missing = 0, builds = 0;
        var children = project.Nodes.ToLookup(n => n.ParentId);
        void Visit(IndustryProjectNode node)
        {
            if (node.Strategy != "Make") return;
            builds++;
            var recipe = IndustryCatalog.Recipes.FirstOrDefault(r => r.Blueprint == node.BlueprintTypeId && r.Activity == "manufacturing");
            if (recipe == null) missing++;
            else recipes.Add((recipe, node.Name));
            foreach (var child in children[node.Id]) Visit(child);
        }
        foreach (var root in children[null]) Visit(root);
        return new(recipes.SelectMany(r => r.Recipe.Skills.Select(s => (s.Key, s.Value, r.Product)))
            .GroupBy(s => s.Key).Select(g => new Requirement(g.Key, g.Max(s => s.Value), string.Join(", ", g.Select(s => s.Product).Distinct())))
            .OrderBy(r => r.Name).ToArray(), missing, builds);
    }
    public static bool Current(IndustryPilot? pilot, EvePilotProfile? auth, DateTimeOffset now) =>
        pilot != null && string.IsNullOrWhiteSpace(pilot.Error) && IndustryWorkspaceInventory.Recent(pilot.Updated, now)
        && auth?.Scopes.Contains("esi-skills.read_skills.v1") == true;
}
