using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using EveCommandCenter.Models;

namespace EveCommandCenter.Views;
public partial class MarketOrdersWindow
{
    private PurchaseAllocationSnapshot? _allocation;
    private TransactionRow? _allocationPurchase;
    private bool _allocationLoading;
    private bool AllocatingSale => _allocationPurchase?.Transaction.Buy == false;
    private double _allocationPanelWidth = 440;
    internal double AllocationPanelSize
    {
        get => AllocationWidth.ActualWidth > 0 ? AllocationWidth.ActualWidth : _allocationPanelWidth;
        set => _allocationPanelWidth = double.IsFinite(value) ? Math.Clamp(value, 340, 800) : 440;
    }
    private sealed record AllocationDestination(string Purpose, Guid? ProjectId, string Name, string Detail = "", string? Icon = null, string Symbol = "")
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
        if (MarketTabs.SelectedIndex is not (4 or 5) || TransactionsGrid.SelectedItem is not TransactionRow purchase) { HidePurchaseAllocation(); return; }
        try
        {
            _allocationLoading = true; _allocationPurchase = purchase; _allocation = null; SaveAllocation.IsEnabled = false; RemoveAllocation.IsEnabled = false;
            _allocation = AllocatingSale ? _service.SaleAllocation(purchase.OwnerId, purchase.Transaction.Id) : _service.PurchaseAllocation(purchase.OwnerId, purchase.Transaction.Id);
            AllocationTitle.Text = AllocatingSale ? "Where did this sale come from?" : "Why did I buy this?";
            AllocationDestinationLabel.Text = AllocatingSale ? "Sold for" : "Bought for";
            AllocationValueLabel.Text = AllocatingSale ? "Gross receipts assigned" : "Cost assigned";
            AllocationPrompt.Text = AllocatingSale ? "Choose the source of these sold units" : "Choose where this purchase belongs";
            AllocationExplanation.Text = AllocatingSale ? "Project links are your confirmed attribution. They do not prove a production batch, delivery or project profit; fees and costs remain separate." : "Assigning a purchase records its cost. Reserve and check delivery separately in your project.";
            AllocationWidth.MinWidth = 340; AllocationWidth.Width = new GridLength(_allocationPanelWidth);
            AllocationPanel.Visibility = AllocationDivider.Visibility = Visibility.Visible;
            AllocationIcon.Source = new BitmapImage(new Uri(purchase.Transaction.Icon)); AllocationItem.Text = purchase.Transaction.Name;
            long free = purchase.Transaction.Quantity - _allocation.Splits.Sum(a => a.Quantity);
            AllocationSummary.Text = $"{purchase.Owner} {(AllocatingSale ? "sold" : "bought")} {purchase.Transaction.Quantity:N0} for {purchase.Transaction.Total:N2} ISK.\n{free:N0} units still unallocated.";
            AllocationAssigned.Text = $"{purchase.Transaction.Quantity - free:N0}";
            AllocationRemaining.Text = $"{free:N0}";
            AllocationProgress.Value = purchase.Transaction.Quantity > 0 ? (double)((decimal)(purchase.Transaction.Quantity - free) / purchase.Transaction.Quantity * 100) : 0;
            AllocationEmpty.Visibility = _allocation.Splits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AllocationSplits.Visibility = _allocation.Splits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            var destinations = new List<AllocationDestination> { new("Stock", null, AllocatingSale ? "Sold from general stock" : "General stock", AllocatingSale ? "Not linked to a production project" : "Keep for future use", Symbol: "\u25A6"), new("Unrelated", null, "Unrelated activity", "Personal use or another purpose", Symbol: "\u25CB") };
            var projects = (AllocatingSale ? _service.SaleProjects(purchase.Transaction.TypeId) : _service.AllocationProjects(purchase.Transaction.TypeId)).OrderBy(p => p.Name).ToList();
            destinations.AddRange(projects.Select(p => new AllocationDestination("Project", p.Id, p.Name, p.Paused ? "Matching project - paused" : AllocatingSale ? "Project produces this item - confirm its origin" : "Project contains this item", $"https://images.evetech.net/types/{p.Nodes.FirstOrDefault(n => n.ParentId == null)?.TypeId ?? purchase.Transaction.TypeId}/icon?size=32")));
            AllocationMatches.Text = projects.Count == 0
                ? (AllocatingSale ? "No active projects produce this item. Choose general stock or unrelated activity." : "No active projects contain this item. You can assign it to general stock or unrelated activity.")
                : $"{projects.Count:N0} matching project{(projects.Count == 1 ? "" : "s")} in the list. Choose one to assign this {(AllocatingSale ? "sale" : "purchase")}.";
            foreach (var historical in _allocation.Splits.Where(a => a.Purpose == "Project" && !destinations.Any(d => d.ProjectId == a.ProjectId)))
                destinations.Add(new("Project", historical.ProjectId, historical.ProjectName, "Historical allocation - keep or remove", Symbol: "\u25C7"));
            AllocationTarget.ItemsSource = destinations; AllocationSplits.ItemsSource = _allocation.Splits;
            AllocationAudit.Text = string.Join("\n\n", (AllocatingSale ? _service.SaleHistory(purchase.OwnerId, purchase.Transaction.Id) : _service.PurchaseHistory(purchase.OwnerId, purchase.Transaction.Id)).Select(h => $"{h.TimeUtc:dd MMM yyyy HH:mm} UTC\n{Describe(h.Before)} -> {Describe(h.After)}"));
            AllocationMessage.Text = ""; AllocationMessage.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 185, 187));
            if (AllocationAudit.Text.Length == 0) AllocationAudit.Text = "History appears after your first saved allocation.";
            ResetSplit(); UpdateAllocationForm();
        }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; AllocationMessage.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 173, 131)); }
        finally { _allocationLoading = false; UpdateAllocationForm(); }
    }
    private static string Describe(IEnumerable<PurchaseAllocation> splits) => splits.Any() ? string.Join("; ", splits.Select(s => $"{s.Quantity:N0} to {s.ProjectName}" + (s.Note.Length > 0 ? " - " + s.Note : ""))) : "No assigned quantities";
    private void ResetSplit()
    {
        ConfirmSaleProject.IsChecked = false; AllocationSplits.SelectedIndex = -1; AllocationTarget.SelectedIndex = -1; AllocationNote.Text = ""; AllocationNoteSection.IsExpanded = false;
        AllocationQuantity.Text = Math.Max(0, (_allocationPurchase?.Transaction.Quantity ?? 0) - (_allocation?.Splits.Sum(s => s.Quantity) ?? 0)).ToString(CultureInfo.CurrentCulture);
        RemoveAllocation.IsEnabled = false;
        UpdateAllocationForm();
    }
    private void NewAllocation_Click(object sender, RoutedEventArgs e) { _allocationLoading = true; ResetSplit(); _allocationLoading = false; }
    private void AllocationSplit_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_allocationLoading || AllocationSplits.SelectedItem is not PurchaseAllocation split) return;
        ConfirmSaleProject.IsChecked = false;
        AllocationTarget.SelectedItem = AllocationTarget.Items.Cast<AllocationDestination>().FirstOrDefault(d => d.Purpose == split.Purpose && d.ProjectId == split.ProjectId);
        AllocationQuantity.Text = split.Quantity.ToString(CultureInfo.CurrentCulture); AllocationNote.Text = split.Note; AllocationNoteSection.IsExpanded = split.Note.Length > 0; RemoveAllocation.IsEnabled = true;
        UpdateAllocationForm();
    }
    private void AllocationForm_Changed(object sender, RoutedEventArgs e)
    { if (e.Source == AllocationTarget && ConfirmSaleProject != null) ConfirmSaleProject.IsChecked = false; UpdateAllocationForm(); }
    private void UpdateAllocationForm()
    {
        if (AllocationPrompt == null || SaveAllocation == null || AllocationCostPreview == null) return;
        AllocationPrompt.Visibility = AllocationTarget.SelectedItem == null ? Visibility.Visible : Visibility.Collapsed;
        var selected = AllocationSplits.SelectedItem as PurchaseAllocation;
        SaveAllocation.Content = selected == null ? (AllocatingSale ? "Assign sale" : "Assign purchase") : "Save changes";
        bool needsOrigin = AllocatingSale && AllocationTarget.SelectedItem is AllocationDestination { Purpose: "Project" };
        ConfirmSaleProject.Visibility = needsOrigin ? Visibility.Visible : Visibility.Collapsed;
        long remaining = (_allocationPurchase?.Transaction.Quantity ?? 0) - (_allocation?.Splits.Sum(s => s.Quantity) ?? 0) + (selected?.Quantity ?? 0);
        bool valid = long.TryParse(AllocationQuantity.Text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out long quantity) && quantity > 0 && quantity <= remaining;
        SaveAllocation.IsEnabled = valid && _allocation != null && AllocationTarget.SelectedItem is AllocationDestination && (!needsOrigin || ConfirmSaleProject.IsChecked == true);
        AllocationCostPreview.Text = valid && _allocationPurchase != null ? $"{quantity * _allocationPurchase.Transaction.UnitPrice:N2} ISK" : $"Up to {remaining:N0} units";
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
            if (AllocatingSale) _service.SaveSaleAllocation(_allocation.OwnerId, _allocation.TransactionId, _allocation.Revision, rows, ConfirmSaleProject.IsChecked == true);
            else _service.SavePurchaseAllocation(_allocation.OwnerId, _allocation.TransactionId, _allocation.Revision, rows);
            bool sale = AllocatingSale; RenderOrders(); Status.Text = sale ? "Sale allocation saved." : "Purchase allocation saved."; AllocationMessage.Text = sale ? "Saved. Gross sale receipts assigned; costs and fees are separate." : "Saved. Purchase cost assigned; physical stock is unchanged.";
        }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; AllocationMessage.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 173, 131)); }
    }
    private void RemoveAllocation_Click(object sender, RoutedEventArgs e)
    {
        if (_allocation == null || _service == null || AllocationSplits.SelectedItem is not PurchaseAllocation selected) return;
        try
        {
            var rows = _allocation.Splits.Where(s => s.Id != selected.Id).ToArray();
            if (AllocatingSale) _service.SaveSaleAllocation(_allocation.OwnerId, _allocation.TransactionId, _allocation.Revision, rows, false);
            else _service.SavePurchaseAllocation(_allocation.OwnerId, _allocation.TransactionId, _allocation.Revision, rows);
            RenderOrders(); Status.Text = "Allocation removed; history retained."; AllocationMessage.Text = "Split removed. Its previous assignment remains in history.";
        }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; AllocationMessage.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 173, 131)); }
    }
    private void ReloadAllocation_Click(object sender, RoutedEventArgs e)
    {
        try { _service?.ReloadAllocations(); RenderOrders(); ShowPurchaseAllocation(); }
        catch (Exception ex) { AllocationMessage.Text = ex.Message; AllocationMessage.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 173, 131)); }
    }
}
