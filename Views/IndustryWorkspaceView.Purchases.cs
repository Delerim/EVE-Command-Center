using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
namespace EveCommandCenter.Views;
public partial class IndustryWorkspaceView
{
    private void WorkspaceTab_Changed(object sender, SelectionChangedEventArgs e)
    { if (_initialized && e.Source == WorkspaceTabs && WorkspaceTabs.SelectedItem == ProjectPurchasesTab) RefreshProjectPurchases(); }
    private void ReloadProjectPurchases_Click(object sender, RoutedEventArgs e) => RefreshProjectPurchases();
    private void RefreshProjectPurchases()
    {
        if (!_initialized || _projectId is not {} id) return;
        try
        {
            var rows = _store.ProjectPurchases(id).OrderByDescending(p => p.Purchase.Date).Select(p => new ProjectPurchaseRow(p.Purchase, p.Allocation, Pilot(p.OwnerId), p.Cost)).ToArray();
            ProjectPurchases.ItemsSource = rows;
            ProjectPurchaseSummary.Text = rows.Length == 0 ? "No purchases attributed yet. Select a purchase in Market to assign its quantity to this project." : $"{rows.Sum(p => p.Cost):N2} ISK attributed purchase cost | {rows.Length:N0} saved splits";
        }
        catch (Exception ex) { ProjectPurchases.ItemsSource = null; ProjectPurchaseSummary.Text = "Could not load purchases: " + ex.Message; }
    }
    private sealed record ProjectPurchaseRow(MarketTransaction Purchase, PurchaseAllocation Allocation, string Buyer, decimal Cost);
}
