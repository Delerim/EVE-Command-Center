using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class IndustryWorkspaceView
{
    private bool _loadingFinances;
    private Guid? _financeProject;
    private sealed record FinancePilot(long? Id, string Name) { public override string ToString() => Name; }
    private sealed record FinanceRow(ProjectCashFlow.Entry Source, string PilotName)
    {
        public string Color => Source.Trade.Buy ? "#EDAD83" : "#76D7CB";
        public string Detail => $"{PilotName} | {Source.Trade.Date:dd MMM yyyy HH:mm} UTC | {Source.Split.Quantity:N0} {Source.Trade.Name} at {Source.Trade.UnitPrice:N2} ISK each. " +
            $"{Source.Amount:N2} ISK assigned to this project. {Source.Trade.Location}. " + Source.Split.Note;
    }
    private sealed record ExpenseFinanceRow(AttributedExpense Source, string PilotName)
    {
        public string Activity => ExpenseEvidence.Name(Source.Entry);
        public string Detail => $"{PilotName} | {Source.Entry.Date:dd MMM yyyy HH:mm} UTC | {Activity}: {ExpenseEvidence.Charge(Source.Entry):N2} ISK charged; {Source.Allocation.Amount:N2} ISK assigned here. " + Source.Allocation.Note;
    }
    private void FinanceActivity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FinanceOpenSource == null) return;
        FinanceOpenSource.Visibility = FinanceActivity.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
        if (FinanceActivity.SelectedIndex == 1) ExpenseFinance_Selected(sender, e); else FinanceEntry_Selected(sender, e);
    }
    private void ExpenseFinance_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (FinanceDetail == null) return;
        FinanceDetail.Text = (ProjectExpenseEntries.SelectedItem as ExpenseFinanceRow)?.Detail ?? "Select a charge to inspect its source amount and note. Edit assignments in Market & Wallet > Wallet activity.";
        FinanceOpenSource.IsEnabled = false;
    }
    private void FinanceFilter_Changed(object sender, SelectionChangedEventArgs e)
    { if (!_loadingFinances && _initialized) RefreshProjectFinances(); }
    private void ReloadProjectFinances_Click(object sender, RoutedEventArgs e) => RefreshProjectFinances();
    private void RefreshProjectFinances()
    {
        if (!_initialized || _projectId is not {} id || _loadingFinances) return;
        _loadingFinances = true;
        try
        {
            var (entries, expenses) = _store.ProjectFinancialEvidence(id);
            long? owner = _financeProject == id ? (FinancePilotFilter.SelectedItem as FinancePilot)?.Id : null;
            if (_financeProject != id) FinancePeriod.SelectedIndex = 0;
            _financeProject = id;
            var pilots = entries.Select(p => p.OwnerId).Concat(expenses.Select(p => p.OwnerId)).Distinct()
                .Select(p => new FinancePilot(p, Pilot(p))).OrderBy(p => p.Name).ToList();
            pilots.Insert(0, new(null, "All project pilots")); FinancePilotFilter.ItemsSource = pilots;
            FinancePilotFilter.SelectedItem = pilots.FirstOrDefault(p => p.Id == owner) ?? pilots[0];
            owner = ((FinancePilot)FinancePilotFilter.SelectedItem).Id;
            var now = DateTimeOffset.UtcNow;
            var since = FinancePeriod.SelectedIndex switch { 1 => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), 2 => now.AddDays(-7), 3 => now.AddDays(-30), _ => DateTimeOffset.MinValue };
            var summary = ProjectCashFlow.Calculate(entries, since, now, owner);
            var charges = ExpenseEvidence.Filter(expenses, since, now, owner);
            decimal expenseTotal = charges.Sum(e => e.Allocation.Amount), difference = checked(summary.Difference - expenseTotal);
            bool hasEvidence = summary.HasEvidence || charges.Length > 0;
            FinanceExpenses.Text = hasEvidence ? $"{expenseTotal:N2} ISK" : "No linked charges";
            ProjectExpenseEntries.ItemsSource = charges.Select(e => new ExpenseFinanceRow(e, Pilot(e.OwnerId))).ToArray();
            ExpenseFinanceEmpty.Visibility = charges.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FinancePaid.Text = hasEvidence ? $"{summary.Paid:N2} ISK" : "No linked trades";
            FinanceReceived.Text = hasEvidence ? $"{summary.Received:N2} ISK" : "No linked trades";
            FinanceDifference.Text = hasEvidence ? $"{difference:N2} ISK" : "Not available";
            FinanceDifference.Foreground = new System.Windows.Media.BrushConverter().ConvertFromString(difference >= 0 ? "#76D7CB" : "#EDAD83") as System.Windows.Media.Brush;
            FinanceScope.Text = $"{summary.PurchaseCount:N0} purchases | {summary.SaleCount:N0} sales | {charges.Select(e => (e.OwnerId, e.Entry.Id)).Distinct().Count():N0} charges | Selected pilot and period applied to every figure. Updated {now:HH:mm} UTC.";
            ProjectFinanceEntries.ItemsSource = summary.Entries.Select(e => new FinanceRow(e, Pilot(e.OwnerId))).ToArray();
            FinanceEmpty.Visibility = summary.HasEvidence ? Visibility.Collapsed : Visibility.Visible;
            FinanceEmpty.Text = "No linked trades in this view. Assign purchases and sales to this project in Market & Wallet, or change the filters.";
            FinanceActivity_Changed(this, null!);
        }
        catch (Exception ex)
        {
            ProjectFinanceEntries.ItemsSource = null; ProjectExpenseEntries.ItemsSource = null; FinanceExpenses.Text = "Unavailable"; FinancePaid.Text = FinanceReceived.Text = FinanceDifference.Text = "Unavailable";
            FinanceScope.Text = "Financial records could not be loaded."; FinanceEmpty.Visibility = Visibility.Visible; FinanceEmpty.Text = ex.Message;
            FinanceDetail.Text = "Reload to try again. Saved allocations are unchanged.";
        }
        finally { _loadingFinances = false; }
    }
    private void FinanceEntry_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (FinanceDetail == null) return;
        FinanceDetail.Text = (ProjectFinanceEntries.SelectedItem as FinanceRow)?.Detail ?? "Select a row to inspect its source.";
        FinanceOpenSource.IsEnabled = ProjectFinanceEntries.SelectedItem is FinanceRow;
    }
    private void FinanceOpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectFinanceEntries.SelectedItem is not FinanceRow row) return;
        if (row.Source.Trade.Buy)
        {
            WorkspaceTabs.SelectedItem = ProjectPurchasesTab; _purchaseTypeFilter = null; RefreshProjectPurchases(); ProjectCostViews.SelectedIndex = 1;
            ProjectPurchases.SelectedItem = ProjectPurchases.Items.Cast<ProjectPurchaseRow>().FirstOrDefault(p => p.OwnerId == row.Source.OwnerId && p.Purchase.Id == row.Source.Trade.Id && p.Allocation.Id == row.Source.Split.Id);
            if (ProjectPurchases.SelectedItem != null) ProjectPurchases.ScrollIntoView(ProjectPurchases.SelectedItem);
        }
        else
        {
            WorkspaceTabs.SelectedItem = ProjectSalesTab; RefreshProjectSales();
            ProjectSales.SelectedItem = ProjectSales.Items.Cast<ProjectSaleRow>().FirstOrDefault(p => p.OwnerId == row.Source.OwnerId && p.Sale.Id == row.Source.Trade.Id && p.Allocation.Id == row.Source.Split.Id);
            if (ProjectSales.SelectedItem != null) ProjectSales.ScrollIntoView(ProjectSales.SelectedItem);
        }
    }
}
