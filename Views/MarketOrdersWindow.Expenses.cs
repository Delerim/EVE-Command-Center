using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class MarketOrdersWindow
{
    private ExpenseAllocationSnapshot? _expense;
    private JournalRow? _expenseSource;
    private bool _expenseLoading;
    private void HideExpenseAllocation()
    {
        if (ExpensePanel == null || ExpensePanel.Visibility != Visibility.Visible) return;
        if (AllocationWidth.ActualWidth > 0) _allocationPanelWidth = Math.Max(340, AllocationWidth.ActualWidth);
        ExpensePanel.Visibility = AllocationDivider.Visibility = Visibility.Collapsed;
        AllocationWidth.MinWidth = 0; AllocationWidth.Width = new GridLength(0); _expense = null; _expenseSource = null;
    }
    private void ShowExpenseAllocation()
    {
        if (_service == null || ExpensePanel == null || _expenseLoading) return;
        if (MarketTabs.SelectedIndex != 6 || JournalGrid.SelectedItem is not JournalRow source || !ExpenseEvidence.Eligible(source.Entry))
        { HideExpenseAllocation(); return; }
        _expenseLoading = true;
        try
        {
            _expense = _service.ExpenseAllocation(source.OwnerId, source.Entry.Id); _expenseSource = source;
            AllocationWidth.MinWidth = 340; AllocationWidth.Width = new GridLength(_allocationPanelWidth);
            ExpensePanel.Visibility = AllocationDivider.Visibility = Visibility.Visible;
            decimal charge = ExpenseEvidence.Charge(source.Entry), assigned = _expense.Splits.Sum(s => s.Amount);
            ExpenseTitle.Text = $"{ExpenseEvidence.Name(source.Entry)} | {charge:N2} ISK";
            ExpenseSource.Text = $"{source.Owner} | {source.Entry.Date:dd MMM yyyy HH:mm} UTC";
            ExpenseRemaining.Text = $"{charge - assigned:N2} ISK";
            var targets = _service.ExpenseProjects().OrderBy(p => p.Name).Select(p => new AllocationDestination("Project", p.Id, p.Name, Icon: $"https://images.evetech.net/types/{p.Nodes.FirstOrDefault(n => n.ParentId == null)?.TypeId ?? 0}/icon?size=32")).ToList();
            foreach (var split in _expense.Splits.Where(s => !targets.Any(t => t.ProjectId == s.ProjectId))) targets.Add(new("Project", split.ProjectId, split.ProjectName + " (historical)"));
            ExpenseTarget.ItemsSource = targets; ExpenseSplits.ItemsSource = _expense.Splits; ExpenseSaved.Visibility = _expense.Splits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ExpenseAudit.Text = string.Join("\n\n", _service.ExpenseHistory(source.OwnerId, source.Entry.Id).Select(h => $"{h.TimeUtc:dd MMM yyyy HH:mm} UTC\n{DescribeExpenses(h.Before)} -> {DescribeExpenses(h.After)}"));
            if (ExpenseAudit.Text.Length == 0) ExpenseAudit.Text = "No changes saved yet.";
            ResetExpense(); ExpenseMessage.Text = targets.Count == 0 ? "Create a production project first. This charge will remain unassigned." : "";
        }
        catch (Exception ex) { _expense = null; ExpenseMessage.Text = ex.Message; Status.Text = "Charge assignments could not be loaded: " + ex.Message; }
        finally { _expenseLoading = false; UpdateExpenseForm(); }
    }
    private static string DescribeExpenses(IEnumerable<ExpenseAllocation> splits) => splits.Any() ? string.Join("; ", splits.Select(s => $"{s.Amount:N2} ISK to {s.ProjectName}" + (s.Note.Length > 0 ? " - " + s.Note : ""))) : "No assignments";
    private void ResetExpense()
    {
        ExpenseSplits.SelectedIndex = -1; ExpenseTarget.SelectedIndex = -1; ExpenseNote.Text = "";
        ExpenseAmount.Text = (_expenseSource == null ? 0 : ExpenseEvidence.Charge(_expenseSource.Entry) - (_expense?.Splits.Sum(s => s.Amount) ?? 0)).ToString(CultureInfo.CurrentCulture);
    }
    private void NewExpense_Click(object sender, RoutedEventArgs e) { _expenseLoading = true; ResetExpense(); _expenseLoading = false; UpdateExpenseForm(); }
    private void ReloadExpense_Click(object sender, RoutedEventArgs e) => ShowExpenseAllocation();
    private void ExpenseSplit_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_expenseLoading || ExpenseSplits.SelectedItem is not ExpenseAllocation split) return;
        ExpenseTarget.SelectedItem = ExpenseTarget.Items.Cast<AllocationDestination>().FirstOrDefault(t => t.ProjectId == split.ProjectId);
        ExpenseAmount.Text = split.Amount.ToString(CultureInfo.CurrentCulture); ExpenseNote.Text = split.Note; UpdateExpenseForm();
    }
    private void ExpenseForm_Changed(object sender, RoutedEventArgs e) => UpdateExpenseForm();
    private void UpdateExpenseForm()
    {
        if (SaveExpense == null || _expenseLoading) return;
        var selected = ExpenseSplits.SelectedItem as ExpenseAllocation;
        decimal remaining = _expenseSource == null ? 0 : ExpenseEvidence.Charge(_expenseSource.Entry) - (_expense?.Splits.Where(s => s.Id != selected?.Id).Sum(s => s.Amount) ?? 0);
        SaveExpense.IsEnabled = _expense != null && ExpenseTarget.SelectedItem is AllocationDestination && decimal.TryParse(ExpenseAmount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) && amount > 0 && amount <= remaining;
        SaveExpense.Content = selected == null ? "Assign charge" : "Save changes"; RemoveExpense.IsEnabled = _expense != null && selected != null;
    }
    private void SaveExpense_Click(object sender, RoutedEventArgs e)
    {
        if (_expense == null || _expenseSource == null || ExpenseTarget.SelectedItem is not AllocationDestination target || target.ProjectId is not {} project || !decimal.TryParse(ExpenseAmount.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount)) return;
        var selected = ExpenseSplits.SelectedItem as ExpenseAllocation;
        var rows = _expense.Splits.Where(s => s.Id != selected?.Id).ToList(); rows.Add(new(selected?.Id ?? Guid.NewGuid(), project, selected?.ProjectName ?? target.Name, amount, ExpenseNote.Text));
        SaveExpenses(rows);
    }
    private void RemoveExpense_Click(object sender, RoutedEventArgs e)
    { if (_expense != null && ExpenseSplits.SelectedItem is ExpenseAllocation split) SaveExpenses(_expense.Splits.Where(s => s.Id != split.Id).ToArray()); }
    private void SaveExpenses(IReadOnlyList<ExpenseAllocation> rows)
    {
        if (_expense == null || _expenseSource == null) return;
        try
        {
            _service!.SaveExpenseAllocation(_expense.OwnerId, _expense.EntryId, _expense.Revision, rows);
            ShowExpenseAllocation(); ExpenseMessage.Text = "Saved. Project Finances includes these assigned charges.";
        }
        catch (Exception ex) { ExpenseMessage.Text = ex.Message; }
    }
}
