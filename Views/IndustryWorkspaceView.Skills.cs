using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;

public partial class IndustryWorkspaceView
{
    private long? _skillPilot;
    private Guid? _skillsProject;
    private IndustryProjectSkills.Plan? _skillPlan;
    private bool InventoryComplete => _linked.Count > 0 && _linked.All(auth =>
        auth.Scopes.Contains("esi-assets.read_assets.v1") && _inventory().Any(p => p.Id == auth.CharacterId &&
            string.IsNullOrWhiteSpace(p.Error) && IndustryWorkspaceInventory.Recent(p.Updated, DateTimeOffset.UtcNow)));
    private sealed record SkillPilot(long Id, string Name, string Status, string Color)
    { public string Portrait => $"https://images.evetech.net/characters/{Id}/portrait?size=64"; }
    private sealed record SkillRow(string Name, string Icon, int Required, string Trained, string Training, string Products, string Color);
    private void RefreshProjectSkills(IndustryProject project)
    {
        _skillPlan = IndustryProjectSkills.Read(project);
        if (_skillsProject != project.Id) { _skillPilot = project.LeadId; _skillsProject = project.Id; }
        SkillsScope.Text = $"Blueprint owner: {Pilot(project.LeadId)}. Compare skills across {_skillPlan.Builds} manufacturing steps, including the main blueprint. Selecting a pilot does not change assignments.";
        var pilots = _inventory().ToArray();
        SkillPilots.ItemsSource = _linked.Select(auth =>
        {
            var p = pilots.FirstOrDefault(p => p.Id == auth.CharacterId);
            bool known = IndustryProjectSkills.Current(p, auth, DateTimeOffset.UtcNow);
            int missing = _skillPlan.Requirements.Count(r => (p?.Skills.GetValueOrDefault(r.TypeId) ?? 0) < r.Level);
            string status = !known ? "Refresh skills" : missing > 0 ? $"{missing} to train" : _skillPlan.MissingRecipes > 0 ? "Recipe unavailable" : _skillPlan.Builds == 0 ? "No builds" : "All trained";
            return new SkillPilot(auth.CharacterId, auth.CharacterName, status, !known || _skillPlan.MissingRecipes > 0 ? "#9DB6BD" : missing > 0 ? "#E3BC70" : "#76D7CB");
        }).ToArray();
        SkillPilots.SelectedItem = SkillPilots.Items.Cast<SkillPilot>().FirstOrDefault(p => p.Id == _skillPilot) ?? SkillPilots.Items.Cast<SkillPilot>().FirstOrDefault();
        RefreshSkillRows();
    }
    private void SkillPilot_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SkillPilots.SelectedItem is SkillPilot selected) _skillPilot = selected.Id;
        RefreshSkillRows();
    }
    private void RefreshSkillRows()
    {
        if (_skillPlan == null || SkillPilots.SelectedItem is not SkillPilot selected) return;
        var pilot = _inventory().FirstOrDefault(p => p.Id == selected.Id);
        bool known = IndustryProjectSkills.Current(pilot, _linked.FirstOrDefault(p => p.CharacterId == selected.Id), DateTimeOffset.UtcNow);
        SkillsStatus.Text = selected.Name + " | " + selected.Status + (_skillPlan.MissingRecipes > 0 ? $" | {_skillPlan.MissingRecipes} recipes need verification" : "") + ". Bought components are excluded. Facility eligibility is checked separately.";
        ProjectSkills.ItemsSource = _skillPlan.Requirements.Select(r =>
        {
            int level = pilot?.Skills.GetValueOrDefault(r.TypeId) ?? 0;
            return new SkillRow(ProductName(r.TypeId, r.Name), r.Icon, r.Level, known ? level.ToString() : "Unknown",
                !known ? "Refresh skill data" : level >= r.Level ? "Trained" : $"Train {level} to {r.Level}", r.Products,
                !known ? "#9DB6BD" : level >= r.Level ? "#76D7CB" : "#E3BC70");
        }).OrderBy(r => r.Training == "Trained").ThenBy(r => r.Name).ToArray();
    }
}
