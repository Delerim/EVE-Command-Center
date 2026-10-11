using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class CommandCenterWindow
{
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
        var projects = ops.IndustryWorkspace.Snapshot().Projects.Where(p => !p.Archived).ToArray();
        DashboardProjects.Text = ops.IndustryWorkspace.Error.Length > 0 ? "Project data unavailable" : $"{projects.Length:N0} projects | {projects.Count(p => p.Paused):N0} paused\n{projects.Sum(p => p.Nodes.Count(n => n.ActualJob != null)):N0} linked EVE jobs";
        DashboardProjectNames.Text = projects.Length == 0 ? "Create a build in Industry > Production Queue." : string.Join(" / ", projects.Take(5).Select(p => p.Name)) + (projects.Length > 5 ? " / ..." : "");
        DashboardSync.Text = ops.SyncSettings.Error.Length > 0 ? ops.SyncSettings.Error : "Choose automatic sync and independent dashboard visibility in Command Center Setup.";
    }
}
