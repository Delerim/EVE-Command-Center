using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class SkillPlannerWindow : Window
{
    private readonly EvePilotDashboard _data;
    private Dictionary<int, int> _targets = new();
    private List<SkillPlanStep> _steps = new();
    private sealed class SavedPlan { public Dictionary<int, int> Targets { get; set; } = new(); public bool AttributeOrder { get; set; } }
    private Dictionary<string, SavedPlan> _saved = new();
    private readonly string _file;
    private bool _ordered;
    public SkillPlannerWindow(EvePilotDashboard data)
    {
        _data = data;
        InitializeComponent();
        Heading.Text = $"SKILL PLANNER | {data.Summary.CharacterName}";
        AttributesText.Text = string.Join("   |   ", data.TrainingProfile.Attributes.Select(x => $"{x.Name}: {x.Total}")) + $"   |   Bonus remaps: {data.TrainingProfile.BonusRemaps}";
        _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "PilotData", $"skill-plans-{data.Summary.CharacterId}.json");
        try { if (File.Exists(_file)) _saved = JsonSerializer.Deserialize<Dictionary<string, SavedPlan>>(File.ReadAllText(_file)) ?? new(); }
        catch (Exception ex) { Status.Text = "Could not read saved plans: " + ex.Message; }
        SavedPlans.ItemsSource = _saved.Keys.ToList();
        Skill.ItemsSource = SkillPlanning.Catalog.Values.OrderBy(x => x.Name).ToList();
        Profiles.ItemsSource = new[] { "Max Exhumer Miner" }.Concat(SkillPlanning.Profiles.Keys).ToList();
        Profiles.SelectedIndex = 0;
        Refresh();
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not string profile) return;
        var trained = _data.TrainedSkills.ToDictionary(x => x.SkillId);
        ProfileGrid.ItemsSource = SkillPlanning.Profile(profile).Select(x => {
            trained.TryGetValue(x.Key, out var s);
            return new { Name = SkillPlanning.Catalog[x.Key].Name, Trained = SkillPlanning.Roman(s?.TrainedSkillLevel ?? 0), Status = s?.TrainedSkillLevel >= x.Value ? (s.ActiveSkillLevel < x.Value ? "Trained / inactive" : "Complete") : "Missing", Color = s?.TrainedSkillLevel >= x.Value ? "#58D3B4" : "#E8BC62" };
        }).OrderBy(x => x.Status == "Complete").ThenBy(x => x.Name).ToList();
    }
    private void Refresh()
    {
        try
        {
            _steps = SkillPlanning.Build(_targets, _data, _ordered);
            Steps.ItemsSource = _steps;
            Summary.Text = $"{_targets.Count} targets | {_steps.Count} missing levels | {_steps.Sum(x => x.RemainingSp):N0} SP | " + (_steps.Any(x => x.Rate <= 0) ? "Training time unavailable" : $"{_steps.Sum(x => x.Minutes) / 1440:0.0} days estimated");
            Guidance.Text = "Training by attribute pair:\n" + string.Join("\n", _steps.GroupBy(x => x.Attributes).OrderByDescending(x => x.Sum(s => s.RemainingSp)).Select(x => $"{x.Key}: {x.Sum(s => s.RemainingSp):N0} SP, {x.First().Rate:0.0} SP/min")) + "\n\nA remap changes training speed, not skill requirements. Use the largest remaining SP groups to guide a future remap. Implant bonuses are already included in the current attributes above. Prerequisites are added automatically; removing a target keeps anything another target still requires.";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Profile_Click(object sender, RoutedEventArgs e) { if (Profiles.SelectedItem is string p) { foreach (var x in SkillPlanning.Profile(p)) _targets[x.Key] = Math.Max(_targets.GetValueOrDefault(x.Key), x.Value); Refresh(); } }
    private void Add_Click(object sender, RoutedEventArgs e) { if (Skill.SelectedItem is PlanningSkill s) { _targets[s.Id] = Math.Max(_targets.GetValueOrDefault(s.Id), Level.SelectedIndex + 1); Refresh(); } else Status.Text = "Choose a skill from the list."; }
    private void Optimize_Click(object sender, RoutedEventArgs e) { _ordered = true; Refresh(); Status.Text = "Plan reordered by current training speed, with prerequisites first. Total training time is unchanged."; }
    private void Clear_Click(object sender, RoutedEventArgs e) { _targets.Clear(); Refresh(); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Steps.SelectedItem is SkillPlanStep s) { if (!_targets.Remove(s.Id)) Status.Text = "This skill is a prerequisite. Remove the target that requires it first."; Refresh(); } }
    private void Queue_Click(object sender, RoutedEventArgs e) { _targets = _data.SkillQueue.OrderBy(x => x.Position).GroupBy(x => x.SkillId).ToDictionary(x => x.Key, x => x.Max(y => y.FinishedLevel)); _ordered = false; Refresh(); Status.Text = "Live queue targets copied into a local plan. No changes were sent to EVE."; }
    private void Copy_Click(object sender, RoutedEventArgs e) { try { if (_steps.Count == 0) { Status.Text = "The plan has no missing levels to copy."; return; } System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _steps.Select(x => $"{x.Name} {x.Target}"))); Status.Text = "Copied. In EVE, import the skill list into a skill plan and review it before applying to your queue."; } catch (Exception ex) { Status.Text = "Clipboard unavailable: " + ex.Message; } }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PlanName.Text)) { Status.Text = "Enter a plan name."; return; }
        try { _saved[PlanName.Text.Trim()] = new() { Targets = new(_targets), AttributeOrder = _ordered }; Directory.CreateDirectory(Path.GetDirectoryName(_file)!); File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_saved)); File.Move(_file + ".tmp", _file, true); SavedPlans.ItemsSource = _saved.Keys.ToList(); Status.Text = "Plan saved for this pilot."; } catch (Exception ex) { Status.Text = "Could not save plan: " + ex.Message; }
    }
    private void Load_Click(object sender, RoutedEventArgs e) { if (SavedPlans.SelectedItem is string name && _saved.TryGetValue(name, out var plan)) { _targets = new(plan.Targets); _ordered = plan.AttributeOrder; PlanName.Text = name; Refresh(); } }
}
