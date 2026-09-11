using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class ContractsWindow : Window
{
    private readonly BackgroundOperations _operations = BackgroundOperations.Current;
    private ContractService Service => _operations.Contracts;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<EvePilotProfile> _pilots = Array.Empty<EvePilotProfile>();
    private bool _closed;
    public ContractsWindow()
    {
        InitializeComponent();
        RateBox.Text = Service.State.BuyPercent.ToString(CultureInfo.InvariantCulture);
        ToleranceBox.Text = Service.State.TolerancePercent.ToString(CultureInfo.InvariantCulture);
        AlertsCheck.IsChecked = Service.State.NotificationsEnabled;
        Service.Changed += Render;
        Loaded += async (_, _) =>
        {
            try { await LoadPilotsAsync(); Render(); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        };
        Closed += (_, _) => { _closed = true; Service.Changed -= Render; _lifetime.Cancel(); };
    }
    private async Task LoadPilotsAsync(long preferred = 0)
    {
        _pilots = await _operations.Sso.LoadPilotsAsync();
        if (_closed) return;
        PilotCombo.ItemsSource = _pilots.Where(p => p.CharacterId == _operations.Access.State.ContractCharacterId).ToArray();
        PilotCombo.SelectedItem = _pilots.FirstOrDefault(p => p.CharacterId == (preferred > 0 ? preferred : Service.State.CharacterId)) ?? _pilots.FirstOrDefault();
    }
    private void Render()
    {
        if (_closed) return;
        var state = Service.State;
        CountText.Text = state.Rows.Count.ToString("N0");
        CorpText.Text = state.CorporationName;
        ValueText.Text = (state.Rows.Sum(r => r.Contract.Price ?? 0) / 1_000_000m).ToString("N2") + "M ISK";
        PassedText.Text = state.Rows.Count(r => r.Passed).ToString();
        AttentionText.Text = state.Rows.Count(r => !r.Passed).ToString();
        FreshnessText.Text = state.LastRefreshUtc is { } time ? "Updated " + time.ToLocalTime().ToString("dd MMM HH:mm") + (DateTimeOffset.UtcNow - time > TimeSpan.FromMinutes(45) ? " (stale)" : "") : "Not refreshed";
        RefreshButton.IsEnabled = !Service.IsRefreshing;
        if (Service.IsRefreshing) StatusText.Text = "Refreshing corporation contracts and checking appraisals...";
        else if (Service.LastError != null) StatusText.Text = "Refresh failed; showing last successful data. " + Service.LastError;
        else StatusText.Text = state.Rows.Count == 0 ? "No outstanding contracts in the current snapshot. Checks run each minute, subject to ESI cache expiry." : "Double-click for contents. Monitoring remains active with this window closed. Checks do not verify item-by-item appraisal contents.";
        ApplyFilter();
    }
    private void ApplyFilter()
    {
        if (ContractsGrid == null || SearchBox == null || FilterCombo == null) return;
        var text = SearchBox.Text.Trim();
        var rows = Service.State.Rows.Where(r => text.Length == 0 || (r.Issuer + " " + r.Location + " " + r.Contract.Title + " " + r.Contract.Id).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (FilterCombo.SelectedIndex == 1) rows = rows.Where(r => r.Passed);
        if (FilterCombo.SelectedIndex == 2) rows = rows.Where(r => !r.Passed);
        ContractsGrid.ItemsSource = rows.ToArray();
        if (HistoryGrid == null) return;
        var history = Service.State.History.Where(r => r.CorporationId == Service.State.CorporationId).ToArray();
        HistorySummary.Text = $"{history.Length:N0} recorded contracts | {history.Count(r => r.Contract.WasAccepted):N0} accepted | {history.Sum(r => r.Contract.Price ?? 0):N0} ISK total recorded price";
        HistoryGrid.ItemsSource = history.Where(r => text.Length == 0 || (r.Issuer + " " + r.Acceptor + " " + r.Contract.Status + " " + r.Contract.Title + " " + r.Contract.Id).Contains(text, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Contract.Accepted ?? r.Contract.Issued).ToArray();
        AcceptorsGrid.ItemsSource = history.Where(r => r.Contract.WasAccepted).GroupBy(r => r.Contract.AcceptorId).Select(g => new { Name = g.First().Acceptor, Count = g.Count(), Value = g.Sum(r => r.Contract.Price ?? 0), Last = g.Max(r => r.Contract.Accepted) }).OrderByDescending(r => r.Count).ToArray();
    }
    private void History_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is ContractRow row) OpenContents(row);
    }
    private void Search_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (PilotCombo.SelectedItem is not EvePilotProfile pilot) { StatusText.Text = "Choose a linked character first."; return; }
        if (!ContractService.CanRead(pilot)) { StatusText.Text = "Use RECONNECT / ADD to grant corporation contract access."; return; }
        try
        {
            await _operations.Access.ValidateAsync(_pilots, _lifetime.Token);
            if (!_operations.Access.CanReadContracts || pilot.CharacterId != _operations.Access.State.ContractCharacterId) return;
            if (Service.State.CharacterId != pilot.CharacterId)
            {
                Service.State.CharacterId = pilot.CharacterId;
                Service.State.Rows.Clear();
                Service.State.CorporationName = "";
                Service.State.LastRefreshUtc = null;
                Service.Save();
            }
            // Refresh belongs to the app, so closing this window does not cancel monitoring.
            await Service.RefreshAsync(pilot, CancellationToken.None);
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex.Message; }
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ReconnectButton.IsEnabled = false;
        try
        {
            new ClientSetupWindow().ShowDialog();
            await LoadPilotsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex.Message; }
        finally { if (!_closed) ReconnectButton.IsEnabled = true; }
    }
    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(RateBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0 || rate > 100 ||
            !decimal.TryParse(ToleranceBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var tolerance) || tolerance < 0 || tolerance > 100)
        { StatusText.Text = "Enter a buy percentage above 0 and at most 100, and tolerance from 0 to 100."; return; }
        Service.State.BuyPercent = rate; Service.State.TolerancePercent = tolerance;
        try { Service.Save(); StatusText.Text = "Rules saved. Refresh to re-evaluate contracts."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Alerts_Click(object sender, RoutedEventArgs e)
    {
        Service.State.NotificationsEnabled = AlertsCheck.IsChecked == true;
        try { Service.Save(); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Janice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContractRow row) return;
        if (row.JaniceUrl == null) { StatusText.Text = "This contract has no recognized Janice appraisal link."; return; }
        try { Process.Start(new ProcessStartInfo(row.JaniceUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Game_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContractRow row) return;
        try
        {
            var pilot = (await _operations.Sso.LoadPilotsAsync()).FirstOrDefault(p => p.CharacterId == (row.ReaderCharacterId > 0 ? row.ReaderCharacterId : Service.State.CharacterId))
                ?? throw new InvalidOperationException("Choose and refresh a corporation-data toon first.");
            await Service.OpenInGameAsync(row.Contract.Id, pilot, _lifetime.Token);
            StatusText.Text = "Contract window requested for " + pilot.CharacterName + ". That character must be logged into EVE.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(ContractsGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row?.Item is ContractRow contract) OpenContents(contract);
    }
    public void OpenContents(ContractRow row) => new ContractContentsWindow(Service, _operations.Sso, row, row.ReaderCharacterId > 0 ? row.ReaderCharacterId : Service.State.CharacterId) { Owner = this }.Show();
}
