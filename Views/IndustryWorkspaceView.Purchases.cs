using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class IndustryWorkspaceView
{
    private ProjectPurchaseRow[] _projectPurchaseRows = Array.Empty<ProjectPurchaseRow>();
    private int? _purchaseTypeFilter;
    private Guid? _purchaseProject;
    private void WorkspaceTab_Changed(object sender, SelectionChangedEventArgs e)
    { if (_initialized && e.Source == WorkspaceTabs && WorkspaceTabs.SelectedItem == ProjectPurchasesTab) RefreshProjectPurchases(); }
    private void ReloadProjectPurchases_Click(object sender, RoutedEventArgs e) => RefreshProjectPurchases();
    private void RefreshProjectPurchases()
    {
        if (!_initialized || _projectId is not {} id) return;
        try
        {
            if (_purchaseProject != id) { _purchaseTypeFilter = null; _purchaseProject = id; }
            var purchases = _store.ProjectPurchases(id);
            var costs = ProjectAcquisitionCosts.Calculate(purchases);
            _projectPurchaseRows = purchases.OrderByDescending(p => p.Purchase.Date).Select(p => new ProjectPurchaseRow(p.Purchase, p.Allocation, Pilot(p.OwnerId), p.Cost)).ToArray();
            ProjectItemCosts.ItemsSource = costs;
            ProjectPurchaseSummary.Text = $"{costs.Sum(p => p.Cost):N2} ISK";
            ProjectPurchaseItems.Text = $"{costs.Count:N0}";
            ProjectPurchaseCount.Text = $"{purchases.Select(p => (p.OwnerId, p.Purchase.Id)).Distinct().Count():N0}";
            ProjectPurchaseEmpty.Visibility = purchases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ProjectCostViews.Visibility = purchases.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            FilterProjectPurchases();
        }
        catch (Exception ex)
        {
            _projectPurchaseRows = Array.Empty<ProjectPurchaseRow>(); ProjectPurchases.ItemsSource = null; ProjectItemCosts.ItemsSource = null;
            ProjectPurchaseSummary.Text = "Unavailable"; ProjectPurchaseItems.Text = ProjectPurchaseCount.Text = "-";
            ProjectPurchaseEmpty.Visibility = Visibility.Visible; ProjectCostViews.Visibility = Visibility.Collapsed;
            ProjectPurchaseEmpty.Text = "Could not load purchases: " + ex.Message;
        }
    }
    private void ProjectCost_Selected(object sender, SelectionChangedEventArgs e)
    { if (InspectItemPurchases != null) InspectItemPurchases.IsEnabled = ProjectItemCosts.SelectedItem is ProjectAcquisitionCosts.ItemCost; }
    private void InspectItemPurchases_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectItemCosts.SelectedItem is not ProjectAcquisitionCosts.ItemCost item) return;
        _purchaseTypeFilter = item.TypeId; FilterProjectPurchases(); ProjectCostViews.SelectedIndex = 1;
    }
    private void ClearPurchaseFilter_Click(object sender, RoutedEventArgs e) { _purchaseTypeFilter = null; FilterProjectPurchases(); }
    private void FilterProjectPurchases()
    {
        if (_purchaseTypeFilter is {} type && !_projectPurchaseRows.Any(p => p.Purchase.TypeId == type)) _purchaseTypeFilter = null;
        var rows = _projectPurchaseRows.Where(p => _purchaseTypeFilter == null || p.Purchase.TypeId == _purchaseTypeFilter).ToArray();
        ProjectPurchases.ItemsSource = rows;
        ProjectPurchaseFilter.Text = _purchaseTypeFilter == null ? "All purchases assigned to this project" : $"{rows.First().Purchase.Name} | {rows.Sum(p => p.Cost):N2} ISK assigned";
        ClearPurchaseFilter.Visibility = _purchaseTypeFilter == null ? Visibility.Collapsed : Visibility.Visible;
        ProjectPurchaseEmpty.Text = "No purchases assigned yet. In Market, select a purchase and choose this project in Bought for.";
    }
    private sealed record ProjectPurchaseRow(MarketTransaction Purchase, PurchaseAllocation Allocation, string Buyer, decimal Cost);
}
