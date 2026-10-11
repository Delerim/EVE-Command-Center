using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using EveCommandCenter.Models;

namespace EveCommandCenter.Views;
public partial class MarketOrdersWindow
{
    private PurchaseAllocationSnapshot? _allocation;
    private TransactionRow? _allocationPurchase;
    private bool _allocationLoading;
    private double _allocationPanelWidth = 440;
    internal double AllocationPanelSize
    {
        get => AllocationWidth.ActualWidth > 0 ? AllocationWidth.ActualWidth : _allocationPanelWidth;
        set => _allocationPanelWidth = double.IsFinite(value) ? Math.Clamp(value, 340, 800) : 440;
    }
    private sealed record AllocationDestination(string Purpose, Guid? ProjectId, string Name)
    { public override string ToString() => Name; }
    private void HidePurchaseAllocation()
    {
        if (AllocationPanel == null) return;
        if (AllocationWidth.ActualWidth > 0) _allocationPanelWidth = Math.Max(340, AllocationWidth.ActualWidth);
        AllocationWidth.Width = new GridLength(0); AllocationWidth.MinWidth = 0;
        AllocationPanel.Visibility = AllocationDivider.Visibility = Visibility.Collapsed;
        _allocation = null; _allocationPurchase = null;
    }
    private void ShowPurchaseAllocation()
    {
        if (_service == null || AllocationPanel == null) return;
        if (MarketTabs.SelectedIndex != 4 || TransactionsGrid.SelectedItem is not TransactionRow purchase) { HidePurchaseAllocation(); return; }
        try
        {
            _allocationLoading = true; _allocationPurchase = purchase; _allocation = null; SaveAllocation.IsEnabled = false; RemoveAllocation.IsEnabled = false;
            _allocation = _service.PurchaseAllocation(purchase.OwnerId, purchase.Transaction.Id);
            AllocationWidth.MinWidth = 340; AllocationWidth.Width = new GridLength(_allocationPanelWidth);
            AllocationPanel.Visibility = AllocationDivider.Visibility = Visibility.Visible;
            AllocationIcon.Source = new BitmapImage(new Uri(purchase.Transaction.Icon)); AllocationItem.Text = purchase.Transaction.Name;
            long free = purchase.Transaction.Quantity - _allocation.Splits.Sum(a => a.Quantity);
            AllocationSummary.Text = $"{purchase.Owner} bought {purchase.Transaction.Quantity:N0} for {purchase.Transaction.Total:N2} ISK.\n{free:N0} units still unallocated.";
            var destinations = new List<AllocationDestination> { new("Stock", null, "General stock"), new("Unrelated", null, "Unrelated activity") };
            destinations.AddRange(_service.AllocationProjects(purchase.Transaction.TypeId).OrderBy(p => p.Name).Select(p => new AllocationDestination("Project", p.Id, p.Name)));
            foreach (var historical in _allocation.Splits.Where(a => a.Purpose == "Project" && !destinations.Any(d => d.ProjectId == a.ProjectId)))
                destinations.Add(new("Project", historical.ProjectId, historical.ProjectName + " (historical)"));
            AllocationTarget.ItemsSource = destinations; AllocationSplits.ItemsSource = _allocation.Splits;
            AllocationAudit.Text = string.Join("\n\n", _service.PurchaseHistory(purchase.OwnerId, purchase.Transaction.Id).Select(h => $"{h.TimeUtc:dd MMM yyyy HH:mm} UTC\n{Describe(h.Before)} -> {Describe(h.After)}"));
            AllocationMessage.Text = "Choose a destination explicitly. Matching projects are suggestions, not confirmed intent.";
            ResetSplit(); SaveAllocation.IsEnabled = true;
        }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; }
        finally { _allocationLoading = false; }
    }
    private static string Describe(IEnumerable<PurchaseAllocation> splits) => splits.Any() ? string.Join("; ", splits.Select(s => $"{s.Quantity:N0} to {s.ProjectName}" + (s.Note.Length > 0 ? " - " + s.Note : ""))) : "No assigned quantities";
    private void ResetSplit()
    {
        AllocationSplits.SelectedIndex = -1; AllocationTarget.SelectedIndex = -1; AllocationNote.Text = ""; AllocationNoteSection.IsExpanded = false;
        AllocationQuantity.Text = Math.Max(0, (_allocationPurchase?.Transaction.Quantity ?? 0) - (_allocation?.Splits.Sum(s => s.Quantity) ?? 0)).ToString(CultureInfo.CurrentCulture);
        RemoveAllocation.IsEnabled = false;
    }
    private void NewAllocation_Click(object sender, RoutedEventArgs e) { _allocationLoading = true; ResetSplit(); _allocationLoading = false; }
    private void AllocationSplit_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_allocationLoading || AllocationSplits.SelectedItem is not PurchaseAllocation split) return;
        AllocationTarget.SelectedItem = AllocationTarget.Items.Cast<AllocationDestination>().FirstOrDefault(d => d.Purpose == split.Purpose && d.ProjectId == split.ProjectId);
        AllocationQuantity.Text = split.Quantity.ToString(CultureInfo.CurrentCulture); AllocationNote.Text = split.Note; AllocationNoteSection.IsExpanded = split.Note.Length > 0; RemoveAllocation.IsEnabled = true;
    }
    private void SaveAllocation_Click(object sender, RoutedEventArgs e)
    {
        if (_allocation == null || _allocationPurchase == null || _service == null) return;
        try
        {
            if (AllocationTarget.SelectedItem is not AllocationDestination destination) throw new InvalidOperationException("Choose a destination first.");
            if (!long.TryParse(AllocationQuantity.Text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out long quantity) || quantity <= 0) throw new InvalidOperationException("Enter a positive whole quantity.");
            var selected = AllocationSplits.SelectedItem as PurchaseAllocation;
            var split = new PurchaseAllocation(selected?.Id ?? Guid.NewGuid(), destination.Purpose, destination.ProjectId, selected?.ProjectName ?? destination.Name, quantity, AllocationNote.Text);
            var rows = _allocation.Splits.Where(r => r.Id != split.Id).Append(split).ToArray();
            _service.SavePurchaseAllocation(_allocation.OwnerId, _allocation.TransactionId, _allocation.Revision, rows);
            RenderOrders(); Status.Text = "Purchase allocation saved."; AllocationMessage.Text = "Saved. Purchase cost assigned; physical stock is unchanged.";
        }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; }
    }
    private void RemoveAllocation_Click(object sender, RoutedEventArgs e)
    {
        if (_allocation == null || _service == null || AllocationSplits.SelectedItem is not PurchaseAllocation selected) return;
        try
        {
            _service.SavePurchaseAllocation(_allocation.OwnerId, _allocation.TransactionId, _allocation.Revision, _allocation.Splits.Where(s => s.Id != selected.Id).ToArray());
            RenderOrders(); Status.Text = "Purchase split removed; history retained."; AllocationMessage.Text = "Split removed. Its previous assignment remains in history.";
        }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; }
    }
    private void ReloadAllocation_Click(object sender, RoutedEventArgs e)
    {
        try { _service?.ReloadAllocations(); RenderOrders(); ShowPurchaseAllocation(); }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; }
    }
}
