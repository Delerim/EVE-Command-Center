using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class PlanningSkill
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Rank { get; set; }
    public int Primary { get; set; }
    public int Secondary { get; set; }
    public Dictionary<int, int> Prerequisites { get; set; } = new();
}
public sealed class SkillPlanStep
{
    public int Id { get; set; }
    public int Level { get; set; }
    public string Name => SkillPlanning.Catalog[Id].Name;
    public string Target => SkillPlanning.Roman(Level);
    public long RemainingSp { get; set; }
    public double Rate { get; set; }
    public double Minutes => Rate > 0 ? RemainingSp / Rate : double.NaN;
    public string Duration => double.IsNaN(Minutes) ? "Attributes unavailable" : (Minutes < 60 ? $"{Math.Ceiling(Minutes):0} min" : Minutes < 1440 ? $"{Minutes / 60:0.1} hours" : $"{Minutes / 1440:0.1} days");
    public string Attributes => $"{SkillPlanning.Attribute(SkillPlanning.Catalog[Id].Primary)} / {SkillPlanning.Attribute(SkillPlanning.Catalog[Id].Secondary)}";
}
public static class SkillPlanning
{
    public static readonly Dictionary<int, PlanningSkill> Catalog = Load();
    private static Dictionary<int, PlanningSkill> Load()
    {
        var a = Assembly.GetExecutingAssembly();
        using var stream = a.GetManifestResourceStream(a.GetManifestResourceNames().Single(x => x.EndsWith("skill-planner.json")))!;
        return JsonSerializer.Deserialize<List<PlanningSkill>>(stream)!.ToDictionary(x => x.Id);
    }
    public static string Roman(int level) => new[] { "0", "I", "II", "III", "IV", "V" }[Math.Clamp(level, 0, 5)];
    public static string Attribute(int id) => id switch { 164 => "CHA", 165 => "INT", 166 => "MEM", 167 => "PER", 168 => "WIL", _ => "?" };
    public static long Sp(int rank, int level) => level <= 0 ? 0 : (long)Math.Ceiling(250 * rank * Math.Pow(2, 2.5 * (level - 1)));
    public static readonly Dictionary<string, string[]> Profiles = new()
    {
        ["Exhumer: mining & drones"] = new[] { "Mining", "Astrogeology", "Mining Upgrades", "Mining Barge", "Exhumers", "Drones", "Mining Drone Operation", "Mining Drone Specialization", "Drone Interfacing", "Drone Navigation", "Drone Durability", "Drone Avionics" },
        ["Exhumer: shields"] = new[] { "Shield Management", "Shield Operation", "Shield Upgrades", "Tactical Shield Manipulation", "EM Shield Compensation", "Thermal Shield Compensation", "Kinetic Shield Compensation", "Explosive Shield Compensation" },
        ["Exhumer: armor & hull"] = new[] { "Mechanics", "Hull Upgrades", "Repair Systems", "EM Armor Compensation", "Thermal Armor Compensation", "Kinetic Armor Compensation", "Explosive Armor Compensation" },
        ["Exhumer: agility & fitting"] = new[] { "Navigation", "Evasive Maneuvering", "Spaceship Command", "Warp Drive Operation", "CPU Management", "Power Grid Management", "Capacitor Management", "Capacitor Systems Operation" },
    };
    public static Dictionary<int, int> Profile(string name)
    {
        var names = name == "Max Exhumer Miner" ? Profiles.Values.SelectMany(x => x) : Profiles[name];
        return names.Select(n => Catalog.Values.Single(x => x.Name == n)).ToDictionary(x => x.Id, _ => 5);
    }
    public static List<SkillPlanStep> Build(Dictionary<int, int> targets, EvePilotDashboard data, bool attributeOrder)
    {
        var trained = data.TrainedSkills.ToDictionary(x => x.SkillId);
        var result = new List<SkillPlanStep>();
        var visiting = new HashSet<int>();
        void Add(int id, int level)
        {
            if (!Catalog.TryGetValue(id, out var skill)) throw new InvalidOperationException($"Unknown skill {id}; refresh the catalog.");
            if (!visiting.Add(id)) throw new InvalidOperationException("Circular skill prerequisite.");
            trained.TryGetValue(id, out var current);
            if (level > (current?.TrainedSkillLevel ?? 0))
            {
                foreach (var p in skill.Prerequisites) Add(p.Key, p.Value);
                for (int l = (current?.TrainedSkillLevel ?? 0) + 1; l <= Math.Clamp(level, 1, 5); l++)
                    if (!result.Any(x => x.Id == id && x.Level == l)) result.Add(new SkillPlanStep { Id = id, Level = l,
                        RemainingSp = Math.Max(0, Sp(skill.Rank, l) - Math.Max(Sp(skill.Rank, l - 1), current?.SkillpointsInSkill ?? 0)),
                        Rate = data.TrainingProfile.GetTotal(skill.Primary) > 0 && data.TrainingProfile.GetTotal(skill.Secondary) > 0 ? data.TrainingProfile.GetTotal(skill.Primary) + data.TrainingProfile.GetTotal(skill.Secondary) / 2.0 : 0 });
            }
            visiting.Remove(id);
        }
        foreach (var t in targets) Add(t.Key, t.Value);
        if (!attributeOrder) return result;
        var sorted = new List<SkillPlanStep>();
        var levels = trained.ToDictionary(x => x.Key, x => x.Value.TrainedSkillLevel);
        while (result.Count > 0)
        {
            var next = result.Where(x => levels.GetValueOrDefault(x.Id) >= x.Level - 1 && Catalog[x.Id].Prerequisites.All(p => levels.GetValueOrDefault(p.Key) >= p.Value))
                .OrderByDescending(x => x.Rate).ThenBy(x => x.Minutes).FirstOrDefault() ?? throw new InvalidOperationException("Cannot order prerequisites.");
            sorted.Add(next); result.Remove(next); levels[next.Id] = next.Level;
        }
        return sorted;
    }
}
