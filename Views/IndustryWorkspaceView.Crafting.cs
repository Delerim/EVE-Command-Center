using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class IndustryWorkspaceView
{
    private sealed record CraftSourceChoice(long? OwnerId, long? LocationId, string Name)
    { public override string ToString() => Name; }
    private bool _craftLoading;
    private bool _selectingCraftBundle;

    private async void OpenCraft_Click(object sender, RoutedEventArgs e)
    {
        ProjectGallery.Visibility = ProjectWorkspace.Visibility = CreationPage.Visibility = SettingsPage.Visibility = Visibility.Collapsed;
        CraftPage.Visibility = Visibility.Visible;
        RefreshAll();
        if (_autoCraftPricing) await LoadCraftPricesAsync(false);
    }

    private IReadOnlyList<IndustryStockSource> CraftStock()
    {
        var source = CraftSource.SelectedItem as CraftSourceChoice;
        return _stock.Where(s => source?.OwnerId == null || s.OwnerId == source.OwnerId && s.LocationId == source.LocationId).ToArray();
    }

    private void RefreshCraftSuggestions()
    {
        if (!_initialized || CraftPage.Visibility != Visibility.Visible) return;
        var owner = Choice(CraftOwner);
        var source = CraftSource.SelectedItem as CraftSourceChoice;
        _craftLoading = true;
        CraftOwner.ItemsSource = new[] { new PilotOption(null, "All linked blueprint owners") }.Concat(_linked.Select(p => new PilotOption(p.CharacterId, p.CharacterName))).ToArray();
        Select(CraftOwner, owner);
        var sources = new[] { new CraftSourceChoice(null, null, "All observed cans / hangars (hauling may be needed)") }
            .Concat(_stock.GroupBy(s => (s.OwnerId, s.LocationId)).Select(g => new CraftSourceChoice(g.Key.OwnerId, g.Key.LocationId,
                g.First().Owner + " | " + DisplayPath(g.Key.OwnerId, g.Key.LocationId)))).ToArray();
        CraftSource.ItemsSource = sources;
        CraftSource.SelectedItem = sources.FirstOrDefault(s => s.OwnerId == source?.OwnerId && s.LocationId == source?.LocationId) ?? sources[0];
        _craftLoading = false;
        FilterCraftSuggestions();
    }

    private void FilterCraftSuggestions()
    {
        if (!_initialized || _craftLoading) return;
        var rows = IndustryCraftSuggestions.Read(_blueprints.Where(b => Choice(CraftOwner) == null || b.OwnerId == Choice(CraftOwner)),
            _state, _stock, DateTimeOffset.UtcNow, allowedSources: AllowedCraftSources());
        string query = CraftSearch.Text.Trim();
        bool ready = CraftTabs.SelectedIndex <= 0;
        _craftMatching = rows.Where(r => (!ready || r.MaxRuns > 0) && (query.Length == 0 || (r.Name + " " + r.Blueprint.Name).Contains(query, StringComparison.OrdinalIgnoreCase))).Select(CostCraftRow).ToArray();
        var visible = _craftMatching.Where(r => CraftProfitOnly.IsChecked != true || r.Economics.Profit > 0).ToArray();
        SetCraftRows(visible);
        CraftReadyCount.Text = rows.Count(r => r.MaxRuns > 0).ToString("N0") + " copies";
        int profitableCopies = _craftMatching.Count(r => r.Economics.Profit > 0);
        CraftProfitableCount.Text = profitableCopies.ToString("N0") + (profitableCopies == 1 ? " copy" : " copies");
        CraftPricedCount.Text = $"{_craftMatching.Count(r => r.Economics.Profit.HasValue):N0} / {_craftMatching.Length:N0} copies";
        CraftSummary.Text = $"{visible.Length:N0} blueprint copies | {rows.Count(r => r.MaxRuns > 0):N0} material-covered alternatives. Existing reservations and planned BPC runs excluded. " +
            (visible.Length == 0 ? "Refresh Industry snapshots or change the source/filter. Unverified, stale and in-use copies are excluded." : "Suggested projects includes copies needing more inputs; select one to see its material gaps.");
        UpdateCraftPreview();
    }

    private List<IndustryCraftSuggestions.Selection> CraftSelection() => CraftGrid.SelectedItems.Cast<IndustryCraftSuggestions.Suggestion>()
        .Select(s => new IndustryCraftSuggestions.Selection(s.Blueprint, Quantity(CraftRuns.Text))).ToList();

    private void UpdateCraftPreview()
    {
        if (!_initialized || CraftPreview == null || _selectingCraftBundle) return;
        CreateCraftButton.IsEnabled = false;
        var selected = CraftGrid.SelectedItems.Cast<IndustryCraftSuggestions.Suggestion>().ToArray();
        ShowCraftTotals(selected);
        var economics = selected.Length > 0 ? IndustryCraftEconomics.Combine(selected.Select(s => CostCraftRow(s).Economics)) : null;
        CraftInputs.Text = (economics?.Breakdown ?? "") + (selected.Length == 1 ? "\n\n" + selected[0].Inputs : "");
        if (selected.Length == 0) { CraftPreview.Text = "Select BPCs to preview a combined project."; return; }
        try
        {
            var plan = IndustryCraftSuggestions.Plan(CraftSelection(), _state, _stock, DateTimeOffset.UtcNow, allowedSources: AllowedCraftSources());
            CraftPreview.Text = $"{selected.Length:N0} BPCs | {plan.Nodes.Where(n => n.ParentId == null).Sum(n => (decimal)n.Quantity):N0} output units | All combined inputs covered. Creating reserves the exact source stacks.";
            CraftInputs.Text = (economics?.Breakdown ?? "") + "\n\nReserved source preview:\n" + string.Join("\n", plan.Allocations.GroupBy(a => (a.Source.TypeId, a.Source.OwnerId, a.Source.LocationId))
                .Select(g => $"{CraftSourceCost(g.Key.TypeId, g.Sum(a => (decimal)a.Quantity))} from {g.First().Source.Owner} | {DisplayPath(g.Key.OwnerId, g.Key.LocationId)}"));
            CreateCraftButton.IsEnabled = _store.Error.Length == 0;
        }
        catch (Exception ex) { CraftPreview.Text = ex.Message; }
    }

    private ISet<(long OwnerId, long ItemId)> AllowedCraftSources() =>
        CraftStock().Select(s => (s.OwnerId, s.ItemId)).ToHashSet();
    private void SuggestCraftBundle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows=CraftGrid.Items.Cast<IndustryCraftSuggestions.Suggestion>().ToArray();
            var candidates=rows.Any(s=>s.Economics.Profit.HasValue)?rows.Where(s=>s.Economics.Profit>0).OrderByDescending(s=>s.Economics.Profit):rows.AsEnumerable();
            var selected=IndustryCraftSuggestions.SuggestBundle(candidates,Quantity(CraftRuns.Text),
                _state,_stock,DateTimeOffset.UtcNow,AllowedCraftSources()).Select(s=>(s.Blueprint.OwnerId,s.Blueprint.ItemId)).ToHashSet();
            _selectingCraftBundle=true;
            try
            {
                CraftGrid.SelectedItems.Clear();
                foreach(var row in CraftGrid.Items.Cast<IndustryCraftSuggestions.Suggestion>().Where(s=>selected.Contains((s.Blueprint.OwnerId,s.Blueprint.ItemId))))
                    CraftGrid.SelectedItems.Add(row);
            }
            finally { _selectingCraftBundle=false; }
            UpdateCraftPreview();
            if(selected.Count==0)CraftPreview.Text=rows.Any(s=>s.Economics.Profit.HasValue)
                ? "No profitable compatible copies for this run count and source. Review prices and job-cost setup or choose another material source."
                : "No compatible BPCs for this run count and material source. Try fewer runs or refresh Industry.";
        }
        catch(Exception ex){CraftPreview.Text=ex.Message;}
    }
    private void CraftFilter_Changed(object sender, SelectionChangedEventArgs e) { if (!_craftLoading) FilterCraftSuggestions(); }
    private void CraftSearch_Changed(object sender, TextChangedEventArgs e) => FilterCraftSuggestions();
    private void CraftSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateCraftPreview();
    private void CraftRuns_Changed(object sender, TextChangedEventArgs e) { if (_initialized) FilterCraftSuggestions(); }
    private void CreateCraft_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        var selections = CraftSelection();
        var now = DateTimeOffset.UtcNow;
        var blueprints = IndustryBlueprintLibrary.Read(_inventory(), _linked.Where(p => p.Scopes.Contains("esi-characters.read_blueprints.v1")).Select(p => p.CharacterId).ToHashSet(), now);
        _stock = ReadStock();
        _projectId = _store.CreateCraftBundle(CraftName.Text, selections, blueprints, _stock, LinkedIds, now, allowedSources: AllowedCraftSources());
        _nodeId = null; OpenProject();
    }, "Craft project saved. Combined materials reserved; review source locations, skills and delivery before starting jobs in EVE.");
}
