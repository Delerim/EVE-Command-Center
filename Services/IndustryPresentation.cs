using System.Text.RegularExpressions;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public static class IndustryPresentation
{
    public sealed record ReadinessState(string Summary, string Detail);
    public static ReadinessState Readiness(IndustryProjectNode node, IReadOnlyList<IndustryPilot> pilots,
        IReadOnlyList<EvePilotProfile> linked, IReadOnlyList<IndustryBlueprintLibrary.Blueprint> blueprints, DateTimeOffset now, long? projectOwnerId = null)
    {
        if (node.Strategy != "Make") return new("", "Purchased or supplied from stock; manufacturing checks do not apply.");
        var pilot = pilots.FirstOrDefault(p => p.Id == (node.ExecutorId ?? projectOwnerId));
        var auth = linked.FirstOrDefault(p => p.CharacterId == (node.ExecutorId ?? projectOwnerId));
        var owner = pilots.FirstOrDefault(p => p.Id == projectOwnerId);
        var ownerAuth = linked.FirstOrDefault(p => p.CharacterId == projectOwnerId);
        bool fresh = pilot != null && string.IsNullOrWhiteSpace(pilot.Error) && IndustryWorkspaceInventory.Recent(pilot.Updated, now);
        var owned = blueprints.Where(b => b.TypeId == node.BlueprintTypeId && b.OwnerId == projectOwnerId).ToArray();
        string bp = "? Blueprint", detail = "Choose a project owner and refresh their blueprint inventory.";
        if (owner != null && string.IsNullOrWhiteSpace(owner.Error) && IndustryWorkspaceInventory.Recent(owner.Updated, now) && ownerAuth?.Scopes.Contains("esi-characters.read_blueprints.v1") == true)
        {
            var usable = owned.FirstOrDefault(b => b.Available && (!b.Copy || b.Runs >= node.PlannedRuns));
            bp = usable != null ? "\u2713 " + (usable.Copy ? "BPC" : "BPO") : owned.Length > 0 ? "! Blueprint" : "\u00d7 Blueprint";
            detail = usable != null ? $"{owner!.Name}: {(usable.Copy ? "copy" : "original")} available for the planned runs. {usable.Location}." : owned.Length > 0 ? "Owned blueprint is busy, lacks runs, or needs review." : $"No matching blueprint in {owner!.Name}'s current inventory.";
        }
        var recipe = IndustryCatalog.Recipes.FirstOrDefault(r => r.Blueprint == node.BlueprintTypeId && r.Activity == "manufacturing");
        string skills = "? Skills";
        if (recipe != null && fresh && auth?.Scopes.Contains("esi-skills.read_skills.v1") == true)
        {
            var missing = recipe.Skills.Where(s => pilot!.Skills.GetValueOrDefault(s.Key) < s.Value).ToArray();
            skills = missing.Length == 0 ? "\u2713 Skills" : "\u00d7 Skills";
            detail += $" Skills checked for {pilot!.Name}.";
            detail += missing.Length == 0 ? " Required recipe skills trained." : " Missing: " + string.Join(", ", missing.Select(s => IndustryDisplayNames.Type(pilot, s.Key) + " level " + s.Value)) + ".";
        }
        else detail += " Skills unverified: assign a pilot and refresh with skill access.";
        return new(bp + "   " + skills, detail + " Facility eligibility and hauling are separate checks.");
    }
    // The original audit record remains intact; only its user-facing projection is simplified.
    public static string History(string raw)
    {
        if (raw.StartsWith("Deleted project snapshot:")) return "Project deleted; reservations released.";
        if (raw.StartsWith("Edited project:")) return raw.Split(';')[0] + "; settings updated.";
        if (raw.StartsWith("Edited component"))
        {
            var quantity = Regex.Match(raw, @"planned quantity (\d+)").Groups[1].Value;
            var strategy = Regex.Match(raw, @"strategy (.+)\.").Groups[1].Value;
            return $"Component updated: {quantity} required; {strategy}.";
        }
        if (raw.StartsWith("Added planned component type"))
        {
            var match = Regex.Match(raw, @"type (\d+), quantity (\d+)");
            return match.Success ? $"Added {IndustryCatalog.Name(int.Parse(match.Groups[1].Value))} x {match.Groups[2].Value}." : "Component added.";
        }
        if (raw.StartsWith("Assigned local manufacturing plan")) return "Manufacturing job assigned to a pilot.";
        if (raw.StartsWith("Selected blueprint")) return "Blueprint selected; component inputs recalculated.";
        if (raw.StartsWith("Reserved ")) return "Stock reserved for this build; delivery not confirmed.";
        if (raw.StartsWith("Manually matched EVE job")) return "Existing EVE job linked to a component; stock and delivery unchanged.";
        var text = Regex.Replace(raw, @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", "", RegexOptions.CultureInvariant);
        int json = text.IndexOf('{'); if (json >= 0) text = text[..json].TrimEnd(' ', ':');
        text = Regex.Replace(text, @"\b(?:item|instance|facility|location|job|owner|type)\s+(?:ID\s*)?\d+\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(text, @"\s+", " ").Replace("component ;", "component;").Trim();
    }
}
