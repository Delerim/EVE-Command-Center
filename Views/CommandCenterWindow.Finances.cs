using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class CommandCenterWindow
{
    private DateTimeOffset _nextProductionMaterials;
    private long _productionRevision = -1;
    private void ProductionMaterials_Open(object? sender, EventArgs e)
    {
        if (OpenModule("industry") is IndustryWindow industry) industry.ShowProductionMaterials();
    }
    private void DashboardWallet_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (OpenModule("market") is MarketOrdersWindow market)
            ((System.Windows.Controls.TabControl)market.FindName("MarketTabs")).SelectedIndex = 7;
    }
    private void RefreshFinancialDashboard(BackgroundOperations ops)
    {
        var summary = FinancialDashboard.Build(ops.MarketOrders, ops.SyncSettings, ops.LinkedPilots, DateTimeOffset.UtcNow);
        DashboardOrders.Text = summary.Orders; DashboardOrderCoverage.Text = summary.OrderCoverage;
        DashboardWallet.Text = summary.Wallet; DashboardWalletCoverage.Text = summary.WalletCoverage;
        DashboardTrades.Text = summary.Trades;
        var workspace = ops.IndustryWorkspace.Snapshot();
        if (ops.IndustryWorkspace.Error.Length > 0)
        {
            ProductionMaterials.ShowUnavailable();
            _nextProductionMaterials = default;
        }
        else if (_productionRevision != workspace.Revision || DateTimeOffset.UtcNow >= _nextProductionMaterials)
        {
            ProductionMaterials.ShowSummary(ProductionDashboard.Build(workspace, ops.Industry.State.Pilots, ops.LinkedPilots.ToArray(), DateTimeOffset.UtcNow));
            _productionRevision = workspace.Revision;
            _nextProductionMaterials = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        var projects = workspace.Projects.Where(p => !p.Archived).ToArray();
        DashboardProjects.Text = ops.IndustryWorkspace.Error.Length > 0 ? "Project data unavailable" : $"{projects.Length:N0} projects | {projects.Count(p => p.Paused):N0} paused\n{projects.Sum(p => p.Nodes.Sum(n => IndustryJobMatching.LinkedJobs(n).Count())):N0} linked EVE jobs";
        DashboardProjectNames.Text = projects.Length == 0 ? "Create a build in Industry > Production Queue." : string.Join(" / ", projects.Take(5).Select(p => p.Name)) + (projects.Length > 5 ? " / ..." : "");
        DashboardSync.Text = ops.SyncSettings.Error.Length > 0 ? ops.SyncSettings.Error : "Choose automatic sync and independent dashboard visibility in Command Center Setup.";
    }
}
