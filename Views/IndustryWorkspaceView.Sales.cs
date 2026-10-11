using System.Windows;
using EveCommandCenter.Models;
namespace EveCommandCenter.Views;
public partial class IndustryWorkspaceView
{
    private void ReloadProjectSales_Click(object sender, RoutedEventArgs e) => RefreshProjectSales();
    private void RefreshProjectSales()
    {
        if (!_initialized || _projectId is not {} id) return;
        try
        {
            var rows = _store.ProjectSales(id).OrderByDescending(p => p.Sale.Date).Select(p => new ProjectSaleRow(p.Sale, p.Allocation, Pilot(p.OwnerId), p.GrossProceeds)).ToArray();
            ProjectSales.ItemsSource = rows;
            ProjectSalesSummary.Text = $"{rows.Sum(p => p.GrossProceeds):N2} ISK";
            ProjectSalesQuantity.Text = $"{rows.Sum(p => p.Allocation.Quantity):N0}";
            ProjectSalesNote.Text = rows.Length == 0 ? "No sales linked yet. In Market & Wallet > Sales, select a sale and confirm the project its units came from." : "Project origin confirmed by you. Receipts are before fees and costs; a sale link does not mark production or delivery complete.";
        }
        catch (Exception ex) { ProjectSales.ItemsSource = null; ProjectSalesSummary.Text = ProjectSalesQuantity.Text = "Unavailable"; ProjectSalesNote.Text = "Sales could not be loaded: " + ex.Message; }
    }
    private sealed record ProjectSaleRow(MarketTransaction Sale, PurchaseAllocation Allocation, string Seller, decimal GrossProceeds);
}
