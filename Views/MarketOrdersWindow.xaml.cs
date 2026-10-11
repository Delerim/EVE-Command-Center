using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class MarketOrdersWindow : Window
{
    private readonly CancellationTokenSource _life = new();
    private MarketOrdersService? _service;
    private IReadOnlyList<EvePilotProfile> _pilots = Array.Empty<EvePilotProfile>();
    private List<CharacterChoice> _choices = new();
    public MarketOrdersWindow()
    {
        InitializeComponent();
        _progressTimer.Tick += (_, _) => ShowProgress();
        TableSorting.Attach(Orders); TableSorting.Attach(TransactionsGrid);
        Loaded += async (_, _) =>
        {
            if (_service != null) return;
            try
            {
                var ops = BackgroundOperations.Current;
                Initialize(ops.MarketOrders, (await ops.Sso.LoadPilotsAsync()).ToArray());
            }
            catch (Exception ex) { Status.Text = "Market unavailable: " + ex.Message; }
        };
        Closed += (_, _) => { _life.Cancel(); _progressTimer.Stop(); };
    }
    internal void Initialize(MarketOrdersService service, IReadOnlyList<EvePilotProfile> pilots)
    {
        _service = service; _pilots = pilots;
        _choices = pilots.Select(p => new CharacterChoice { Id = p.CharacterId, Name = p.CharacterName, Selected = service.State.SelectedCharacters.Contains(p.CharacterId) }).ToList();
        Characters.ItemsSource = _choices; RenderOrders();
    }
    private CancellationTokenSource? _refresh;
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshMarket(0);
    private async void History_Click(object sender, RoutedEventArgs e) => await RefreshMarket(3);
    private async void Transactions_Click(object sender, RoutedEventArgs e) => await RefreshMarket(4);
    private void CancelRefresh_Click(object sender, RoutedEventArgs e) => _refresh?.Cancel();
    private void ShowProgress()
    {
        if (_refresh == null) return;
        Status.Text = _refresh.IsCancellationRequested ? "Cancelling refresh; saved data is retained..." : _service?.Busy == true ? _service.Progress : "Preparing refresh...";
        HistoryStatus.Text = "Refresh in progress. EVE rate limits may add waiting time.";
    }
    internal async Task RefreshMarket(int mode, IReadOnlyList<EvePilotProfile>? linkedPilots = null)
    {
        if (_service == null || _service.Busy || _refresh != null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _refresh = request;
        string? failure = null;
        TransactionsButton.IsEnabled = HistoryButton.IsEnabled = RefreshButton.IsEnabled = false;
        CancelRefresh.Visibility = Visibility.Visible;
        _progressTimer.Start();
        try
        {
            ShowProgress();
            _pilots = linkedPilots ?? (await BackgroundOperations.Current.Sso.LoadPilotsAsync()).ToArray();
            var ids = _choices.Where(c => c.Selected).Select(c => c.Id).ToHashSet();
            _choices = _pilots.Select(p => new CharacterChoice { Id = p.CharacterId, Name = p.CharacterName, Selected = ids.Contains(p.CharacterId) }).ToList();
            Characters.ItemsSource = _choices; _service.Select(ids);
            if (mode == 3) await _service.RefreshHistoryAsync(_pilots, request.Token);
            else if (mode == 4) await _service.RefreshTransactionsAsync(_pilots, request.Token);
            else await _service.RefreshAsync(_pilots, request.Token);
            Observation.SelectedIndex = mode;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = MarketOrdersService.Failure(ex); }
        finally
        {
            _progressTimer.Stop(); _refresh = null;
            TransactionsButton.IsEnabled = HistoryButton.IsEnabled = RefreshButton.IsEnabled = true;
            CancelRefresh.Visibility = Visibility.Collapsed;
            RenderOrders();
            if (failure != null) Status.Text = failure;
            else if (request.IsCancellationRequested) Status.Text = "Refresh cancelled. Saved data is retained; you can refresh again.";
        }
    }
    private void Transaction_Selected(object sender, SelectionChangedEventArgs e)
    { if (OrderDetail != null) OrderDetail.Text = (TransactionsGrid.SelectedItem as TransactionRow)?.Detail ?? "Select a transaction to inspect its confirmed purchase or sale."; }
    private void RenderTransactions(bool buy, string query)
    {
        if (_service == null) return;
        var rows = _service.Transactions.Where(r => _service.State.SelectedCharacters.Contains(r.OwnerId))
            .Select(r => new TransactionRow(r.Transaction, _pilots.FirstOrDefault(p => p.CharacterId == r.OwnerId)?.CharacterName ?? "Unlinked pilot", r.Observed))
            .Where(r => query.Length == 0 || (r.Transaction.Name + " " + r.Owner + " " + r.Transaction.Location).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selection = TransactionsGrid.SelectedItem as TransactionRow;
        TransactionsGrid.ItemsSource = rows.Where(r => r.Transaction.Buy == buy).ToArray();
        if (selection != null) TransactionsGrid.SelectedItem = TransactionsGrid.Items.Cast<TransactionRow>().FirstOrDefault(r => r.Owner == selection.Owner && r.Transaction.Id == selection.Transaction.Id);
        Totals.Text = $"IMPORTED TRANSACTIONS | Purchases {rows.Where(r => r.Transaction.Buy).Sum(r => r.Transaction.Total):N2} ISK | Sales {rows.Where(r => !r.Transaction.Buy).Sum(r => r.Transaction.Total):N2} ISK | Gross amounts, not profit";
        HistoryStatus.Text = _service.TransactionStatus;
        Status.Text = _service.Error;
        ObservationSummary.Text = $"{TransactionsGrid.Items.Count:N0} {(buy ? "purchases" : "sales")} shown | Selected pilots and search applied. Imported history may be incomplete; fees and project allocations are not included.";
        OrderDetail.Text = (TransactionsGrid.SelectedItem as TransactionRow)?.Detail ?? "Select a transaction to inspect its confirmed purchase or sale.";
    }
    private sealed record TransactionRow(MarketTransaction Transaction, string Owner, DateTimeOffset Observed)
    {
        public string Color => Transaction.Buy ? "#EDAD83" : "#76D7CB";
        public string Detail => $"{Transaction.Side}: {Transaction.Quantity:N0} {Transaction.Name}, {Transaction.Total:N2} ISK at {Transaction.Location}. Confirmed by the wallet feed, imported {Observed:dd MMM HH:mm} UTC. Project and order matching have not been assigned.";
    }
    private void Side_Changed(object sender, SelectionChangedEventArgs e) { if (e.Source == SideTabs) RenderOrders(); }
    private void Order_Selected(object sender, SelectionChangedEventArgs e)
    { if (OrderDetail != null) OrderDetail.Text = (Orders.SelectedItem as OrderRow)?.Detail ?? "Select an order to inspect its last observation."; }
    private void Observation_Changed(object sender, SelectionChangedEventArgs e) => RenderOrders();
    private void Search_Changed(object sender, TextChangedEventArgs e) => RenderOrders();
    private void RenderOrders()
    {
        if (_service == null) return;
        bool buy = (SideTabs.SelectedItem as TabItem)?.Tag?.ToString() != "sell";
        var snapshots = _service.State.Characters.Where(c => _service.State.SelectedCharacters.Contains(c.CharacterId)).ToArray();
        string query = Search.Text.Trim();
        string Fresh(MarketCharacterSnapshot c) => c.Updated == default ? "Not refreshed" :
            $"{c.Updated:dd MMM HH:mm} UTC" + (c.Error.Length > 0 || c.Updated < DateTimeOffset.UtcNow.AddMinutes(-20) ? " / REVIEW" : " / cached");
        var history = _service.History.Where(h => _service.State.SelectedCharacters.Contains(h.OwnerId)).ToArray();
        var confirmed = history.Select(h => new OrderRow(h.Order, _pilots.FirstOrDefault(p => p.CharacterId == h.OwnerId)?.CharacterName ?? snapshots.FirstOrDefault(c => c.CharacterId == h.OwnerId)?.Name ?? "Unlinked pilot", "Confirmed history", h.Order.State == "cancelled" ? "Cancelled" : "Expired", h.Observed, null)).ToArray();
        bool Historical(long owner, long order) => history.Any(h => h.OwnerId == owner && h.Order.OrderId == order);
        HistoryStatus.Text = _service.HistoryStatus;
        var all = snapshots.SelectMany(c => c.Orders.Where(o => !o.Corporation && !Historical(c.CharacterId, o.OrderId)).Select(o => new OrderRow(o, c.Name, Fresh(c), "Open (last snapshot)", c.Updated, null))).ToArray();
        var missing = snapshots.SelectMany(c => c.Unobserved.Where(o => !Historical(c.CharacterId, o.Order.OrderId)).Select(o => new OrderRow(o.Order, c.Name, Fresh(c), "Not observed", o.LastSeen, o.MissingSince))).ToArray();
        string mode = (Observation.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "open";
        bool transactions = mode == "transactions";
        TransactionsGrid.Visibility = transactions ? Visibility.Visible : Visibility.Collapsed;
        Orders.Visibility = transactions ? Visibility.Collapsed : Visibility.Visible;
        ((TabItem)SideTabs.Items[0]).Header = transactions ? "Purchases" : "Buy orders";
        ((TabItem)SideTabs.Items[1]).Header = transactions ? "Sales" : "Sell orders";
        if (transactions) { RenderTransactions(buy, query); ShowProgress(); return; }
        var shown = mode == "history" ? confirmed : mode == "missing" ? missing : mode == "all" ? all.Concat(missing).Concat(confirmed).ToArray() : all;
        var selection = Orders.SelectedItem as OrderRow;
        Orders.ItemsSource = shown.Where(r => r.Order.Buy == buy && (query.Length == 0 || (r.Order.Name + " " + r.Owner + " " + r.Order.Location).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selection != null) Orders.SelectedItem = Orders.Items.Cast<OrderRow>().FirstOrDefault(r => r.Order.OrderId == selection.Order.OrderId && r.Owner == selection.Owner);
        ObservationSummary.Text = $"{Orders.Items.Count:N0} shown | {confirmed.Length:N0} confirmed historical orders | {missing.Length:N0} orders no longer observed across selected pilots. Last quantities are retained; disappearance does not confirm a sale, fill or cancellation.";
        bool incomplete = snapshots.Length != _service.State.SelectedCharacters.Count || snapshots.Any(c => c.Error.Length > 0 || c.Updated == default || c.Updated < DateTimeOffset.UtcNow.AddMinutes(-20));
        Totals.Text = _service.State.SelectedCharacters.Count == 0 ? "Choose characters above, then save selection / refresh." :
            $"{(incomplete ? "OPEN ORDERS / PARTIAL CACHE" : "CACHED OPEN ORDERS")} | Buy {all.Count(r => r.Order.Buy):N0}: {all.Where(r => r.Order.Buy).Sum(r => r.Order.RemainingValue):N2} ISK | Sell {all.Count(r => !r.Order.Buy):N0}: {all.Where(r => !r.Order.Buy).Sum(r => r.Order.RemainingValue):N2} ISK";
        Status.Text = _service.Error.Length > 0 ? _service.Error : string.Join("\n", snapshots.Select(c =>
            $"{c.Name}: {Fresh(c)} | {(c.Error.Length > 0 ? MarketOrdersService.DisplayError(c.Error) : $"Next refresh after {c.NextRefresh:HH:mm} UTC")}"));
        ShowProgress();
    }
    private sealed class CharacterChoice { public long Id { get; set; } public string Name { get; set; } = ""; public bool Selected { get; set; } }
    private sealed record OrderRow(MarketOrder Order, string Owner, string Freshness, string State, DateTimeOffset LastSeen, DateTimeOffset? MissingSince)
    {
        public string Color => Order is HistoricalMarketOrder ? "#A5BBC1" : MissingSince.HasValue ? "#E3BC70" : Freshness.Contains("REVIEW") ? "#9DB6BD" : "#76D7CB";
        public string SeenLabel => LastSeen.ToString("dd MMM HH:mm 'UTC'");
        public string Detail => Order is HistoricalMarketOrder ? $"{State}, confirmed by ESI order history on {SeenLabel}. Remaining quantities and price come from that record. This is not a wallet transaction and does not establish spending or profit." : MissingSince.HasValue ? $"Last returned {SeenLabel}; absent from a successful refresh on {MissingSince:dd MMM HH:mm} UTC. Price and quantities are the last observation. Final state needs order history; no fill or spending is inferred." : $"Last returned {SeenLabel}. Quantities reflect this snapshot, not confirmed transactions.";
    }
}
